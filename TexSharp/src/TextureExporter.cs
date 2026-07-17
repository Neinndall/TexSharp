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
                using (var idat = new IdatChunkStream(writer))
                using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
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
                        zlib.Write(rowBuf, 0, rowLen);
                    }
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
            writer.WriteBE(unchecked((int)crc));
        }

        private sealed class IdatChunkStream : Stream
        {
            private const int BufferSize = 64 * 1024;
            private readonly BinaryWriter _writer;
            private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            private int _count;
            private bool _disposed;

            public IdatChunkStream(BinaryWriter writer) => _writer = writer;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                while (!buffer.IsEmpty)
                {
                    int length = Math.Min(BufferSize - _count, buffer.Length);
                    buffer[..length].CopyTo(_buffer.AsSpan(_count));
                    _count += length;
                    buffer = buffer[length..];
                    if (_count == BufferSize) FlushChunk();
                }
            }

            public override void Flush() => FlushChunk();
            private void FlushChunk()
            {
                if (_count == 0) return;
                int length = _count;
                WriteChunk(_writer, "IDAT", writer => writer.Write(_buffer, 0, length));
                _count = 0;
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    if (disposing) FlushChunk();
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _disposed = true;
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
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
