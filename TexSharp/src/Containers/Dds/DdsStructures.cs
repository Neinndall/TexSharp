using System;
using System.Runtime.InteropServices;

namespace TexSharp.Containers.Dds
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DdsHeader
    {
        public uint Magic;
        public uint Size;
        public uint Flags;
        public uint Height;
        public uint Width;
        public uint PitchOrLinearSize;
        public uint Depth;
        public uint MipMapCount;
        public unsafe fixed uint Reserved1[11];
        public DdsPixelFormat PixelFormat;
        public uint Caps;
        public uint Caps2;
        public uint Caps3;
        public uint Caps4;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DdsPixelFormat
    {
        public uint Size;
        public uint Flags;
        public uint FourCC;
        public uint RgbBitCount;
        public uint RBitMask;
        public uint GBitMask;
        public uint BBitMask;
        public uint ABitMask;

        public static uint MakeFourCC(char c0, char c1, char c2, char c3)
        {
            return (uint)c0 | ((uint)c1 << 8) | ((uint)c2 << 16) | ((uint)c3 << 24);
        }
    }

    public enum DdsFormat
    {
        Unknown,
        Bc1,
        Bc2,
        Bc3,
        Bc4,
        Bc5,
        Bc7,
        Rgba8,
        Bgra8
    }
}
