using System;
using System.Runtime.InteropServices;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Block Compression 4 (BC4 / ATI1) decoder. Un solo canal (rojo).
    /// Bloque de 8 bytes (misma estructura de alpha que BC3/BC5).
    /// </summary>
    public static class Bc4Block
    {
        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (data.Length < 8) return;

            Span<byte> values = stackalloc byte[16];
            BcCommon.DecodeAlphaBlock(data, values);

            for (int i = 0; i < 16; i++)
            {
                byte v = values[i];
                rgbaOutput[i] = (uint)(v | (v << 8) | (v << 16) | (0xFF << 24));
            }
        }
    }
}
