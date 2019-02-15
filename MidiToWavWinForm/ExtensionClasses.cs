using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MidiToWavWinForm
{
	public static class BinaryReaderExtensions
	{
		public static int ReadVariableLengthValue(this BinaryReader reader)
		{
			int value = 0;
			for (int i = 0; i < 4; i++)
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
}
