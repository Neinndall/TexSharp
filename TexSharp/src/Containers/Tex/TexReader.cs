using System;
using System.IO;
using TexSharp.Formats;
using TexSharp.Formats.BC;

namespace TexSharp.Containers.Tex
{
    public enum TexFormat : byte
    {
        Etc1 = 1,
        Etc2 = 2,
        Bc1 = 10,
        Bc1_Alt = 11,
        Bc3 = 12,
        Bc7 = 13,
        Bc5 = 14,
        Bgra8 = 20,
        Rgba16f = 21
    }

    [Flags]
    public enum TexFlags : byte
    {
        None = 0,
        HasMipMaps = 1 << 0
    }

    public class TexReader
    {
        public ushort Width { get; private set; }
        public ushort Height { get; private set; }
        public TexFormat Format { get; private set; }
        public TexFlags Flags { get; private set; }

        public int MipLevels { get; private set; }

        private readonly byte[] _fileData;
        private const int HeaderSize = 12;
        private MipInfo[] _mips = Array.Empty<MipInfo>();

        private struct MipInfo
        {
            public int Offset;
            public int Width;
            public int Height;
        }

        public TexReader(byte[] fileData)
        {
            ArgumentNullException.ThrowIfNull(fileData);
            if (fileData.Length < HeaderSize)
                throw new ArgumentException("File data is too small for a TEX header.", nameof(fileData));

            _fileData = fileData;
            uint magic = BitConverter.ToUInt32(fileData, 0);
            if (magic != 0x00584554)
                throw new ArgumentException("Invalid TEX magic signature.");

            Width = BitConverter.ToUInt16(fileData, 4);
            Height = BitConverter.ToUInt16(fileData, 6);
            Format = (TexFormat)fileData[9];
            Flags = (TexFlags)fileData[11];

            _ = ResolvedFormat;
            if (Width == 0 || Height == 0)
                throw new ArgumentException("TEX dimensions must be greater than zero.", nameof(fileData));

            BuildMipChain();
        }

        private DecodedFormat ResolvedFormat => Format switch
        {
            TexFormat.Bc1 or TexFormat.Bc1_Alt => DecodedFormat.Bc1,
            TexFormat.Bc3 => DecodedFormat.Bc3,
            TexFormat.Bc7 => DecodedFormat.Bc7,
            TexFormat.Bc5 => DecodedFormat.Bc5,
            TexFormat.Bgra8 => DecodedFormat.Bgra8,
            TexFormat.Rgba16f => DecodedFormat.Rgba16f,
            _ => throw new NotSupportedException($"Unsupported TEX format: {Format}.")
        };

        private void BuildMipChain()
        {
            DecodedFormat fmt = ResolvedFormat;
            int maxLevels = (Flags & TexFlags.HasMipMaps) != 0
                ? BcImageDecoder.ComputeMipLevels(Width, Height)
                : 1;

            // Construir tamaños de cada mip (índice 0 = mayor).
            var sizes = new int[maxLevels];
            int w = Width;
            int h = Height;
            for (int i = 0; i < maxLevels; i++)
            {
                sizes[i] = BcImageDecoder.MipSize(w, h, fmt);
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
            }

            int total = 0;
            for (int i = 0; i < maxLevels; i++) total = checked(total + sizes[i]);

            int dataLen = _fileData.Length - HeaderSize;
            int levelCount = maxLevels;
            while (levelCount > 1 && total > dataLen)
            {
                levelCount--;
                total -= sizes[levelCount];
            }

            _mips = new MipInfo[levelCount];
            int[] mipW = new int[levelCount];
            int[] mipH = new int[levelCount];
            int cw = Width, ch = Height;
            for (int i = 0; i < levelCount; i++)
            {
                mipW[i] = cw; mipH[i] = ch;
                cw = Math.Max(1, cw >> 1);
                ch = Math.Max(1, ch >> 1);
            }
            int offset = 0;
            for (int i = levelCount - 1; i >= 0; i--)
            {
                _mips[i] = new MipInfo { Offset = offset, Width = mipW[i], Height = mipH[i] };
                offset += sizes[i];
            }
            MipLevels = levelCount;
        }

        public uint[] Decode() => DecodeMip(0);

        public uint[] DecodeMip(int level)
        {
            return DecodeMip(level, Rgba16fColorMapping.Linear);
        }

        public uint[] DecodeMip(int level, Rgba16fColorMapping rgba16fMapping)
        {
            uint[] output = new uint[GetMipPixelCount(level)];
            DecodeMip(level, output, rgba16fMapping);
            return output;
        }

        public void DecodeMip(int level, Span<uint> output)
        {
            DecodeMip(level, output, Rgba16fColorMapping.Linear);
        }

        public void DecodeMip(int level, Span<uint> output, Rgba16fColorMapping rgba16fMapping)
        {
            if (level < 0 || level >= MipLevels)
                throw new ArgumentOutOfRangeException(nameof(level));

            MipInfo mip = _mips[level];
            int offset = checked(HeaderSize + mip.Offset);
            int expectedSize = BcImageDecoder.MipSize(mip.Width, mip.Height, ResolvedFormat);
            int start = Math.Min(offset, _fileData.Length);
            int available = offset < _fileData.Length ? Math.Min(expectedSize, _fileData.Length - offset) : 0;
            ReadOnlySpan<byte> mipData = _fileData.AsSpan(start, available);
            BcImageDecoder.DecodeImage(mipData, mip.Width, mip.Height, ResolvedFormat, output, rgba16fMapping);
        }

        public int GetMipPixelCount(int level)
        {
            if (level < 0 || level >= MipLevels)
                throw new ArgumentOutOfRangeException(nameof(level));
            MipInfo mip = _mips[level];
            return checked(mip.Width * mip.Height);
        }

        public uint[][] DecodeAllMips()
        {
            return DecodeAllMips(Rgba16fColorMapping.Linear);
        }

        public uint[][] DecodeAllMips(Rgba16fColorMapping rgba16fMapping)
        {
            var result = new uint[MipLevels][];
            for (int i = 0; i < MipLevels; i++)
                result[i] = DecodeMip(i, rgba16fMapping);
            return result;
        }
    }
}
