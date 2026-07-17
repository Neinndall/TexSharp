using System;
using System.IO;
using System.Runtime.InteropServices;
using TexSharp.Formats;
using TexSharp.Formats.BC;

namespace TexSharp.Containers.Dds
{
    public class DdsReader
    {
        public DdsHeader Header { get; private set; }
        public DdsFormat Format { get; private set; }
        public int MipLevels { get; private set; }

        private readonly byte[] _fileData;
        private int _headerSize;
        private MipInfo[] _mips = Array.Empty<MipInfo>();

        private struct MipInfo
        {
            public int Offset;
            public int Width;
            public int Height;
        }

        public DdsReader(byte[] fileData)
        {
            ArgumentNullException.ThrowIfNull(fileData);
            if (fileData.Length < Marshal.SizeOf<DdsHeader>())
                throw new ArgumentException("File data is too small for a DDS header.");

            Header = MemoryMarshal.AsRef<DdsHeader>(fileData);

            if (Header.Magic != 0x20534444)
                throw new ArgumentException("Invalid DDS magic signature.");
            if (Header.Size != 124 || Header.PixelFormat.Size != 32)
                throw new ArgumentException("Invalid DDS header size.", nameof(fileData));
            if (Header.Width == 0 || Header.Height == 0 || Header.Width > int.MaxValue || Header.Height > int.MaxValue)
                throw new ArgumentException("DDS dimensions must be valid positive Int32 values.", nameof(fileData));

            _fileData = fileData;
            _headerSize = 128;

            uint dxgiFormat = 0;
            if (Header.PixelFormat.FourCC == DdsPixelFormat.MakeFourCC('D', 'X', '1', '0'))
            {
                if (fileData.Length < 148)
                    throw new ArgumentException("File data is too small for a DDS DX10 header.", nameof(fileData));

                dxgiFormat = BitConverter.ToUInt32(fileData, 128);
                _headerSize = 148;
            }

            Format = GetFormat(Header.PixelFormat, dxgiFormat);
            if (Format == DdsFormat.Unknown)
                throw new NotSupportedException("Unsupported DDS pixel format.");
            BuildMipChain();
        }

        private DecodedFormat ResolvedFormat => Format switch
        {
            DdsFormat.Bc1 => DecodedFormat.Bc1,
            DdsFormat.Bc2 => DecodedFormat.Bc2,
            DdsFormat.Bc3 => DecodedFormat.Bc3,
            DdsFormat.Bc4 => DecodedFormat.Bc4,
            DdsFormat.Bc5 => DecodedFormat.Bc5,
            DdsFormat.Bc7 => DecodedFormat.Bc7,
            DdsFormat.Bc4Snorm => DecodedFormat.Bc4Snorm,
            DdsFormat.Bc5Snorm => DecodedFormat.Bc5Snorm,
            DdsFormat.Rgba8 => DecodedFormat.Rgba8,
            DdsFormat.Bgra8 => DecodedFormat.DdsBgra8,
            _ => throw new NotSupportedException($"Unsupported DDS format: {Format}.")
        };

        private void BuildMipChain()
        {
            DecodedFormat fmt = ResolvedFormat;
            int w = (int)Header.Width;
            int h = (int)Header.Height;

            // El contador de mipmaps incluye el nivel base; si falta, asumimos 1.
            int possibleLevels = BcImageDecoder.ComputeMipLevels(w, h);
            if (Header.MipMapCount > (uint)possibleLevels)
                throw new ArgumentException("DDS mip level count exceeds the dimensions allow.");

            int declared = (int)Header.MipMapCount;
            int maxLevels = declared > 0 ? declared : 1;

            int total = 0;
            for (int i = 0; i < maxLevels; i++)
            {
                int width = Math.Max(1, w >> i);
                int height = Math.Max(1, h >> i);
                total = checked(total + BcImageDecoder.MipSize(width, height, fmt));
            }

            int dataLen = _fileData.Length - _headerSize;
            int levelCount = maxLevels;
            while (levelCount > 1 && total > dataLen)
            {
                levelCount--;
                int width = Math.Max(1, w >> levelCount);
                int height = Math.Max(1, h >> levelCount);
                total -= BcImageDecoder.MipSize(width, height, fmt);
            }

            _mips = new MipInfo[levelCount];
            int offset = 0; // DDS: mip mayor primero.
            for (int i = 0; i < levelCount; i++)
            {
                int width = Math.Max(1, w >> i);
                int height = Math.Max(1, h >> i);
                _mips[i] = new MipInfo { Offset = offset, Width = width, Height = height };
                offset += BcImageDecoder.MipSize(width, height, fmt);
            }
            MipLevels = levelCount;
        }

