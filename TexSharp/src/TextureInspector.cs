using System.Buffers.Binary;
using TexSharp.Formats;

namespace TexSharp;

public enum TextureContainer : byte
{
    Unknown,
    Tex,
    Dds
}

public enum TexturePixelFormat : byte
{
    Unknown,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc4Snorm,
    Bc5,
    Bc5Snorm,
    Bc7,
    Bgra8,
    Rgba8,
    Rgba16Float
}

public enum TextureInspectionStatus : byte
{
    Success,
    Encrypted,
    InvalidData,
    UnsupportedFormat
}

public readonly record struct TextureInfo(
    TextureContainer Container,
    TexturePixelFormat Format,
    int Width,
    int Height,
    int MipLevels,
    int HeaderSize,
    bool HasMipMaps,
    bool IsDataComplete);

/// <summary>Inspecciona cabeceras TEX y DDS sin decodificar ni asignar memoria administrada.</summary>
public static class TextureInspector
{
    private const uint TexMagic = 0x00584554;
    private const uint DdsMagic = 0x20534444;
    private const uint EncryptedRiotTexMagic = 0x2644E3C9;

    public static TextureInspectionStatus Inspect(ReadOnlySpan<byte> data, out TextureInfo info)
    {
        info = default;
        if (data.Length < sizeof(uint))
            return TextureInspectionStatus.InvalidData;

        return BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            TexMagic => InspectTex(data, out info),
            DdsMagic => InspectDds(data, out info),
            EncryptedRiotTexMagic => InspectEncrypted(out info),
            _ => TextureInspectionStatus.InvalidData
        };
    }

    private static TextureInspectionStatus InspectEncrypted(out TextureInfo info)
    {
        info = new(TextureContainer.Tex, TexturePixelFormat.Unknown, 0, 0, 0, 0, false, false);
        return TextureInspectionStatus.Encrypted;
    }

    private static TextureInspectionStatus InspectTex(ReadOnlySpan<byte> data, out TextureInfo info)
    {
        info = default;
        const int headerSize = 12;
        if (data.Length < headerSize)
            return TextureInspectionStatus.InvalidData;

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        bool hasMipMaps = (data[11] & 1) != 0;
        if (width == 0 || height == 0)
            return TextureInspectionStatus.InvalidData;

        TexturePixelFormat format = data[9] switch
        {
            10 or 11 => TexturePixelFormat.Bc1,
            12 => TexturePixelFormat.Bc3,
            13 => TexturePixelFormat.Bc7,
            14 => TexturePixelFormat.Bc5,
            20 => TexturePixelFormat.Bgra8,
            21 => TexturePixelFormat.Rgba16Float,
            _ => TexturePixelFormat.Unknown
        };
        if (format == TexturePixelFormat.Unknown)
        {
            info = new(TextureContainer.Tex, format, width, height, 0, headerSize, hasMipMaps, false);
            return TextureInspectionStatus.UnsupportedFormat;
        }

        int maximumLevels = hasMipMaps ? BcImageDecoder.ComputeMipLevels(width, height) : 1;
        int availableBytes = data.Length - headerSize;
        int mipLevels = CountCompleteTexMips(width, height, maximumLevels, format, availableBytes,
            out bool complete);
        info = new(TextureContainer.Tex, format, width, height, mipLevels, headerSize, hasMipMaps, complete);
        return TextureInspectionStatus.Success;
    }

    private static TextureInspectionStatus InspectDds(ReadOnlySpan<byte> data, out TextureInfo info)
    {
        info = default;
        const int baseHeaderSize = 128;
        if (data.Length < baseHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 124 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[76..]) != 32)
            return TextureInspectionStatus.InvalidData;

        uint rawWidth = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        uint rawHeight = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (rawWidth is 0 or > int.MaxValue || rawHeight is 0 or > int.MaxValue)
            return TextureInspectionStatus.InvalidData;

        int width = (int)rawWidth;
        int height = (int)rawHeight;
        uint declaredMips = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        int maximumLevels = BcImageDecoder.ComputeMipLevels(width, height);
        if (declaredMips > maximumLevels)
            return TextureInspectionStatus.InvalidData;

        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
        int headerSize = baseHeaderSize;
        TexturePixelFormat format;
        if (fourCc == MakeFourCc('D', 'X', '1', '0'))
        {
            headerSize = 148;
            if (data.Length < headerSize)
                return TextureInspectionStatus.InvalidData;
            format = DxgiToFormat(BinaryPrimitives.ReadUInt32LittleEndian(data[128..]));
        }
        else
        {
            format = FourCcToFormat(fourCc);
        }

        int mipLevels = declaredMips == 0 ? 1 : (int)declaredMips;
        bool hasMipMaps = mipLevels > 1;
        if (format == TexturePixelFormat.Unknown)
        {
            info = new(TextureContainer.Dds, format, width, height, mipLevels, headerSize, hasMipMaps, false);
            return TextureInspectionStatus.UnsupportedFormat;
        }

        bool complete = HasCompleteDdsData(data.Length - headerSize, width, height, mipLevels, format);
        info = new(TextureContainer.Dds, format, width, height, mipLevels, headerSize, hasMipMaps, complete);
        return TextureInspectionStatus.Success;
    }

    private static int CountCompleteTexMips(int width, int height, int levels, TexturePixelFormat format,
        int availableBytes, out bool complete)
    {
        long total = 0;
        for (int level = 0; level < levels; level++)
            total += GetMipSize(Math.Max(1, width >> level), Math.Max(1, height >> level), format);

        complete = total <= availableBytes;
        int count = levels;
        while (count > 1 && total > availableBytes)
        {
            count--;
            total -= GetMipSize(Math.Max(1, width >> count), Math.Max(1, height >> count), format);
        }
        return count;
    }

    private static bool HasCompleteDdsData(int availableBytes, int width, int height, int levels,
        TexturePixelFormat format)
    {
        long total = 0;
        for (int level = 0; level < levels; level++)
        {
            total += GetMipSize(Math.Max(1, width >> level), Math.Max(1, height >> level), format);
            if (total > availableBytes)
                return false;
        }
        return true;
    }

    private static long GetMipSize(int width, int height, TexturePixelFormat format)
    {
        if (format == TexturePixelFormat.Rgba16Float)
            return (long)width * height * 8;
        if (format is TexturePixelFormat.Rgba8 or TexturePixelFormat.Bgra8)
            return (long)width * height * 4;

        long blocks = ((width + 3L) / 4) * ((height + 3L) / 4);
        return blocks * (format is TexturePixelFormat.Bc1 or TexturePixelFormat.Bc4 or
            TexturePixelFormat.Bc4Snorm ? 8 : 16);
    }

    private static TexturePixelFormat DxgiToFormat(uint value) => value switch
    {
        71 or 72 => TexturePixelFormat.Bc1,
        74 or 75 => TexturePixelFormat.Bc2,
        77 or 78 => TexturePixelFormat.Bc3,
        80 => TexturePixelFormat.Bc4,
        81 => TexturePixelFormat.Bc4Snorm,
        83 => TexturePixelFormat.Bc5,
        84 => TexturePixelFormat.Bc5Snorm,
        98 or 99 => TexturePixelFormat.Bc7,
        28 => TexturePixelFormat.Rgba8,
        87 => TexturePixelFormat.Bgra8,
        _ => TexturePixelFormat.Unknown
    };

    private static TexturePixelFormat FourCcToFormat(uint value)
    {
        if (value == MakeFourCc('D', 'X', 'T', '1')) return TexturePixelFormat.Bc1;
        if (value == MakeFourCc('D', 'X', 'T', '3')) return TexturePixelFormat.Bc2;
        if (value == MakeFourCc('D', 'X', 'T', '5')) return TexturePixelFormat.Bc3;
        if (value == MakeFourCc('A', 'T', 'I', '1') || value == MakeFourCc('B', 'C', '4', 'U')) return TexturePixelFormat.Bc4;
        if (value == MakeFourCc('B', 'C', '4', 'S')) return TexturePixelFormat.Bc4Snorm;
        if (value == MakeFourCc('A', 'T', 'I', '2') || value == MakeFourCc('B', 'C', '5', 'U')) return TexturePixelFormat.Bc5;
        if (value == MakeFourCc('B', 'C', '5', 'S')) return TexturePixelFormat.Bc5Snorm;
        if (value == MakeFourCc('B', 'C', '7', ' ')) return TexturePixelFormat.Bc7;
        return TexturePixelFormat.Unknown;
    }

    private static uint MakeFourCc(char a, char b, char c, char d) =>
        (uint)(byte)a | ((uint)(byte)b << 8) | ((uint)(byte)c << 16) | ((uint)(byte)d << 24);
}
