using System;
using System.Runtime.InteropServices;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Block Compression 3 (DXT5) decoder. Bloque de 16 bytes:
    /// 8 bytes de alpha interpolado + 8 bytes de color BC1.
    /// </summary>
    public struct Bc3Block
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 16 || rgbaOutput.Length < 16) return;

            Span<byte> alphas = stackalloc byte[16];
            BcCommon.DecodeAlphaBlock(data.Slice(0, 8), alphas);

            DecodeColorBlock(data.Slice(8, 8), rgbaOutput);

            for (int i = 0; i < 16; i++)
            {
                uint color = rgbaOutput[i] & 0x00FFFFFF;
                rgbaOutput[i] = color | ((uint)alphas[i] << 24);
            }
        }

        public static void DecodeColorBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 8 || rgbaOutput.Length < 16) return;

            ushort c0 = MemoryMarshal.Read<ushort>(data);
            ushort c1 = MemoryMarshal.Read<ushort>(data.Slice(2));
            uint indices = MemoryMarshal.Read<uint>(data.Slice(4));

            Span<uint> colors = stackalloc uint[4];
            colors[0] = BcCommon.Rgb565ToRgba8(c0);
            colors[1] = BcCommon.Rgb565ToRgba8(c1);
            colors[2] = BcCommon.InterpolateRgb(colors[0], colors[1], 2, 1);
            colors[3] = BcCommon.InterpolateRgb(colors[0], colors[1], 1, 2);

            for (int i = 0; i < 16; i++)
            {
                rgbaOutput[i] = colors[(int)((indices >> (i * 2)) & 0x3)];
            }
        }
    }
}
