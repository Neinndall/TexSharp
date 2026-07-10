using System;
using System.Runtime.InteropServices;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Block Compression 2 (DXT3) decoder. Bloque de 16 bytes:
    /// 8 bytes de alpha explícito (4 bits por píxel) + 8 bytes de color BC1.
    /// </summary>
    public static class Bc2Block
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 16 || rgbaOutput.Length < 16) return;

            // Alpha explícito de 4 bits (un nibble por píxel, en orden little-endian).
            Span<byte> alphas = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                byte nibble = (i & 1) == 0 ? (byte)(data[i / 2] & 0x0F) : (byte)(data[i / 2] >> 4);
                alphas[i] = (byte)(nibble | (nibble << 4)); // 0..15 -> 0..255
            }

            Bc3Block.DecodeColorBlock(data.Slice(8, 8), rgbaOutput);

            for (int i = 0; i < 16; i++)
            {
                uint color = rgbaOutput[i] & 0x00FFFFFF;
                rgbaOutput[i] = color | ((uint)alphas[i] << 24);
            }
        }
    }
}
