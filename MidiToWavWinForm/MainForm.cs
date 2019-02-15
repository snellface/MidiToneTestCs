using MidiToWavWinForm;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MidiToWavWinForm
{
	public partial class MainForm : Form
	{
		public MainForm()
		{
			InitializeComponent();
		}

		private void RunTest()
		{
			var waveStream = new MemoryStream();
			var waveWriter = new BinaryWriter(waveStream);

			// From http://subsynth.sourceforge.net/midinote2freq.html and corrected via formula from https://en.wikipedia.org/wiki/MIDI_tuning_standard
			double[] midiFrequency = Enumerable.Range(0, 127).Select(x => 440.0 * Math.Pow(2, ((x - 69.0) / 12.0))).ToArray();
			ushort[] midiFrequencyVolumes = new ushort[midiFrequency.Length];

			//var samples1 = GenerateWaveSamples(440, 2000);
			//var samples2 = GenerateWaveSamples(540, 2000);
			//var compositeSamples = samples1.Select((v, i) => (v + samples2[i])).ToArray();

			//const int samplesPerPixel = 10;
			//Bitmap bmp = new Bitmap(samples1.Length / samplesPerPixel, 600);
			//Graphics g = Graphics.FromImage(bmp);
			//g.Clear(Color.White);

			//double maxValue = samples1.Max(s => Math.Abs(s));
			//maxValue = Math.Max(maxValue, samples2.Max(s => Math.Abs(s)));
			//maxValue = Math.Max(maxValue, compositeSamples.Max(s => Math.Abs(s)));

			//for (int i = 0; i < samples1.Length; i++)
			//{
			//	waveWriter.Write(samples1[i]);
			//}
			//for (int i = 0; i < samples1.Length; i++)
			//{
			//	waveWriter.Write(samples2[i]);
			//}

			//for (int i = 0; i < samples1.Length; i++)
			//{
			//	int y = 100 - (int)(samples1[i] / maxValue * 100.0);
			//	g.FillRectangle(Brushes.Red, i / samplesPerPixel, y, 1, 1);

			//	y = 100 - (int)(samples2[i] / maxValue * 100.0);
			//	g.FillRectangle(Brushes.Green, i / samplesPerPixel, y + 200, 1, 1);

			//	y = 100 - (int)(compositeSamples[i] / maxValue * 100.0);
			//	g.FillRectangle(Brushes.Black, i / samplesPerPixel, y + 400, 1, 1);

			//	waveWriter.Write(compositeSamples[i]);
			//}

			//bmp.Save("Waveform.bmp");
			//g.Dispose();
			//bmp.Dispose();


			const bool LoadMidi = true;
			if (LoadMidi)
			{
				using (FileStream fs = new FileStream("MIDIs/Piano.mid", FileMode.Open))
				{
					using (BinaryReader reader = new BinaryReader(fs))
					{
						// Read MIDI header
						// Skip MThd string
						int MThd = BinaryPrimitives.ReverseEndianness(reader.ReadInt32()); //0x4d546864
						if (MThd != 0x4d546864)
							throw new Exception("Invalid midi file header");

						int headerSize = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
						short fileType = BinaryPrimitives.ReverseEndianness(reader.ReadInt16());
						short trackCount = BinaryPrimitives.ReverseEndianness(reader.ReadInt16());

						byte ticksPerQuarterNoteMsb = reader.ReadByte();
						byte ticksPerQuarterNoteLsb = reader.ReadByte();
						short ticksPerQuarterNote = (short)((ticksPerQuarterNoteLsb) + (ticksPerQuarterNoteMsb << 8));

						int microsecondsPerBeat = 500000;
						int microsecondsPerOffset = microsecondsPerBeat / ticksPerQuarterNote;

						int headerRead = 6;
						while (headerRead < headerSize)
							reader.ReadByte();

						for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
						{
							int MTrk = BinaryPrimitives.ReverseEndianness(reader.ReadInt32()); //0x4d54726b
							if (MTrk != 0x4d54726b)
								throw new Exception("Invalid midi track header");

							int bytesInTrack = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
							long runningOffset = 0;
							byte runningStatus = 0;

							int stepContinuation = 0;
							while (true)
							{
								// Begin parsing a message
								int offset = reader.ReadVariableLengthValue();
								runningOffset += offset;
								if (offset != 0)
								{
									int offsetAsMs = (microsecondsPerOffset * offset) / 1000;
									var samplesList = new List<short[]>();
									// Add sounds to wave buffer
									int thisStepContinuation = 0;
									for (int i = 0; i < midiFrequencyVolumes.Length; i++)
									{
										if (midiFrequencyVolumes[i] > 0)
										{
											thisStepContinuation = GenerateWaveSamples(out short[] waveData, (ushort)midiFrequency[i], offsetAsMs, midiFrequencyVolumes[i], stepContinuation: stepContinuation);
											samplesList.Add(waveData);
										}
									}

									if (samplesList.Select(l => l.Length).Distinct().Count() > 1)
										throw new Exception("One or more of the sample lists to be mixed did not have the same lengths");


									if (samplesList.Any())
									{
										int count = samplesList[0].Length;
										for (int i = 0; i < count; i++)
										{
											int sample = samplesList.Sum(s => s[i]);
											if (sample > short.MaxValue)
												sample = short.MaxValue;
											else if (sample < short.MinValue)
												sample = short.MinValue;

											waveWriter.Write((short)sample);
										}
									}
									else
									{
										thisStepContinuation = AddWaveData(waveWriter, 440, offsetAsMs, 0, stepContinuation);
									}

									stepContinuation = thisStepContinuation;
								}

								byte operationByte = reader.ReadByte();
								if (operationByte == 0xff)
								{
									// I bot sure if running status i cancelled by meta data messages..
									runningStatus = 0;
									// Meta event
									byte type = reader.ReadByte();
									int length = reader.ReadVariableLengthValue();


									if (type == 0x2F) // End of track
										break;

									switch (type)
									{
										case 0x51:
											// Set tempo meta message
											if (length < 3)
												throw new Exception("Unexpected length of Set Tempo message.");

											byte[] tempo = reader.ReadBytes(3); // 24 bit value, MSB first as usual?

											microsecondsPerBeat = (tempo[0] << 16) + (tempo[1] << 8) + (tempo[2] << 0);
											microsecondsPerOffset = microsecondsPerBeat / ticksPerQuarterNote;

											if (length > 3)
												reader.ReadBytes(length - 3); // Throw away any bytes after the first 3 since those are the only ones that should be there.
											break;
										case 0x2F:
											// End of track
											// Handled before the switch.
											break;
										default:
											for (int i = 0; i < length; i++)
												reader.ReadByte();
											break;
									}

									continue;
								}

								if (operationByte == 0xf0 || operationByte == 0xf7)
								{
									runningStatus = 0;
									// System Exclusive Event
									int length = reader.ReadVariableLengthValue();
									for (int i = 0; i < length; i++)
										reader.ReadByte();
									continue;
								}

								// It should now be safe to assume that this is a midi message.
								// Check if we should update the running status

								bool runningStatusWasUsed = runningStatus != 0;
								if ((operationByte & 0x80) != 0) // Check if status byte is set, if so, update running status
								{
									runningStatusWasUsed = false;
									runningStatus = operationByte;
								}

								byte eventType = (byte)(runningStatus & 0xF0);
								byte midiChannel = (byte)(runningStatus & 0x0F);

								runningStatus = operationByte;

								if (eventType == 0xC0 || eventType == 0xE0)
								{
									// One param only
									byte param = reader.ReadByte();
								}
								else if(eventType == 0xF0)
								{
									throw new Exception("System Common Messages and System Real-Time Messages parsing not yet implemented");
								}
								else
								{
									byte param1 = 0;
									if (runningStatusWasUsed)
										param1 = operationByte;
									else
										param1 = reader.ReadByte();
									byte param2 = reader.ReadByte();

									switch (eventType)
									{
										case 0x80:
											// Note Off
											System.Diagnostics.Debug.WriteLine($"@ {runningOffset}: Off note: {param1}");
											midiFrequencyVolumes[param1] = 0;
											break;
										case 0x90:
											// Note On
											if (param2 != 0)
											{
												System.Diagnostics.Debug.WriteLine($"@ {runningOffset}: On note: {param1}");
												midiFrequencyVolumes[param1] = (ushort)(16383.0 * (param2 / 40.0));
											}
											else
											{
												System.Diagnostics.Debug.WriteLine($"@ {runningOffset}: Off note: {param1}");
												midiFrequencyVolumes[param1] = 0;
											}
											break;
										case 0xA0:
											// Note Aftertouch
											break;
										case 0xB0:
											// Note Controller
											break;
									}
								}
							}
						}
					}
				}
			}

			PlayWaveData(waveStream.ToArray());

			waveStream.Close();
			waveWriter.Close();
			waveStream.Dispose();
			waveWriter.Dispose();
		}

		private void AddWaveData(BinaryWriter writer, short[] samples)
		{
			foreach (var s in samples)
				writer.Write(s);
		}

		private int AddWaveData(BinaryWriter writer, ushort frequency, int msDuration, ushort volume = 8191, int stepContinuation = 0)
		{
			stepContinuation = GenerateWaveSamples(out short[] waveData, frequency, msDuration, volume, stepContinuation);
			AddWaveData(writer, waveData);
			return stepContinuation;
		}

		private int GenerateWaveSamples(out short[] waveData, ushort frequency, int msDuration, ushort volume = 16383, int stepContinuation = 0)
		{
			const double TAU = 2 * Math.PI;
			const int samplesPerSecond = 44100;
			int samples = (int)((decimal)samplesPerSecond * msDuration / 1000);
			waveData = new short[samples];

			double theta = frequency * TAU / (double)samplesPerSecond;
			// 'volume' is UInt16 with range 0 thru Uint16.MaxValue ( = 65 535)
			// we need 'amp' to have the range of 0 thru Int16.MaxValue ( = 32 767)
			double amp = volume >> 1; // so we simply set amp = volume / 2
			int step;
			for (step = 0; step < samples; step++)
			{
				short s = (short)(amp * Math.Sin(theta * (double)(step + stepContinuation)));
				waveData[step] = s;

				amp *= 0.9999;
			}

			return step + stepContinuation;
		}

		private byte[] CreateWaveFile(byte[] data)
		{
			using (var stream = new MemoryStream())
			{
				using (BinaryWriter writer = new BinaryWriter(stream))
				{
					const int formatChunkSize = 16;
					const int headerSize = 8;
					const short formatType = 1;
					const short tracks = 1;
					const int samplesPerSecond = 44100;
					const short bitsPerSample = 16;
					short frameSize = (short)(tracks * ((bitsPerSample + 7) / 8));
					int bytesPerSecond = samplesPerSecond * frameSize;
					const int waveSize = 4;
					int samples = data.Length;
					int dataChunkSize = samples * frameSize;
					int fileSize = waveSize + headerSize + formatChunkSize + headerSize + dataChunkSize;

					// var encoding = new System.Text.UTF8Encoding();
					writer.Write(0x46464952); // = encoding.GetBytes("RIFF")
					writer.Write(fileSize);
					writer.Write(0x45564157); // = encoding.GetBytes("WAVE")
					writer.Write(0x20746D66); // = encoding.GetBytes("fmt ")
					writer.Write(formatChunkSize);
					writer.Write(formatType);
					writer.Write(tracks);
					writer.Write(samplesPerSecond);
					writer.Write(bytesPerSecond);
					writer.Write(frameSize);
					writer.Write(bitsPerSample);
					writer.Write(0x61746164); // = encoding.GetBytes("data")
					writer.Write(dataChunkSize);

					writer.Write(data);

					return stream.ToArray();
				}
			}
		}

		private void PlayWaveData(byte[] data)
		{
			using (var stream = new MemoryStream())
			{
				using (BinaryWriter writer = new BinaryWriter(stream))
				{
					const int formatChunkSize = 16;
					const int headerSize = 8;
					const short formatType = 1;
					const short tracks = 1;
					const int samplesPerSecond = 44100;
					const short bitsPerSample = 16;
					short frameSize = (short)(tracks * ((bitsPerSample + 7) / 8));
					int bytesPerSecond = samplesPerSecond * frameSize;
					const int waveSize = 4;
					int samples = data.Length / 2; // Since bitsPerSample == 16
					int dataChunkSize = samples * frameSize;
					int fileSize = waveSize + headerSize + formatChunkSize + headerSize + dataChunkSize;

					// var encoding = new System.Text.UTF8Encoding();
					writer.Write(0x46464952); // = encoding.GetBytes("RIFF")
					writer.Write(fileSize);
					writer.Write(0x45564157); // = encoding.GetBytes("WAVE")
					writer.Write(0x20746D66); // = encoding.GetBytes("fmt ")
					writer.Write(formatChunkSize);
					writer.Write(formatType);
					writer.Write(tracks);
					writer.Write(samplesPerSecond);
					writer.Write(bytesPerSecond);
					writer.Write(frameSize);
					writer.Write(bitsPerSample);
					writer.Write(0x61746164); // = encoding.GetBytes("data")
					writer.Write(dataChunkSize);

					writer.Write(data);
					stream.Seek(0, SeekOrigin.Begin);
					new SoundPlayer(stream).Play();
				}
			}
		}



		private void PlayWaveBeep(UInt16 frequency, int msDuration, UInt16 volume = 16383)
		{
			using (var stream = new MemoryStream())
			{
				using (BinaryWriter writer = new BinaryWriter(stream))
				{
					const double TAU = 2 * Math.PI;
					int formatChunkSize = 16;
					int headerSize = 8;
					short formatType = 1;
					short tracks = 1;
					int samplesPerSecond = 44100;
					short bitsPerSample = 16;
					short frameSize = (short)(tracks * ((bitsPerSample + 7) / 8));
					int bytesPerSecond = samplesPerSecond * frameSize;
					int waveSize = 4;
					int samples = (int)((decimal)samplesPerSecond * msDuration / 1000);
					int dataChunkSize = samples * frameSize;
					int fileSize = waveSize + headerSize + formatChunkSize + headerSize + dataChunkSize;
					// var encoding = new System.Text.UTF8Encoding();
					writer.Write(0x46464952); // = encoding.GetBytes("RIFF")
					writer.Write(fileSize);
					writer.Write(0x45564157); // = encoding.GetBytes("WAVE")
					writer.Write(0x20746D66); // = encoding.GetBytes("fmt ")
					writer.Write(formatChunkSize);
					writer.Write(formatType);
					writer.Write(tracks);
					writer.Write(samplesPerSecond);
					writer.Write(bytesPerSecond);
					writer.Write(frameSize);
					writer.Write(bitsPerSample);
					writer.Write(0x61746164); // = encoding.GetBytes("data")
					writer.Write(dataChunkSize);
					{
						double theta = frequency * TAU / (double)samplesPerSecond;
						// 'volume' is UInt16 with range 0 thru Uint16.MaxValue ( = 65 535)
						// we need 'amp' to have the range of 0 thru Int16.MaxValue ( = 32 767)
						double amp = volume >> 2; // so we simply set amp = volume / 2
						for (int step = 0; step < samples; step++)
						{
							short s = (short)(amp * Math.Sin(theta * (double)step));
							writer.Write(s);
						}
					}

					stream.Seek(0, SeekOrigin.Begin);
					new SoundPlayer(stream).Play();
				}
			}
		}

		private void timer1_Tick(object sender, EventArgs e)
		{
			// Use a timer to allow missing DLL exeptions to be shown. Application wont start enough to show message boxes otherwise..
			timer1.Stop();
			try
			{
				RunTest();
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Something bad happened: {ex}");
			}
		}
	}
}
