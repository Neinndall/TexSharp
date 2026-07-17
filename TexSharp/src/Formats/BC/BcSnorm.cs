using System;

namespace TexSharp.Formats.BC
{
    public static class Bc4SnormBlock
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 8 || rgbaOutput.Length < 16) return;

            Span<byte> values = stackalloc byte[16];
            DecodeSignedChannel(data, values);
            for (int i = 0; i < 16; i++)
                rgbaOutput[i] = values[i] | 0xFF000000u;
        }

        internal static void DecodeSignedChannel(ReadOnlySpan<byte> data, Span<byte> output)
        {
            int endpoint0 = Math.Max((int)unchecked((sbyte)data[0]), -127);
            int endpoint1 = Math.Max((int)unchecked((sbyte)data[1]), -127);
            Span<byte> table = stackalloc byte[8];
            table[0] = MapToByte(endpoint0, 1);
            table[1] = MapToByte(endpoint1, 1);

            if (endpoint0 > endpoint1)
            {
                for (int i = 1; i <= 6; i++)
                    table[i + 1] = MapToByte((7 - i) * endpoint0 + i * endpoint1, 7);
            }
            else
            {
                for (int i = 1; i <= 4; i++)
                    table[i + 1] = MapToByte((5 - i) * endpoint0 + i * endpoint1, 5);
                table[6] = 0;
                table[7] = 255;
            }

            ulong indices = 0;
            for (int i = 0; i < 6; i++)
                indices |= (ulong)data[i + 2] << (i * 8);

            for (int i = 0; i < 16; i++)
            {
                output[i] = table[(int)((indices >> (i * 3)) & 7)];
            }
        }

        private static byte MapToByte(int numerator, int denominator)
        {
            int byteDenominator = denominator * 254;
            int nonNegativeNumerator = numerator + denominator * 127;
            return (byte)((nonNegativeNumerator * 255 + byteDenominator / 2) / byteDenominator);
        }
    }

    public static class Bc5SnormBlock
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 16 || rgbaOutput.Length < 16) return;

            Span<byte> reds = stackalloc byte[16];
            Span<byte> greens = stackalloc byte[16];
            Bc4SnormBlock.DecodeSignedChannel(data[..8], reds);
            Bc4SnormBlock.DecodeSignedChannel(data[8..16], greens);

            for (int i = 0; i < 16; i++)
                rgbaOutput[i] = reds[i] | ((uint)greens[i] << 8) | 0xFF000000u;
        }
    }
}
