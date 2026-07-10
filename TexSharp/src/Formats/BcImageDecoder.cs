using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TexSharp.Formats.BC;

namespace TexSharp.Formats
{
    public enum DecodedFormat
    {
        Bc1,
        Bc2,
        Bc3,
        Bc4,
        Bc5,
        Bc7,
        Bgra8,
        Rgba16f,
        Rgba8,
        DdsBgra8
    }

    public enum Rgba16fColorMapping
    {
        Linear,
        SignedNormalizedOpaque
    }

    public static class BcImageDecoder
    {
        public static int GetBlockSize(DecodedFormat format)
        {
            return format switch
            {
                DecodedFormat.Bc1 or DecodedFormat.Bc4 => 8,
                DecodedFormat.Bc2 or DecodedFormat.Bc3 or DecodedFormat.Bc5 or DecodedFormat.Bc7 => 16,
                DecodedFormat.Bgra8 => 0,
                DecodedFormat.Rgba16f => 0,
                DecodedFormat.Rgba8 => 0,
                DecodedFormat.DdsBgra8 => 0,
                _ => 0
            };
        }

        public static void DecodeImage(ReadOnlySpan<byte> data, int width, int height, DecodedFormat format, Span<uint> output,
            Rgba16fColorMapping rgba16fMapping = Rgba16fColorMapping.Linear)
        {
            int pixelCount = GetPixelCount(width, height);
            if (output.Length < pixelCount)
                throw new ArgumentException("The output buffer is smaller than the decoded image.", nameof(output));

            int dataSize = MipSize(width, height, format);
            if (data.Length < dataSize)
                return;

            if (format is DecodedFormat.Bgra8 or DecodedFormat.Rgba8)
            {
                MemoryMarshal.Cast<byte, uint>(data.Slice(0, dataSize)).CopyTo(output);
                return;
            }

            if (format == DecodedFormat.DdsBgra8)
            {
                DecodeDdsBgra8(data, output, pixelCount);
                return;
            }

            if (format == DecodedFormat.Rgba16f)
            {
                DecodeRgba16f(data, output, pixelCount, rgba16fMapping);
                return;
            }

            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            bool aligned = (width & 3) == 0 && (height & 3) == 0;

            switch (format)
            {
                case DecodedFormat.Bc1: DecodeBc1(data, output, width, height, blocksX, blocksY, aligned); return;
                case DecodedFormat.Bc3: DecodeBc3(data, output, width, height, blocksX, blocksY, aligned); return;
                case DecodedFormat.Bc2: DecodeGeneric(data, output, width, height, blocksX, blocksY, 16, aligned, Bc2Block.DecodeBlock); return;
                case DecodedFormat.Bc4: DecodeGeneric(data, output, width, height, blocksX, blocksY, 8, aligned, Bc4Block.DecodeBlock); return;
                case DecodedFormat.Bc5: DecodeGeneric(data, output, width, height, blocksX, blocksY, 16, aligned, Bc5Block.DecodeBlock); return;
                case DecodedFormat.Bc7: DecodeGeneric(data, output, width, height, blocksX, blocksY, 16, aligned, Bc7Block.DecodeBlock); return;
            }
        }

        private static void DecodeBc1(ReadOnlySpan<byte> data, Span<uint> output,
            int width, int height, int blocksX, int blocksY, bool aligned)
        {
            for (int y = 0; y < blocksY; y++)
            {
                int baseY = y << 2;
                for (int x = 0; x < blocksX; x++)
                {
                    int offset = (y * blocksX + x) << 3;
                    ref byte dataRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
                    ushort c0 = Unsafe.ReadUnaligned<ushort>(ref dataRef);
                    ushort c1 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref dataRef, 2));
                    uint indices = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref dataRef, 4));

                    uint col0 = BcCommon.Rgb565ToRgba8(c0);
                    uint col1 = BcCommon.Rgb565ToRgba8(c1);
                    int r0 = (int)(col0 & 0xFF), r1 = (int)(col1 & 0xFF);
                    int g0 = (int)((col0 >> 8) & 0xFF), g1 = (int)((col1 >> 8) & 0xFF);
                    int b0 = (int)((col0 >> 16) & 0xFF), b1 = (int)((col1 >> 16) & 0xFF);
                    uint col2, col3;
                    if (c0 > c1)
                    {
                        col2 = (uint)((r0 * 2 + r1) * 21846 >> 16) | (uint)((g0 * 2 + g1) * 21846 >> 16) << 8 | (uint)((b0 * 2 + b1) * 21846 >> 16) << 16 | 0xFF000000u;
                        col3 = (uint)((r0 + r1 * 2) * 21846 >> 16) | (uint)((g0 + g1 * 2) * 21846 >> 16) << 8 | (uint)((b0 + b1 * 2) * 21846 >> 16) << 16 | 0xFF000000u;
                    }
                    else
                    {
                        col2 = (uint)((r0 + r1) >> 1) | (uint)((g0 + g1) >> 1) << 8 | (uint)((b0 + b1) >> 1) << 16 | 0xFF000000u;
                        col3 = 0;
                    }

