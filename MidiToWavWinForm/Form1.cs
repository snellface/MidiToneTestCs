using System;
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
using System.Buffers.Binary;

namespace MidiToWavWinForm
{
	public static class BinaryReaderExtensions
	{
		public static int ReadVariableLengthValue(this BinaryReader reader)
		{
			int value = 0;
			for(int i = 0; i < 4; i++)
			{
				byte data = reader.ReadByte();
				byte dataWithoutMarkerBit = (byte)(data & 0b01111111);
				value += dataWithoutMarkerBit; // Mask away any "more data" bits, shift in this masked data

				if ((data & 0b10000000) == 0)
					break;

				value = value << 7;
			}
			return value;
		}
	}

	public partial class Form1 : Form
	{
		public Form1()
		{
			InitializeComponent();

			var waveStream = new MemoryStream();
			var waveWriter = new BinaryWriter(waveStream);

			// From http://subsynth.sourceforge.net/midinote2freq.html and corrected via formula from https://en.wikipedia.org/wiki/MIDI_tuning_standard
			double[] midiFrequency = Enumerable.Range(0, 127).Select(x => 440.0 * Math.Pow(2, ((x - 69.0) / 12.0))).ToArray();
			ushort[] midiFrequencyVolumes = new ushort[midiFrequency.Length];

			using (FileStream fs = new FileStream("Twinkle.mid", FileMode.Open))
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
					short ticksPerQuarterNote = BinaryPrimitives.ReverseEndianness(reader.ReadInt16()); // aka time division

					int microsecondsPerBeat = 500000;
					int microsecondsPerOffset = microsecondsPerBeat / ticksPerQuarterNote;

					int headerRead = 6;
					while (headerRead < headerSize)
						reader.ReadByte();

					int trackIndex = 0;
					if (fileType == 1)
					{
						// read first metadata track
						trackIndex = 1;
					}
					// Read "normal" tracks
					for (; trackIndex < trackCount; trackIndex++)
					{
						int MTrk = BinaryPrimitives.ReverseEndianness(reader.ReadInt32()); //0x4d54726b
						if (MTrk != 0x4d54726b)
							throw new Exception("Invalid midi track header");

						int bytesInTrack = BinaryPrimitives.ReverseEndianness(reader.ReadInt32());
						bool runningStatus = false;
						long runningOffset = 0;
						long lastRunningOffset = 0;
						while (true)
						{
							int offset = reader.ReadVariableLengthValue();
							runningOffset += offset;
							if(offset != 0)
							{
								// Add sounds to wave buffer
								for(int i = 0; i < midiFrequencyVolumes.Length; i++)
								{
									int offsetAsMs = (microsecondsPerOffset * offset) / 1000;
									if (midiFrequencyVolumes.Count(v => v > 0) > 1)
										throw new Exception("Mixing sounds not yet supported.");
									if(midiFrequencyVolumes[i] > 0)
									{
										AddWaveData(waveWriter, (ushort)midiFrequency[i], offsetAsMs, midiFrequencyVolumes[i]);
										break;
									}
								}
							}

							byte operationByte = reader.ReadByte();
							if(operationByte == 0xff)
							{
								// I dont think meta events cancels runningStatus.
								// Meta event
								byte type = reader.ReadByte();
								int length = reader.ReadVariableLengthValue();
								for (int i = 0; i < length; i++)
									reader.ReadByte();

								if (type == 0x51)
								{
									// Set tempo meta message

									if (length != 3)
										throw new Exception("Unexpected length of Set Tempo message.");

									byte[] tempo = reader.ReadBytes(3); // 24 bit value, MSB first as usual?
									throw new Exception("Demo track did not have this message, assume that this implementation is WRONG");
									microsecondsPerBeat = tempo[0] + (tempo[1] << 8) + (tempo[2] << 16);
									microsecondsPerOffset = microsecondsPerBeat / ticksPerQuarterNote;
								}

								if(type == 0x2F)
								{
									// End of track
									break;
								}

								continue;
							}

							if (operationByte == 0xf0 || operationByte == 0xf7)
							{
								runningStatus = false;
								// System Exclusive Event
								int length = reader.ReadVariableLengthValue();
								for (int i = 0; i < length; i++)
									reader.ReadByte();
								continue;
							}

							byte eventType = (byte)(operationByte & 0xF0);
							byte midiChannel = (byte)(operationByte & 0x0F);
							byte param1 = reader.ReadByte();
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
										midiFrequencyVolumes[param1] = 16383;
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

			PlayWaveData(waveStream.ToArray());

			waveStream.Close();
			waveWriter.Close();
			waveStream.Dispose();
			waveWriter.Dispose();
		}

		private void AddWaveData(BinaryWriter writer, UInt16 frequency, int msDuration, UInt16 volume = 16383)
		{
			const double TAU = 2 * Math.PI;
			const int samplesPerSecond = 44100;
			int samples = (int)((decimal)samplesPerSecond * msDuration / 1000);


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
	}
}