        private DdsFormat GetFormat(DdsPixelFormat pf, uint dxgiFormat)
        {
            if (dxgiFormat != 0)
            {
                return dxgiFormat switch
                {
                    71 or 72 => DdsFormat.Bc1,   // BC1 UNORM / SRGB
                    74 or 75 => DdsFormat.Bc2,   // BC2 UNORM / SRGB
                    77 or 78 => DdsFormat.Bc3,   // BC3 UNORM / SRGB
                    80 => DdsFormat.Bc4,
                    81 => DdsFormat.Bc4Snorm,
                    83 => DdsFormat.Bc5,
                    84 => DdsFormat.Bc5Snorm,
                    98 or 99 => DdsFormat.Bc7,   // BC7 UNORM / SRGB
                    28 => DdsFormat.Rgba8,       // R8G8B8A8 UNORM
                    87 => DdsFormat.Bgra8,       // B8G8R8A8 UNORM
                    _ => DdsFormat.Unknown
                };
            }

            if ((pf.Flags & 0x4) != 0) // DDPF_FOURCC
            {
                uint fourCC = pf.FourCC;
                if (fourCC == DdsPixelFormat.MakeFourCC('D', 'X', 'T', '1')) return DdsFormat.Bc1;
                if (fourCC == DdsPixelFormat.MakeFourCC('D', 'X', 'T', '3')) return DdsFormat.Bc2;
                if (fourCC == DdsPixelFormat.MakeFourCC('D', 'X', 'T', '5')) return DdsFormat.Bc3;
                if (fourCC == DdsPixelFormat.MakeFourCC('A', 'T', 'I', '1')) return DdsFormat.Bc4;
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '4', 'U')) return DdsFormat.Bc4;
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '4', 'S')) return DdsFormat.Bc4Snorm;
                if (fourCC == DdsPixelFormat.MakeFourCC('A', 'T', 'I', '2')) return DdsFormat.Bc5;
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '5', 'U')) return DdsFormat.Bc5;
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '5', 'S')) return DdsFormat.Bc5Snorm;
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '7', ' ')) return DdsFormat.Bc7;
            }
            return DdsFormat.Unknown;
        }

        public uint[] Decode() => DecodeMip(0);

        public uint[] DecodeMip(int level)
        {
            uint[] output = new uint[GetMipPixelCount(level)];
            DecodeMip(level, output);
            return output;
        }

        public void DecodeMip(int level, Span<uint> output)
        {
            if (level < 0 || level >= MipLevels)
                throw new ArgumentOutOfRangeException(nameof(level));

            MipInfo mip = _mips[level];
            int offset = checked(_headerSize + mip.Offset);
            int expectedSize = BcImageDecoder.MipSize(mip.Width, mip.Height, ResolvedFormat);
            int start = Math.Min(offset, _fileData.Length);
            int available = offset < _fileData.Length ? Math.Min(expectedSize, _fileData.Length - offset) : 0;
            ReadOnlySpan<byte> mipData = _fileData.AsSpan(start, available);
            BcImageDecoder.DecodeImage(mipData, mip.Width, mip.Height, ResolvedFormat, output);
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
            var result = new uint[MipLevels][];
            for (int i = 0; i < MipLevels; i++)
                result[i] = DecodeMip(i);
            return result;
        }
    }
}