                    int baseX = x << 2;
                    if (aligned)
                    {
                        int row0 = baseY * width + baseX;
                        int row1 = row0 + width;
                        int row2 = row1 + width;
                        int row3 = row2 + width;
                        for (int i = 0; i < 16; i++)
                        {
                            uint c = (indices >> (i * 2) & 0x3) switch { 1 => col1, 2 => col2, 3 => col3, _ => col0 };
                            int py = i >> 2;
                            if (py == 0) output[row0 + (i & 3)] = c;
                            else if (py == 1) output[row1 + (i & 3)] = c;
                            else if (py == 2) output[row2 + (i & 3)] = c;
                            else output[row3 + (i & 3)] = c;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            int py = i >> 2;
                            int targetY = baseY + py;
                            if (targetY >= height) continue;
                            int px = i & 3;
                            int targetX = baseX + px;
                            if (targetX >= width) continue;
                            uint c = (indices >> (i * 2) & 0x3) switch { 1 => col1, 2 => col2, 3 => col3, _ => col0 };
                            output[targetY * width + targetX] = c;
                        }
                    }
                }
            }
        }

        private static void DecodeBc3(ReadOnlySpan<byte> data, Span<uint> output,
            int width, int height, int blocksX, int blocksY, bool aligned)
        {
            Span<byte> alphaTable = stackalloc byte[8];
            Span<byte> alphaPixels = stackalloc byte[16];
            for (int y = 0; y < blocksY; y++)
            {
                int baseY = y << 2;
                for (int x = 0; x < blocksX; x++)
                {
                    int offset = (y * blocksX + x) << 4;
                    BcCommon.DecodeAlphaBlockInner(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset), alphaTable, alphaPixels);

                    int colorOff = offset + 8;
                    ref byte baseRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), colorOff);
                    ushort c0 = Unsafe.ReadUnaligned<ushort>(ref baseRef);
                    ushort c1 = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref baseRef, 2));
                    uint indices = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref baseRef, 4));

                    uint col0 = BcCommon.Rgb565ToRgba8(c0);
                    uint col1 = BcCommon.Rgb565ToRgba8(c1);
                    int r0 = (int)(col0 & 0xFF), r1 = (int)(col1 & 0xFF);
                    int g0 = (int)((col0 >> 8) & 0xFF), g1 = (int)((col1 >> 8) & 0xFF);
                    int b0 = (int)((col0 >> 16) & 0xFF), b1 = (int)((col1 >> 16) & 0xFF);
                    uint col2, col3;
                    if (c0 > c1)
                    {
                        col2 = (uint)((r0 * 2 + r1) * 21846 >> 16) | (uint)((g0 * 2 + g1) * 21846 >> 16) << 8 | (uint)((b0 * 2 + b1) * 21846 >> 16) << 16 | 0xFF000000u;
                        col3 = (uint)((r0 + r1 * 2) * 21846 >> 16) | (uint)((g0 + g1 * 2) * 21846 >> 16) << 8 | (uint)((b0 + b1 * 2) * 21846 >> 16) << 16 | 0xFF000000u;
                    }
                    else
                    {
                        col2 = (uint)((r0 + r1) >> 1) | (uint)((g0 + g1) >> 1) << 8 | (uint)((b0 + b1) >> 1) << 16 | 0xFF000000u;
                        col3 = 0;
                    }

                    uint maskedCol0 = col0 & 0x00FFFFFFu;
                    uint maskedCol1 = col1 & 0x00FFFFFFu;
                    uint maskedCol2 = col2 & 0x00FFFFFFu;
                    uint maskedCol3 = col3 & 0x00FFFFFFu;

                    int baseX = x << 2;
                    if (aligned)
                    {
                        int row0 = baseY * width + baseX;
                        int row1 = row0 + width;
                        int row2 = row1 + width;
                        int row3 = row2 + width;
                        for (int i = 0; i < 16; i++)
                        {
                            uint color = (uint)alphaPixels[i] << 24;
                            color |= (indices >> (i * 2) & 0x3) switch { 1 => maskedCol1, 2 => maskedCol2, 3 => maskedCol3, _ => maskedCol0 };
                            int py = i >> 2;
                            if (py == 0) output[row0 + (i & 3)] = color;
                            else if (py == 1) output[row1 + (i & 3)] = color;
                            else if (py == 2) output[row2 + (i & 3)] = color;
                            else output[row3 + (i & 3)] = color;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            int py = i >> 2;
                            int targetY = baseY + py;
                            if (targetY >= height) continue;
                            int px = i & 3;
                            int targetX = baseX + px;
                            if (targetX >= width) continue;
                            uint color = (uint)alphaPixels[i] << 24;
                            color |= (indices >> (i * 2) & 0x3) switch { 1 => maskedCol1, 2 => maskedCol2, 3 => maskedCol3, _ => maskedCol0 };
                            output[targetY * width + targetX] = color;
                        }
                    }
                }
            }
        }

        private static void DecodeGeneric(ReadOnlySpan<byte> data, Span<uint> output,
            int width, int height, int blocksX, int blocksY, int blockSize, bool aligned,
            Action<ReadOnlySpan<byte>, Span<uint>> decodeBlock)
        {
            Span<uint> block = stackalloc uint[16];
            for (int y = 0; y < blocksY; y++)
            {
                for (int x = 0; x < blocksX; x++)
                {
                    int offset = (y * blocksX + x) * blockSize;
                    if (offset + blockSize > data.Length) return;
                    ReadOnlySpan<byte> blockData = data.Slice(offset, blockSize);
                    decodeBlock(blockData, block);
                    WriteBlock(output, width, y, x, block, aligned, height);
                }
            }
        }

        private static void WriteBlock(Span<uint> output, int width, int y, int x, ReadOnlySpan<uint> block, bool aligned, int height)
        {
            if (aligned)
            {
                int row0 = (y << 2) * width + (x << 2);
                int row1 = row0 + width;
                int row2 = row1 + width;
                int row3 = row2 + width;
                output[row0] = block[0]; output[row0 + 1] = block[1]; output[row0 + 2] = block[2]; output[row0 + 3] = block[3];
                output[row1] = block[4]; output[row1 + 1] = block[5]; output[row1 + 2] = block[6]; output[row1 + 3] = block[7];
                output[row2] = block[8]; output[row2 + 1] = block[9]; output[row2 + 2] = block[10]; output[row2 + 3] = block[11];
                output[row3] = block[12]; output[row3 + 1] = block[13]; output[row3 + 2] = block[14]; output[row3 + 3] = block[15];
            }
            else
            {
                for (int py = 0; py < 4; py++)
                {
                    int targetY = y * 4 + py;
                    if (targetY >= height) continue;
                    for (int px = 0; px < 4; px++)
                    {
                        int targetX = x * 4 + px;
                        if (targetX >= width) continue;
                        output[targetY * width + targetX] = block[py * 4 + px];
                    }
                }
            }
        }

        public static int ComputeMipLevels(int width, int height)
        {
            int levels = 1;
            int w = width;
            int h = height;
            while (w > 1 || h > 1)
            {
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
                levels++;
            }
            return levels;
        }

        public static int MipSize(int width, int height, DecodedFormat format)
        {
            int pixelCount = GetPixelCount(width, height);
            if (format is DecodedFormat.Bgra8 or DecodedFormat.Rgba8 or DecodedFormat.DdsBgra8)
                return checked(pixelCount * 4);
            if (format == DecodedFormat.Rgba16f) return checked(pixelCount * 8);
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            return checked(blocksX * blocksY * GetBlockSize(format));
        }

        private static int GetPixelCount(int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Image width must be greater than zero.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), "Image height must be greater than zero.");

            return checked(width * height);
        }

        private static void DecodeRgba16f(ReadOnlySpan<byte> data, Span<uint> output, int pixelCount,
            Rgba16fColorMapping mapping)
        {
            ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(data);
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                uint r = mapping == Rgba16fColorMapping.SignedNormalizedOpaque
                    ? SignedHalfToByte(source[offset]) : HalfToByte(source[offset]);
                uint g = mapping == Rgba16fColorMapping.SignedNormalizedOpaque
                    ? SignedHalfToByte(source[offset + 1]) : HalfToByte(source[offset + 1]);
                uint b = mapping == Rgba16fColorMapping.SignedNormalizedOpaque
                    ? SignedHalfToByte(source[offset + 2]) : HalfToByte(source[offset + 2]);
                uint a = mapping == Rgba16fColorMapping.SignedNormalizedOpaque ? 255u : HalfToByte(source[offset + 3]);
                output[i] = r | (g << 8) | (b << 16) | (a << 24);
            }
        }

        private static uint HalfToByte(ushort bits)
        {
            float value = (float)BitConverter.UInt16BitsToHalf(bits);
            if (!(value > 0)) return 0;
            if (value >= 1) return 255;
            return (uint)(value * 255.0f + 0.5f);
        }

        private static uint SignedHalfToByte(ushort bits)
        {
            float value = (float)BitConverter.UInt16BitsToHalf(bits);
            if (float.IsNaN(value)) return 0;
            value = Math.Clamp(value * 0.5f + 0.5f, 0.0f, 1.0f);
            return (uint)(value * 255.0f + 0.5f);
        }

        private static void DecodeDdsBgra8(ReadOnlySpan<byte> data, Span<uint> output, int pixelCount)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                output[i] = (uint)data[offset + 2] |
                            (uint)data[offset + 1] << 8 |
                            (uint)data[offset] << 16 |
                            (uint)data[offset + 3] << 24;
            }
        }
    }
}
