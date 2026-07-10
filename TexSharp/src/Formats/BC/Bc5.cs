using System;
using System.Runtime.InteropServices;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Block Compression 5 (BC5) decoder. Típico de normal maps (canales R y G).
    /// Bloque de 16 bytes: dos bloques de alpha BC3 (rojo y verde).
    /// </summary>
    public static class Bc5Block
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 16 || rgbaOutput.Length < 16) return;

            Span<byte> reds = stackalloc byte[16];
            Span<byte> greens = stackalloc byte[16];

            BcCommon.DecodeAlphaBlock(data.Slice(0, 8), reds);
            BcCommon.DecodeAlphaBlock(data.Slice(8, 8), greens);

            for (int i = 0; i < 16; i++)
            {
                rgbaOutput[i] = (uint)(reds[i] | (greens[i] << 8) | (0xFF << 16) | (0xFF << 24));
            }
        }
    }
}
