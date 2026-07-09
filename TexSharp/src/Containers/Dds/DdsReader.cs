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
            if (fileData.Length < Marshal.SizeOf<DdsHeader>())
                throw new ArgumentException("File data is too small for a DDS header.");

            Header = MemoryMarshal.AsRef<DdsHeader>(fileData);

            if (Header.Magic != 0x20534444)
                throw new ArgumentException("Invalid DDS magic signature.");

            _fileData = fileData;
            _headerSize = 128;

            uint dxgiFormat = 0;
            if (Header.PixelFormat.FourCC == DdsPixelFormat.MakeFourCC('D', 'X', '1', '0'))
            {
                if (fileData.Length >= 148)
                    dxgiFormat = BitConverter.ToUInt32(fileData, 128);
                _headerSize = 148;
            }

            Format = GetFormat(Header.PixelFormat, dxgiFormat);
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
            DdsFormat.Rgba8 => DecodedFormat.Bgra8,
            _ => DecodedFormat.Bgra8
        };

        private void BuildMipChain()
        {
            DecodedFormat fmt = ResolvedFormat;
            int w = (int)Header.Width;
            int h = (int)Header.Height;

            // El contador de mipmaps incluye el nivel base; si falta, asumimos 1.
            int declared = (int)Header.MipMapCount;
            int maxLevels = declared > 1 ? declared : BcImageDecoder.ComputeMipLevels(w, h);

            var sizes = new int[maxLevels];
            int cw = w, ch = h;
            for (int i = 0; i < maxLevels; i++)
            {
                sizes[i] = BcImageDecoder.MipSize(cw, ch, fmt);
                cw = Math.Max(1, cw >> 1);
                ch = Math.Max(1, ch >> 1);
            }

            int total = 0;
            for (int i = 0; i < maxLevels; i++) total += sizes[i];

            int dataLen = _fileData.Length - _headerSize;
            int levelCount = maxLevels;
            while (levelCount > 1 && total > dataLen)
            {
                levelCount--;
                total -= sizes[levelCount];
            }

            _mips = new MipInfo[levelCount];
            int offset = 0; // DDS: mip mayor primero.
            cw = w; ch = h;
            for (int i = 0; i < levelCount; i++)
            {
                _mips[i] = new MipInfo { Offset = offset, Width = cw, Height = ch };
                offset += sizes[i];
                cw = Math.Max(1, cw >> 1);
                ch = Math.Max(1, ch >> 1);
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
                    80 or 81 => DdsFormat.Bc4,   // BC4 UNORM / SNORM
                    83 or 84 => DdsFormat.Bc5,   // BC5 UNORM / SNORM
                    98 or 99 => DdsFormat.Bc7,   // BC7 UNORM / SRGB
                    28 => DdsFormat.Rgba8,       // R8G8B8A8 UNORM
                    87 => DdsFormat.Rgba8,       // B8G8R8A8 UNORM
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
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '4', 'U') ||
                    fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '4', 'S')) return DdsFormat.Bc4;
                if (fourCC == DdsPixelFormat.MakeFourCC('A', 'T', 'I', '2')) return DdsFormat.Bc5;
                if (fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '5', 'U') ||
                    fourCC == DdsPixelFormat.MakeFourCC('B', 'C', '5', 'S')) return DdsFormat.Bc5;
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
            ReadOnlySpan<byte> mipData = _fileData.AsSpan(_headerSize + mip.Offset, BcImageDecoder.MipSize(mip.Width, mip.Height, ResolvedFormat));
            BcImageDecoder.DecodeImage(mipData, mip.Width, mip.Height, ResolvedFormat, output);
        }

        public int GetMipPixelCount(int level)
        {
            if (level < 0 || level >= MipLevels)
                throw new ArgumentOutOfRangeException(nameof(level));
            MipInfo mip = _mips[level];
            return mip.Width * mip.Height;
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
