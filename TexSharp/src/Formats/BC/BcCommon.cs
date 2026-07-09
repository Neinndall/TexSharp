using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Utilidades compartidas por los decodificadores BC (factor común de color/alpha).
    /// </summary>
    internal static class BcCommon
    {
        public static uint Rgb565ToRgba8(ushort color)
        {
            int r = (color >> 11) & 0x1F;
            int g = (color >> 5) & 0x3F;
            int b = color & 0x1F;
            r = (r << 3) | (r >> 2);
            g = (g << 2) | (g >> 4);
            b = (b << 3) | (b >> 2);
            return (uint)(r | (g << 8) | (b << 16) | (0xFF << 24));
        }

        public static uint InterpolateRgb(uint c0, uint c1, int w0, int w1)
        {
            int r0 = (int)(c0 & 0xFF);
            int r1 = (int)(c1 & 0xFF);
            int g0 = (int)((c0 >> 8) & 0xFF);
            int g1 = (int)((c1 >> 8) & 0xFF);
            int b0 = (int)((c0 >> 16) & 0xFF);
            int b1 = (int)((c1 >> 16) & 0xFF);

            if (w0 == 1 && w1 == 1)
                return (uint)(((r0 + r1) >> 1) | (((g0 + g1) >> 1) << 8) | (((b0 + b1) >> 1) << 16) | (0xFF << 24));

            uint r = (uint)(r0 * w0 + r1 * w1) * 21846u >> 16;
            uint g = (uint)(g0 * w0 + g1 * w1) * 21846u >> 16;
            uint b = (uint)(b0 * w0 + b1 * w1) * 21846u >> 16;
            return r | (g << 8) | (b << 16) | (0xFFu << 24);
        }

        public static void DecodeAlphaBlock(ReadOnlySpan<byte> data, Span<byte> output)
        {
            Span<byte> table = stackalloc byte[8];
            DecodeAlphaBlockInner(data, table, output);
        }

        public static void DecodeAlphaBlockInner(ReadOnlySpan<byte> data, Span<byte> table, Span<byte> output)
        {
            DecodeAlphaBlockInner(ref MemoryMarshal.GetReference(data), table, output);
        }

        public static void DecodeAlphaBlockInner(ref byte dataRef, Span<byte> table, Span<byte> output)
        {
            byte a0 = dataRef;
            byte a1 = Unsafe.Add(ref dataRef, 1);

            table[0] = a0;
            table[1] = a1;

            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++)
                    table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++)
                    table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                table[6] = 0;
                table[7] = 255;
            }

            ulong indices = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref dataRef, 2));

            for (int i = 0; i < 16; i++)
            {
                output[i] = table[(int)((indices >> (i * 3)) & 0x7)];
            }
        }
    }
}
