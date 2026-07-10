using System;
using System.Runtime.InteropServices;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Block Compression 1 (DXT1) decoder. Bloque de 8 bytes (color RGB 565 + índices).
    /// Soporta el caso de alpha 1-bit cuando Color0 <= Color1.
    /// </summary>
    public struct Bc1Block
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 8 || rgbaOutput.Length < 16) return;

            ushort c0 = MemoryMarshal.Read<ushort>(data);
            ushort c1 = MemoryMarshal.Read<ushort>(data.Slice(2));
            uint indices = MemoryMarshal.Read<uint>(data.Slice(4));

            Span<uint> colors = stackalloc uint[4];
            colors[0] = BcCommon.Rgb565ToRgba8(c0);
            colors[1] = BcCommon.Rgb565ToRgba8(c1);

            if (c0 > c1)
            {
                colors[2] = BcCommon.InterpolateRgb(colors[0], colors[1], 2, 1);
                colors[3] = BcCommon.InterpolateRgb(colors[0], colors[1], 1, 2);
            }
            else
            {
                colors[2] = BcCommon.InterpolateRgb(colors[0], colors[1], 1, 1);
                colors[3] = 0; // Transparente (alpha 0)
            }

            for (int i = 0; i < 16; i++)
            {
                rgbaOutput[i] = colors[(int)((indices >> (i * 2)) & 0x3)];
            }
        }
    }
}
