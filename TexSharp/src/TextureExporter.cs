using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using TexSharp.Containers.Tex;
using TexSharp.Containers.Dds;

namespace TexSharp
{
    public static class TextureExporter
    {
        public static void SaveToPng(string texPath, string pngPath, int mipLevel = 0)
        {
            byte[] data = File.ReadAllBytes(texPath);
            int width, height;

            if (texPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                var reader = new TexReader(data);
                width = reader.Width >> mipLevel;
                height = reader.Height >> mipLevel;
                if (width < 1) width = 1;
                if (height < 1) height = 1;
                int pixelCount = reader.GetMipPixelCount(mipLevel);
                uint[] pixels = ArrayPool<uint>.Shared.Rent(pixelCount);
                try
                {
                    reader.DecodeMip(mipLevel, pixels.AsSpan(0, pixelCount));
                    WritePng(pixels.AsSpan(0, pixelCount), width, height, pngPath);
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(pixels);
                }
            }
            else if (texPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var reader = new DdsReader(data);
                width = (int)reader.Header.Width >> mipLevel;
                height = (int)reader.Header.Height >> mipLevel;
                if (width < 1) width = 1;
                if (height < 1) height = 1;
                int pixelCount = reader.GetMipPixelCount(mipLevel);
                uint[] pixels = ArrayPool<uint>.Shared.Rent(pixelCount);
                try
                {
                    reader.DecodeMip(mipLevel, pixels.AsSpan(0, pixelCount));
                    WritePng(pixels.AsSpan(0, pixelCount), width, height, pngPath);
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(pixels);
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported format: {texPath}");
            }
        }

        public static void SaveToPng(uint[] pixels, int width, int height, string pngPath)
        {
            WritePng(pixels, width, height, pngPath);
        }

        private static void WritePng(ReadOnlySpan<uint> pixels, int width, int height, string pngPath)
        {
            using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.ReadWrite);
            using var writer = new BinaryWriter(fs);

            writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            WriteChunk(writer, "IHDR", bw =>
            {
                bw.WriteBE(width);
                bw.WriteBE(height);
                bw.Write((byte)8);
                bw.Write((byte)6);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((byte)0);
            });

            int rowLen = width * 4 + 1;
            byte[] rowBuf = ArrayPool<byte>.Shared.Rent(rowLen);
            try
            {
                using var compressedMs = new MemoryStream();
                using (var deflate = new DeflateStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        rowBuf[0] = 0;
                        for (int x = 0; x < width; x++)
                        {
                            uint pixel = pixels[y * width + x];
                            int o = x * 4 + 1;
                            rowBuf[o] = (byte)(pixel & 0xFF);
                            rowBuf[o + 1] = (byte)((pixel >> 8) & 0xFF);
                            rowBuf[o + 2] = (byte)((pixel >> 16) & 0xFF);
                            rowBuf[o + 3] = (byte)((pixel >> 24) & 0xFF);
                        }
                        deflate.Write(rowBuf, 0, rowLen);
                    }
                }

                byte[] compressedBuf = compressedMs.GetBuffer();
                int compressedLen = (int)compressedMs.Length;
                int zlibLen = compressedLen + 6;

                byte[] zlibBuf = ArrayPool<byte>.Shared.Rent(zlibLen);
                try
                {
                    zlibBuf[0] = 0x78;
                    zlibBuf[1] = 0x01;
                    Buffer.BlockCopy(compressedBuf, 0, zlibBuf, 2, compressedLen);

                    uint adler = Adler32(pixels, width, height);
                    int end = compressedLen + 2;
                    zlibBuf[end] = (byte)(adler >> 24);
                    zlibBuf[end + 1] = (byte)(adler >> 16);
                    zlibBuf[end + 2] = (byte)(adler >> 8);
                    zlibBuf[end + 3] = (byte)adler;

                    WriteChunk(writer, "IDAT", bw => bw.Write(zlibBuf, 0, zlibLen));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(zlibBuf);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rowBuf);
            }

            WriteChunk(writer, "IEND", bw => { });
        }

        private static void WriteChunk(BinaryWriter writer, string type, Action<BinaryWriter> writeData)
        {
            long lenPos = writer.BaseStream.Position;
            writer.Write(0);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            writer.Write(typeBytes);
            long dataStart = writer.BaseStream.Position;
            writeData(writer);
            long dataEnd = writer.BaseStream.Position;

            int dataLen = (int)(dataEnd - dataStart);
            writer.BaseStream.Position = lenPos;
            writer.WriteBE(dataLen);
            writer.BaseStream.Position = dataEnd;

            uint crc = Crc32Stream(writer.BaseStream, dataStart - 4, dataLen + 4);
            writer.Write(crc);
        }

        private static uint Adler32(ReadOnlySpan<uint> pixels, int w, int h)
        {
            const uint MOD = 65521;
            uint a = 1, b = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint p = pixels[y * w + x];
                    a = (a + (p & 0xFF)) % MOD;
                    b = (b + a) % MOD;
                    a = (a + ((p >> 8) & 0xFF)) % MOD;
                    b = (b + a) % MOD;
                    a = (a + ((p >> 16) & 0xFF)) % MOD;
                    b = (b + a) % MOD;
                    a = (a + ((p >> 24) & 0xFF)) % MOD;
                    b = (b + a) % MOD;
                }
            }
            return (b << 16) | a;
        }

        private static uint Crc32Stream(Stream stream, long start, int length)
        {
            uint crc = 0xFFFFFFFF;
            long origPos = stream.Position;
            stream.Position = start;
            for (int i = 0; i < length; i++)
            {
                int b = stream.ReadByte();
                crc = Crc32Table[(crc ^ (byte)b) & 0xFF] ^ (crc >> 8);
            }
            stream.Position = origPos;
            return crc ^ 0xFFFFFFFF;
        }

        private static readonly uint[] Crc32Table = InitCrc32();

        private static uint[] InitCrc32()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) == 1 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }

    internal static class BinaryWriterExtensions
    {
        public static void WriteBE(this BinaryWriter bw, int value)
        {
            bw.Write((byte)(value >> 24));
            bw.Write((byte)(value >> 16));
            bw.Write((byte)(value >> 8));
            bw.Write((byte)value);
        }
    }
}
