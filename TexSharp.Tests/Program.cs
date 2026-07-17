using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
#if BCN_ENCODER_AVAILABLE
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
#endif
using TexSharp.Formats;
using TexSharp.Formats.BC;
using TexSharp.Containers.Tex;
using TexSharp.Containers.Dds;

namespace TexSharp.Tests
{
    class Program
    {
        static int _failures = 0;

        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                if (args[0] == "--golden-update")
                {
                    ComputeAndSaveGoldenHashes();
                    return;
                }
                if (args[0] == "--benchmark")
                {
                    Benchmark(args.Length > 1 ? args[1] : null);
                    return;
                }
                if (args[0] == "--compare")
                {
                    string? corpusPath = args.Length > 1 ? args[1] : null;
                    int maxFiles = args.Length > 2 && int.TryParse(args[2], out int parsedLimit) ? parsedLimit : 100;
                    CompareDetailed(corpusPath, maxFiles);
                    return;
                }
                if (args[0] == "--png")
                {
                    if (args.Length == 3)
                    {
                        TextureExporter.SaveToPng(args[1], args[2]);
                        Console.WriteLine($"[PNG OK] {args[2]}");
                        return;
                    }
                    TestPngExport();
                    return;
                }
                if (args[0] == "--test" && args.Length > 1)
                {
                    ProcessCorpus(args[1]);
                    Finish();
                    return;
                }
                if (args[0] == "--corpus" && args.Length > 1)
                {
                    ProcessCorpus(args[1]);
                    Finish();
                    return;
                }
                if (args[0] == "--unit")
                {
                    RunUnitTests();
                    Finish();
                    return;
                }
            }

            Console.WriteLine("--- TexSharp Technical Test ---");
            Console.WriteLine($"Hardware Acceleration: {TextureEngine.IsHardwareAccelerated}");

            RunUnitTests();
            ProcessSamples();
            VerifyGoldenHashes();
            Finish();
        }

        static void RunUnitTests()
        {
            TestBc1Decoding();
            TestBc3Decoding();
            TestBc5Decoding();
            TestBc2Decoding();
            TestBc4Decoding();
            TestRgba16fDecoding();
            TestShortBuffers();
            TestBc7Invariants();
            TestBc7Interpolation();
            TestBc7TableSpotChecks();
            TestTexMipmaps();
            TestDdsMipmaps();
            TestDdsUncompressedChannels();
            TestDdsSnormChannels();
            TestContainerValidation();
            TestTextureInspection();
            TestImageEdgeDimensions();
            TestVersionedGoldenCorpus();
            TestStreamingPngExport();
        }

        static void TestStreamingPngExport()
        {
            const int width = 257;
            const int height = 129;
            uint[] pixels = new uint[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (uint)(i * 2654435761u) | 0xFF000000u;

            string path = Path.Combine(Path.GetTempPath(), $"texsharp-{Guid.NewGuid():N}.png");
            try
            {
                TextureExporter.SaveToPng(pixels, width, height, path);
                byte[] png = File.ReadAllBytes(path);
                using var compressed = new MemoryStream();
                int offset = 8;
                int idatChunks = 0;
                while (offset + 12 <= png.Length)
                {
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
                    string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
                    if (type == "IDAT")
                    {
                        compressed.Write(png, offset + 8, length);
                        idatChunks++;
                    }
                    offset += 12 + length;
                }

                compressed.Position = 0;
                using var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
                using var raw = new MemoryStream();
                zlib.CopyTo(raw);
                Check("PNG streaming IDAT round-trip", idatChunks > 1 && raw.Length == (long)(width * 4 + 1) * height);
            }
            finally
            {
                File.Delete(path);
            }
        }

        static void TestDdsSnormChannels()
        {
            byte[] bc4Block = { 0x81, 0x7F, 0, 0, 0, 0, 0, 0 };
            uint bc4Pixel = new DdsReader(CreateDx10Dds(81, bc4Block)).Decode()[0];
            Check("DDS BC4 SNORM maps -1 to zero", bc4Pixel == 0xFF000000u);

            byte[] bc5Block =
            {
                0x7F, 0x81, 0, 0, 0, 0, 0, 0,
                0x81, 0x7F, 0, 0, 0, 0, 0, 0
            };
            uint bc5Pixel = new DdsReader(CreateDx10Dds(84, bc5Block)).Decode()[0];
            Check("DDS BC5 SNORM preserves signed channel extremes", bc5Pixel == 0xFF0000FFu);

            byte[] fractionalBlock = { 0x9C, 0x33, 0x02, 0, 0, 0, 0, 0 };
            Span<uint> fractionalOutput = stackalloc uint[16];
            Bc4SnormBlock.DecodeBlock(fractionalBlock, fractionalOutput);
            Check("BC4 SNORM preserves fractional interpolation", (fractionalOutput[0] & 0xFF) == 57);
        }

        static void TestVersionedGoldenCorpus()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Corpus", "golden.json");
            GoldenCorpusCase[]? cases = JsonSerializer.Deserialize<GoldenCorpusCase[]>(File.ReadAllText(path));
            if (cases is null || cases.Length == 0)
            {
                Check("Versioned golden corpus is available", false);
                return;
            }

            foreach (GoldenCorpusCase testCase in cases)
            {
                byte[] data = Convert.FromHexString(testCase.DataHex);
                uint[] pixels = testCase.Container switch
                {
                    "tex" => new TexReader(data).Decode(),
                    "dds" => new DdsReader(data).Decode(),
                    _ => throw new InvalidDataException($"Unknown corpus container: {testCase.Container}")
                };

                Check($"Golden corpus: {testCase.Name}", ComputeHash(pixels) == testCase.Sha256);
            }
        }

        sealed class GoldenCorpusCase
        {
            public string Name { get; set; } = "";
            public string Container { get; set; } = "";
            public string DataHex { get; set; } = "";
            public string Sha256 { get; set; } = "";
        }

        static void Finish()
        {
            Console.WriteLine(_failures == 0
                ? "\n[ALL TESTS PASSED]"
                : $"\n[{_failures} TEST(S) FAILED]");

            Environment.ExitCode = _failures == 0 ? 0 : 1;
        }

        static void Check(string name, bool condition)
        {
            Console.WriteLine(condition ? $"[PASS] {name}" : $"[FAIL] {name}");
            if (!condition) _failures++;
        }

        // --- BC1 / BC3 / BC5 (regression) ---

        static void TestBc1Decoding()
        {
            byte[] block = { 0x00, 0xF8, 0x1F, 0x00, 0xAA, 0xAA, 0xAA, 0xAA };
            uint[] outp = new uint[16];
            Bc1Block.DecodeBlock(block, outp);
            Check("BC1 decodes without error", outp[0] != 0);
        }

        static void TestBc3Decoding()
        {
            byte[] block = { 0xFF, 0x00, 0, 0, 0, 0, 0, 0, 0x00, 0xF8, 0x1F, 0x00, 0xAA, 0xAA, 0xAA, 0xAA };
            uint[] outp = new uint[16];
            Bc3Block.DecodeBlock(block, outp);
            uint alpha = (outp[0] >> 24) & 0xFF;
            Check("BC3 alpha is 255", alpha == 255);
        }

        static void TestBc5Decoding()
        {
            byte[] block = {
                0xAA, 0x00, 0, 0, 0, 0, 0, 0,
                0x55, 0x00, 0, 0, 0, 0, 0, 0 };
            uint[] outp = new uint[16];
            Bc5Block.DecodeBlock(block, outp);
            uint r = outp[0] & 0xFF;
            uint g = (outp[0] >> 8) & 0xFF;
            Check("BC5 R/G channels", r == 0xAA && g == 0x55);
        }

        static void TestBc2Decoding()
        {
            // Alpha explícito 0xF (15 -> 255) en todos los píxeles; color BC1.
            byte[] block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xFF; // nibbles 0xF -> 255
            block[8] = 0x00; block[9] = 0xF8; block[10] = 0x1F; block[11] = 0x00;
            uint[] outp = new uint[16];
            Bc2Block.DecodeBlock(block, outp);
            uint a = (outp[0] >> 24) & 0xFF;
            Check("BC2 explicit alpha 255", a == 255);
        }

        static void TestBc4Decoding()
        {
            byte[] block = { 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 };
            uint[] outp = new uint[16];
            Bc4Block.DecodeBlock(block, outp);
            // a0=a1=255, indices 0 -> 255.
            byte v = (byte)(outp[0] & 0xFF);
            Check("BC4 single channel 255", v == 255);
        }

        static void TestShortBuffers()
        {
            bool blockDidNotThrow = true;
            try
            {
                Bc1Block.DecodeBlock(new byte[8], new uint[15]);
                Bc2Block.DecodeBlock(new byte[16], new uint[15]);
                Bc3Block.DecodeBlock(new byte[16], new uint[15]);
                Bc4Block.DecodeBlock(new byte[8], new uint[15]);
                Bc5Block.DecodeBlock(new byte[16], new uint[15]);
                Bc7Block.DecodeBlock(new byte[16], new uint[15]);
            }
            catch
            {
                blockDidNotThrow = false;
            }
            Check("Block decoders accept short output buffers", blockDidNotThrow);

            uint[] output = Enumerable.Repeat(0xAABBCCDDu, 16).ToArray();
            BcImageDecoder.DecodeImage(new byte[7], 4, 4, DecodedFormat.Bc1, output);
            Check("Image decoder leaves output unchanged for truncated data", output.All(pixel => pixel == 0xAABBCCDDu));

            bool smallOutputThrows = false;
            try
            {
                BcImageDecoder.DecodeImage(new byte[8], 4, 4, DecodedFormat.Bc1, new uint[15]);
            }
            catch (ArgumentException)
            {
                smallOutputThrows = true;
            }
            Check("Image decoder rejects a short output buffer", smallOutputThrows);
        }

        static void TestRgba16fDecoding()
        {
            byte[] pixel = { 0x00, 0x3C, 0x00, 0x38, 0x00, 0x00, 0x00, 0x3C };
            uint[] output = new uint[1];
            BcImageDecoder.DecodeImage(pixel, 1, 1, DecodedFormat.Rgba16f, output);
            Check("RGBA16F converts Half channels to RGBA8", output[0] == 0xFF0080FFu);

            byte[] signedPixel = { 0x00, 0xBC, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x00 };
            BcImageDecoder.DecodeImage(signedPixel, 1, 1, DecodedFormat.Rgba16f, output,
                Rgba16fColorMapping.SignedNormalizedOpaque);
            Check("RGBA16F signed preview maps -1..1 to RGB", output[0] == 0xFFFF8000u);

            byte[] tex = new byte[20];
            tex[0] = 0x54; tex[1] = 0x45; tex[2] = 0x58;
            tex[4] = 1; tex[6] = 1; tex[9] = (byte)TexFormat.Rgba16f;
            Array.Copy(pixel, 0, tex, 12, pixel.Length);
            Check("TEX format 21 is supported", new TexReader(tex).Decode()[0] == 0xFF0080FFu);
        }

        // --- BC7 ---

        static void TestBc7Invariants()
        {
            for (int mode = 0; mode < 8; mode++)
            {
                // Bloque "todo a 1" salvo el prefijo de modo (que deja el resto a 1).
                byte[] maxBlock = MakeBc7UniformBlock(mode, allOnes: true);
                uint[] outp = new uint[16];
                Bc7Block.DecodeBlock(maxBlock, outp);
                bool allWhite = true;
                for (int i = 0; i < 16; i++) if (outp[i] != 0xFFFFFFFF) allWhite = false;
                Check($"BC7 Mode {mode} all-max -> white", allWhite);

                // Bloque "todo a 0" (salvo el bit de modo).
                byte[] zeroBlock = MakeBc7UniformBlock(mode, allOnes: false);
                uint[] outp2 = new uint[16];
                Bc7Block.DecodeBlock(zeroBlock, outp2);
                uint expected = (mode <= 3) ? 0xFF000000u : 0x00000000u;
                bool allZero = true;
                for (int i = 0; i < 16; i++) if (outp2[i] != expected) allZero = false;
                Check($"BC7 Mode {mode} all-zero -> {expected:X8}", allZero);
            }
        }

        // Construye un bloque BC7 con todos los endpoints iguales (máximo o cero),
        // dejando todos los bits excepto el prefijo de modo a 1 o 0.
        static byte[] MakeBc7UniformBlock(int mode, bool allOnes)
        {
            byte[] block = new byte[16];
            // byte0: bit `mode` = 1; el resto de byte0 = allOnes.
            int mask = ~((1 << (mode + 1)) - 1) & 0xFF;
            block[0] = (byte)((1 << mode) | (allOnes ? mask : 0));
            for (int i = 1; i < 16; i++) block[i] = allOnes ? (byte)0xFF : (byte)0x00;
            return block;
        }

        static void TestBc7Interpolation()
        {
            // Modo 6: endpoint0 = 127 (->254 con P=0), endpoint1 = 0.
            // pixel0 índice 7 (3 bits) -> peso 60 -> 16 por canal.
            byte[] block = new byte[16];
            var w = new BitWriter();
            // Modo 6: bits de modo (7 bits: 6 ceros + 1 uno)
            for (int i = 0; i < 6; i++) w.WriteBit(0);
            w.WriteBit(1);
            // Modo 6: sin partition, rotation ni indexMode (todo 0 bits)
            // BC7 almacena endpoints por canal: R0,R1,G0,G1,B0,B1,A0,A1 (7 bits c/u)
            int[] ep0 = { 127, 127, 127, 127 };
            int[] ep1 = { 0, 0, 0, 0 };
            w.Write(7, ep0[0]); w.Write(7, ep1[0]); // R0,R1
            w.Write(7, ep0[1]); w.Write(7, ep1[1]); // G0,G1
            w.Write(7, ep0[2]); w.Write(7, ep1[2]); // B0,B1
            w.Write(7, ep0[3]); w.Write(7, ep1[3]); // A0,A1
            // P-bits: 2, ambos 0
            w.WriteBit(0); w.WriteBit(0);
            // indices: pixel0 = 3 bits = 7, resto 4 bits = 15
            w.Write(3, 7);
            for (int i = 1; i < 16; i++) w.Write(4, 15);
            w.CopyTo(block);

            uint[] outp = new uint[16];
            Bc7Block.DecodeBlock(block, outp);
            Check("BC7 Mode6 pixel0 interpolated = 0x87878787", outp[0] == 0x87878787u);
            Check("BC7 Mode6 other pixels = 0", outp[1] == 0u && outp[15] == 0u);
        }

        static void TestBc7TableSpotChecks()
        {
            // Valores autorizados de DirectXTex/GIMP.
            int[] p2_0 = { 0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1 };
            int[] p2_17 = { 0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0 };
            int[] p3_0 = { 0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2 };
            int[] p3_17 = { 0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0 };

            bool ok = true;
            for (int i = 0; i < 16; i++)
            {
                if (Bc7Block.Partition2Debug(0)[i] != p2_0[i]) ok = false;
                if (Bc7Block.Partition2Debug(17)[i] != p2_17[i]) ok = false;
                if (Bc7Block.Partition3Debug(0)[i] != p3_0[i]) ok = false;
                if (Bc7Block.Partition3Debug(17)[i] != p3_17[i]) ok = false;
            }
            Check("BC7 partition tables match reference", ok);

            Check("BC7 fixup2[17] anchor = 2", Bc7Block.FixUp2Debug(17) == 2);
            Check("BC7 fixup3[17] anchors = 3,8", Bc7Block.FixUp3Debug(17, 1) == 3 && Bc7Block.FixUp3Debug(17, 2) == 8);
        }

        // --- Mipmaps ---

        static void TestTexMipmaps()
        {
            // .tex: 4x4 (BC1) con 2 mipmaps. Layout: menor(2x2) primero, mayor(4x4) al final.
            byte[] blueBlock = { 0x1F, 0x00, 0x00, 0x00, 0, 0, 0, 0 }; // Color0=Blue, indices 0
            byte[] redBlock = { 0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0 };  // Color0=Red, indices 0

            byte[] file = new byte[12 + 16];
            // magic TEX\0 (LE)
            file[0] = 0x54; file[1] = 0x45; file[2] = 0x58; file[3] = 0x00;
            file[4] = 4; file[5] = 0; // width
            file[6] = 4; file[7] = 0; // height
            file[8] = 0;              // type
            file[9] = 10;             // format BC1
            file[10] = 0;             // type2
            file[11] = 1;             // flags HasMipMaps

            Array.Copy(blueBlock, 0, file, 12, 8);       // mip menor
            Array.Copy(redBlock, 0, file, 12 + 8, 8);    // mip mayor

            var reader = new TexReader(file);
            Check("Tex mip count = 2", reader.MipLevels == 2);
            uint[] largest = reader.DecodeMip(0);
            uint[] smallest = reader.DecodeMip(1);
            Check("Tex largest mip size", largest.Length == 16);
            Check("Tex mip0 = red", (largest[0] & 0xFFFFFF) == 0x0000FF);
            Check("Tex mip1 = blue", (smallest[0] & 0xFFFFFF) == 0xFF0000);
        }

        static void TestDdsMipmaps()
        {
            // DDS mínimo con 2 mipmaps BC1. Header 128 bytes + datos.
            byte[] header = new byte[128];
            // Magic "DDS "
            header[0] = 0x44; header[1] = 0x44; header[2] = 0x53; header[3] = 0x20;
            header[4] = 124; // Size = 124
            header[8] = 0x02; // Flags (DDSD_HEIGHT|WIDTH simplified)
            BitConverter.GetBytes(4).CopyTo(header, 12); // Height
            BitConverter.GetBytes(4).CopyTo(header, 16);  // Width
            BitConverter.GetBytes(2u).CopyTo(header, 28); // MipMapCount = 2
            // PixelFormat (offset 76): size=32, flags=0x4 (FourCC), FourCC='DXT1'
            BitConverter.GetBytes(32).CopyTo(header, 76);
            BitConverter.GetBytes(0x4u).CopyTo(header, 80);
            BitConverter.GetBytes(DdsPixelFormat.MakeFourCC('D', 'X', 'T', '1')).CopyTo(header, 84);

            byte[] blueBlock = { 0x1F, 0x00, 0x00, 0x00, 0, 0, 0, 0 };
            byte[] redBlock = { 0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0 };

            byte[] file = new byte[128 + 16];
            Array.Copy(header, 0, file, 0, 128);
            Array.Copy(redBlock, 0, file, 128, 8);       // mip mayor primero (DDS)
            Array.Copy(blueBlock, 0, file, 128 + 8, 8);  // mip menor

            var reader = new DdsReader(file);
            Check("DDS mip count = 2", reader.MipLevels == 2);
            uint[] largest = reader.DecodeMip(0);
            uint[] smallest = reader.DecodeMip(1);
            Check("DDS mip0 = red", (largest[0] & 0xFFFFFF) == 0x0000FF);
            Check("DDS mip1 = blue", (smallest[0] & 0xFFFFFF) == 0xFF0000);
        }

        static void TestDdsUncompressedChannels()
        {
            uint expected = 0x40302010;

            byte[] rgba = CreateDx10Dds(28, new byte[] { 0x10, 0x20, 0x30, 0x40 });
            Check("DDS DX10 RGBA8 preserves channel order", new DdsReader(rgba).Decode()[0] == expected);

            byte[] bgra = CreateDx10Dds(87, new byte[] { 0x30, 0x20, 0x10, 0x40 });
            Check("DDS DX10 BGRA8 converts to RGBA order", new DdsReader(bgra).Decode()[0] == expected);
        }

        static byte[] CreateDx10Dds(uint dxgiFormat, byte[] pixel)
        {
            byte[] data = CreateDdsHeader(1, 1, 1, DdsPixelFormat.MakeFourCC('D', 'X', '1', '0'));
            Array.Resize(ref data, 148 + pixel.Length);
            BitConverter.GetBytes(dxgiFormat).CopyTo(data, 128);
            Array.Copy(pixel, 0, data, 148, pixel.Length);
            return data;
        }

        static void TestContainerValidation()
        {
            bool texHeaderRejected = false;
            try { _ = new TexReader(new byte[11]); }
            catch (ArgumentException) { texHeaderRejected = true; }
            Check("TEX truncated header is rejected", texHeaderRejected);

            byte[] unknownTex = new byte[12];
            unknownTex[0] = 0x54; unknownTex[1] = 0x45; unknownTex[2] = 0x58;
            unknownTex[4] = 4; unknownTex[6] = 4; unknownTex[9] = 0xFF;
            bool unknownTexRejected = false;
            try { _ = new TexReader(unknownTex); }
            catch (NotSupportedException) { unknownTexRejected = true; }
            Check("Unsupported TEX format is rejected", unknownTexRejected);

            byte[] dds = CreateDdsHeader(4, 4, 0, DdsPixelFormat.MakeFourCC('D', 'X', 'T', '1'));
            Array.Resize(ref dds, 136);
            var singleMip = new DdsReader(dds);
            Check("DDS without mip count has one mip", singleMip.MipLevels == 1);

            byte[] truncatedDx10 = CreateDdsHeader(4, 4, 1, DdsPixelFormat.MakeFourCC('D', 'X', '1', '0'));
            bool dx10Rejected = false;
            try { _ = new DdsReader(truncatedDx10); }
            catch (ArgumentException) { dx10Rejected = true; }
            Check("Truncated DDS DX10 header is rejected", dx10Rejected);

            byte[] excessiveMips = CreateDdsHeader(4, 4, 4, DdsPixelFormat.MakeFourCC('D', 'X', 'T', '1'));
            bool excessiveMipsRejected = false;
            try { _ = new DdsReader(excessiveMips); }
            catch (ArgumentException) { excessiveMipsRejected = true; }
            Check("DDS excessive mip count is rejected", excessiveMipsRejected);
        }

        static void TestTextureInspection()
        {
            byte[] tex = new byte[28];
            tex[0] = 0x54; tex[1] = 0x45; tex[2] = 0x58;
            BitConverter.GetBytes((ushort)4).CopyTo(tex, 4);
            BitConverter.GetBytes((ushort)4).CopyTo(tex, 6);
            tex[9] = 10;
            tex[11] = 1;
            TextureInspectionStatus texStatus = TextureInspector.Inspect(tex, out TextureInfo texInfo);
            Check("Inspector reads TEX metadata", texStatus == TextureInspectionStatus.Success &&
                texInfo.Container == TextureContainer.Tex && texInfo.Format == TexturePixelFormat.Bc1 &&
                texInfo.Width == 4 && texInfo.Height == 4 && texInfo.MipLevels == 2 && !texInfo.IsDataComplete);

            byte[] dds = CreateDdsHeader(8, 4, 1, DdsPixelFormat.MakeFourCC('D', 'X', 'T', '5'));
            Array.Resize(ref dds, 160);
            TextureInspectionStatus ddsStatus = TextureInspector.Inspect(dds, out TextureInfo ddsInfo);
            Check("Inspector reads DDS metadata", ddsStatus == TextureInspectionStatus.Success &&
                ddsInfo.Container == TextureContainer.Dds && ddsInfo.Format == TexturePixelFormat.Bc3 &&
                ddsInfo.Width == 8 && ddsInfo.Height == 4 && ddsInfo.MipLevels == 1 && ddsInfo.IsDataComplete);

            byte[] encrypted = { 0xC9, 0xE3, 0x44, 0x26 };
            Check("Inspector identifies encrypted Riot TEX",
                TextureInspector.Inspect(encrypted, out TextureInfo encryptedInfo) == TextureInspectionStatus.Encrypted &&
                encryptedInfo.Container == TextureContainer.Tex);

            tex[9] = 0xFF;
            TextureInspectionStatus unsupported = TextureInspector.Inspect(tex, out TextureInfo unsupportedInfo);
            Check("Inspector preserves unsupported TEX metadata", unsupported == TextureInspectionStatus.UnsupportedFormat &&
                unsupportedInfo.Container == TextureContainer.Tex && unsupportedInfo.Width == 4);
            Check("Inspector rejects truncated data",
                TextureInspector.Inspect(new byte[3], out _) == TextureInspectionStatus.InvalidData);

            (uint Dxgi, TexturePixelFormat Format)[] dxgiFormats =
            {
                (71, TexturePixelFormat.Bc1), (74, TexturePixelFormat.Bc2),
                (77, TexturePixelFormat.Bc3), (80, TexturePixelFormat.Bc4),
                (81, TexturePixelFormat.Bc4Snorm), (83, TexturePixelFormat.Bc5),
                (84, TexturePixelFormat.Bc5Snorm), (98, TexturePixelFormat.Bc7),
                (28, TexturePixelFormat.Rgba8), (87, TexturePixelFormat.Bgra8)
            };
            bool allDxgiFormatsMatch = true;
            foreach ((uint dxgi, TexturePixelFormat expected) in dxgiFormats)
            {
                byte[] sample = CreateDx10Dds(dxgi, new byte[64]);
                if (TextureInspector.Inspect(sample, out TextureInfo sampleInfo) != TextureInspectionStatus.Success ||
                    sampleInfo.Format != expected)
                    allDxgiFormatsMatch = false;
            }
            Check("Inspector maps all supported DDS DXGI formats", allDxgiFormatsMatch);

            byte[] hostileDds = CreateDdsHeader(int.MaxValue, int.MaxValue, 1,
                DdsPixelFormat.MakeFourCC('D', 'X', 'T', '1'));
            bool hostileDidNotThrow = true;
            try { _ = TextureInspector.Inspect(hostileDds, out _); }
            catch { hostileDidNotThrow = false; }
            Check("Inspector handles extreme dimensions without exceptions", hostileDidNotThrow);

            tex[9] = 10;
            _ = TextureInspector.Inspect(tex, out _);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
                _ = TextureInspector.Inspect(tex, out _);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check("Inspector allocates zero bytes", allocated == 0);
        }

        static void TestImageEdgeDimensions()
        {
            byte[] redBlock = { 0x00, 0xF8, 0x00, 0x00, 0, 0, 0, 0 };
            (int Width, int Height)[] dimensions = { (1, 1), (3, 2), (5, 3), (7, 9) };
            bool allRed = true;

            foreach (var (width, height) in dimensions)
            {
                int size = BcImageDecoder.MipSize(width, height, DecodedFormat.Bc1);
                byte[] data = new byte[size];
                for (int offset = 0; offset < data.Length; offset += redBlock.Length)
                    Array.Copy(redBlock, 0, data, offset, redBlock.Length);

                uint[] pixels = new uint[width * height];
                BcImageDecoder.DecodeImage(data, width, height, DecodedFormat.Bc1, pixels);
                if (pixels.Any(pixel => (pixel & 0x00FFFFFF) != 0x0000FF))
                    allRed = false;
            }

            Check("BC1 decodes non-aligned dimensions", allRed);

            byte[] fullChannel = { 0xFF, 0x00, 0, 0, 0, 0, 0, 0 };
            Check("BC4 decodes non-aligned dimensions",
                ValidateNonAlignedChannels(dimensions, DecodedFormat.Bc4, fullChannel, 0xFFFFFFFFu));

            byte[] fullChannels = new byte[16];
            fullChannel.CopyTo(fullChannels, 0);
            fullChannel.CopyTo(fullChannels, 8);
            Check("BC5 decodes non-aligned dimensions",
                ValidateNonAlignedChannels(dimensions, DecodedFormat.Bc5, fullChannels, 0xFFFFFFFFu));
        }

        static bool ValidateNonAlignedChannels((int Width, int Height)[] dimensions, DecodedFormat format,
            byte[] block, uint expected)
        {
            foreach (var (width, height) in dimensions)
            {
                byte[] data = new byte[BcImageDecoder.MipSize(width, height, format)];
                for (int offset = 0; offset < data.Length; offset += block.Length)
                    block.CopyTo(data, offset);

                uint[] pixels = new uint[width * height];
                BcImageDecoder.DecodeImage(data, width, height, format, pixels);
                if (pixels.Any(pixel => pixel != expected)) return false;
            }

            return true;
        }

        static byte[] CreateDdsHeader(int width, int height, int mipLevels, uint fourCC)
        {
            byte[] header = new byte[128];
            header[0] = 0x44; header[1] = 0x44; header[2] = 0x53; header[3] = 0x20;
            BitConverter.GetBytes(124u).CopyTo(header, 4);
            BitConverter.GetBytes(height).CopyTo(header, 12);
            BitConverter.GetBytes(width).CopyTo(header, 16);
            BitConverter.GetBytes((uint)mipLevels).CopyTo(header, 28);
            BitConverter.GetBytes(32u).CopyTo(header, 76);
            BitConverter.GetBytes(0x4u).CopyTo(header, 80);
            BitConverter.GetBytes(fourCC).CopyTo(header, 84);
            return header;
        }

        static string GetSamplesPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples");
        }

        static List<string> GetSampleFiles(string samplesPath)
        {
            if (!Directory.Exists(samplesPath)) return new List<string>();
            return Directory.GetFiles(samplesPath, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        static uint[] DecodeSampleFile(string file)
        {
            byte[] data = File.ReadAllBytes(file);
            if (file.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                return new TexReader(data).DecodeMip(0);
            else
                return new DdsReader(data).DecodeMip(0);
        }

#if BCN_ENCODER_AVAILABLE
        static byte[] ExtractBaseMipData(string file, byte[] raw, out int w, out int h, out CompressionFormat cf,
            out DecodedFormat decodedFormat, out string formatName)
        {
            if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return ExtractDdsBaseMipData(raw, out w, out h, out cf, out decodedFormat, out formatName);

            if (raw.AsSpan().StartsWith(new byte[] { 0xC9, 0xE3, 0x44, 0x26 }))
                throw new NotSupportedException("Encrypted Riot TEX.");

            w = BitConverter.ToUInt16(raw, 4);
            h = BitConverter.ToUInt16(raw, 6);
            byte fmt = raw[9];
            int headerSize = 12;

            (cf, decodedFormat, formatName) = fmt switch
            {
                10 or 11 => (CompressionFormat.Bc1WithAlpha, DecodedFormat.Bc1, "BC1"),
                12 => (CompressionFormat.Bc3, DecodedFormat.Bc3, "BC3"),
                13 => (CompressionFormat.Bc7, DecodedFormat.Bc7, "BC7"),
                14 => (CompressionFormat.Bc5, DecodedFormat.Bc5, "BC5"),
                20 => (CompressionFormat.Bgra, DecodedFormat.Bgra8, "BGRA8"),
                _ => throw new NotSupportedException($"Format {fmt}")
            };

            int blockLen = cf == CompressionFormat.Bgra ? 1 : 4;
            int blockSize = cf switch
            {
                CompressionFormat.Bc1WithAlpha => 8,
                CompressionFormat.Bc4 => 8,
                CompressionFormat.Bgra => 4,
                _ => 16
            };
            int blocksX = (w + blockLen - 1) / blockLen;
            int blocksY = (h + blockLen - 1) / blockLen;
            int baseMipSize = blocksX * blocksY * blockSize;

            byte[] compressed = new byte[raw.Length - headerSize];
            Array.Copy(raw, headerSize, compressed, 0, compressed.Length);

            // .tex stores mips smallest-to-largest; base mip is at the end.
            if (compressed.Length > baseMipSize)
            {
                int offset = compressed.Length - baseMipSize;
                byte[] baseMip = new byte[baseMipSize];
                Array.Copy(compressed, offset, baseMip, 0, baseMipSize);
                return baseMip;
            }
            return compressed;
        }

        static byte[] ExtractDdsBaseMipData(byte[] raw, out int w, out int h, out CompressionFormat cf,
            out DecodedFormat decodedFormat, out string formatName)
        {
            if (raw.Length < 128 || BitConverter.ToUInt32(raw, 0) != 0x20534444)
                throw new InvalidDataException("Invalid DDS header.");

            w = checked((int)BitConverter.ToUInt32(raw, 16));
            h = checked((int)BitConverter.ToUInt32(raw, 12));
            uint fourCc = BitConverter.ToUInt32(raw, 84);
            int headerSize = 128;

            if (fourCc == DdsPixelFormat.MakeFourCC('D', 'X', '1', '0'))
            {
                if (raw.Length < 148) throw new InvalidDataException("Truncated DDS DX10 header.");
                uint dxgi = BitConverter.ToUInt32(raw, 128);
                headerSize = 148;
                (cf, decodedFormat, formatName) = dxgi switch
                {
                    71 or 72 => (CompressionFormat.Bc1WithAlpha, DecodedFormat.Bc1, "BC1"),
                    74 or 75 => (CompressionFormat.Bc2, DecodedFormat.Bc2, "BC2"),
                    77 or 78 => (CompressionFormat.Bc3, DecodedFormat.Bc3, "BC3"),
                    80 => (CompressionFormat.Bc4, DecodedFormat.Bc4, "BC4"),
                    83 => (CompressionFormat.Bc5, DecodedFormat.Bc5, "BC5"),
                    98 or 99 => (CompressionFormat.Bc7, DecodedFormat.Bc7, "BC7"),
                    28 => (CompressionFormat.Rgba, DecodedFormat.Rgba8, "RGBA8"),
                    87 => (CompressionFormat.Bgra, DecodedFormat.DdsBgra8, "BGRA8"),
                    81 or 84 => throw new NotSupportedException("BC4/BC5 SNORM comparison is not equivalent."),
                    _ => throw new NotSupportedException($"DXGI format {dxgi}")
                };
            }
            else
            {
                (cf, decodedFormat, formatName) = fourCc switch
                {
                    var value when value == DdsPixelFormat.MakeFourCC('D', 'X', 'T', '1') => (CompressionFormat.Bc1WithAlpha, DecodedFormat.Bc1, "BC1"),
                    var value when value == DdsPixelFormat.MakeFourCC('D', 'X', 'T', '3') => (CompressionFormat.Bc2, DecodedFormat.Bc2, "BC2"),
                    var value when value == DdsPixelFormat.MakeFourCC('D', 'X', 'T', '5') => (CompressionFormat.Bc3, DecodedFormat.Bc3, "BC3"),
                    var value when value == DdsPixelFormat.MakeFourCC('A', 'T', 'I', '1') => (CompressionFormat.Bc4, DecodedFormat.Bc4, "BC4"),
                    var value when value == DdsPixelFormat.MakeFourCC('B', 'C', '4', 'U') => (CompressionFormat.Bc4, DecodedFormat.Bc4, "BC4"),
                    var value when value == DdsPixelFormat.MakeFourCC('A', 'T', 'I', '2') => (CompressionFormat.Bc5, DecodedFormat.Bc5, "BC5"),
                    var value when value == DdsPixelFormat.MakeFourCC('B', 'C', '5', 'U') => (CompressionFormat.Bc5, DecodedFormat.Bc5, "BC5"),
                    _ => throw new NotSupportedException($"DDS FourCC 0x{fourCc:X8}")
                };
            }

            int blockLength = cf is CompressionFormat.Rgba or CompressionFormat.Bgra ? 1 : 4;
            int blockSize = cf switch
            {
                CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc4 => 8,
                CompressionFormat.Rgba or CompressionFormat.Bgra => 4,
                _ => 16
            };
            int size = ((w + blockLength - 1) / blockLength) * ((h + blockLength - 1) / blockLength) * blockSize;
            if (raw.Length - headerSize < size) throw new InvalidDataException("DDS base mip is truncated.");
            return raw.AsSpan(headerSize, size).ToArray();
        }
#endif

        static string ComputeHash(uint[] pixels)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(pixels.AsSpan());
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        static void ProcessSamples()
        {
            ProcessCorpus(GetSamplesPath(), required: false);
        }

        static void ProcessCorpus(string dirPath, bool required = true)
        {
            if (!Directory.Exists(dirPath))
            {
                Console.WriteLine($"[WARN] Corpus directory not found: {dirPath}");
                if (required) _failures++;
                return;
            }

            var files = GetSampleFiles(dirPath);
            if (files.Count == 0)
            {
                Console.WriteLine($"[WARN] No TEX or DDS files found in: {dirPath}");
                if (required) _failures++;
                return;
            }

            int failed = 0;

            foreach (var file in files)
            {
                try
                {
                    uint[] pixels = DecodeSampleFile(file);
                    Console.WriteLine($"[SUCCESS] {Path.GetFileName(file)} | {pixels.Length} px");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAILURE] {Path.GetFileName(file)} | {ex.Message}");
                    failed++;
                }
            }
            _failures += failed;
            Console.WriteLine($"\nCorpus: OK={files.Count - failed} FAIL={failed} TOTAL={files.Count}");
        }

        static void ComputeAndSaveGoldenHashes()
        {
            var files = GetSampleFiles(GetSamplesPath());
            if (files.Count == 0)
            {
                Console.WriteLine("[WARN] No sample files found.");
                return;
            }

            var hashes = new Dictionary<string, string>();
            int ok = 0, fail = 0;
            foreach (var file in files)
            {
                string rel = Path.GetRelativePath(GetSamplesPath(), file);
                try
                {
                    uint[] pixels = DecodeSampleFile(file);
                    hashes[rel] = ComputeHash(pixels);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAIL] {rel} | {ex.Message}");
                    fail++;
                }
            }

            string jsonPath = Path.Combine(GetSamplesPath(), "golden.json");
            string json = JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"\nGolden hashes saved: {jsonPath}");
            Console.WriteLine($"OK={ok}  FAIL={fail}  TOTAL={ok + fail}");
        }

        static void VerifyGoldenHashes()
        {
            string jsonPath = Path.Combine(GetSamplesPath(), "golden.json");
            if (!File.Exists(jsonPath)) return;

            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(jsonPath));
            if (hashes == null || hashes.Count == 0) return;

            int verified = 0, mismatched = 0, missing = 0;
            foreach (var kvp in hashes)
            {
                string fullPath = Path.Combine(GetSamplesPath(), kvp.Key);
                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"[MISSING] {kvp.Key}");
                    missing++;
                    continue;
                }
                try
                {
                    uint[] pixels = DecodeSampleFile(fullPath);
                    string current = ComputeHash(pixels);
                    if (current == kvp.Value)
                        verified++;
                    else
                    {
                        Console.WriteLine($"[HASH MISMATCH] {kvp.Key}");
                        mismatched++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DECODE ERROR] {kvp.Key} | {ex.Message}");
                    mismatched++;
                }
            }

            if (mismatched > 0 || missing > 0)
            {
                Console.WriteLine($"\n[GOLDEN HASHES] verified={verified} mismatched={mismatched} missing={missing}");
                _failures += mismatched + missing;
            }
            else
            {
                Console.WriteLine($"\n[GOLDEN HASHES] {verified}/{verified} match");
            }
        }

        static void TestPngExport()
        {
            var files = GetSampleFiles(GetSamplesPath());
            string dest = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            int count = 0;
            foreach (var file in files.Take(5))
            {
                string name = Path.GetFileNameWithoutExtension(file) + ".png";
                string outPath = Path.Combine(dest, name);
                try
                {
                    TextureExporter.SaveToPng(file, outPath);
                    var info = new FileInfo(outPath);
                    Console.WriteLine($"[PNG OK] {name} | {info.Length,10} bytes");
                    count++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PNG FAIL] {name} | {ex.Message}");
                }
            }
            Console.WriteLine($"\nExported {count}/5 PNGs to {dest}");
        }


        static void CompareDetailed(string? corpusPath, int maxFiles)
        {
#if BCN_ENCODER_AVAILABLE
            string path = corpusPath ?? GetSamplesPath();
            var files = GetSampleFiles(path);
            if (files.Count == 0)
            {
                Console.Error.WriteLine($"[UNAVAILABLE] No TEX or DDS files found in: {path}");
                Environment.ExitCode = 2;
                return;
            }
            if (maxFiles < 0)
            {
                Console.Error.WriteLine("[INVALID] max-files must be zero (all files) or a positive number.");
                Environment.ExitCode = 2;
                return;
            }

            var bcn = new BcDecoder();
            int filesToCompare = maxFiles == 0 ? files.Count : Math.Min(maxFiles, files.Count);
            long totalPixels = 0, exactMatch = 0, within1 = 0, beyond1 = 0;
            int maxDiffR = 0, maxDiffG = 0, maxDiffB = 0, maxDiffA = 0;
            int comparedFiles = 0, skippedFiles = 0, failedFiles = 0;
            var formats = new Dictionary<string, ComparisonStats>(StringComparer.Ordinal);

            Console.WriteLine("--- TexSharp vs BCnEncoder ---");
            Console.WriteLine($"Corpus: {Path.GetFullPath(path)}");
            Console.WriteLine($"Files:  {filesToCompare}/{files.Count}");

            for (int fi = 0; fi < filesToCompare; fi++)
            {
                string file = files[fi];
                try
                {
                    byte[] raw = File.ReadAllBytes(file);
                    byte[] baseMip = ExtractBaseMipData(file, raw, out int w, out int h, out CompressionFormat cf,
                        out DecodedFormat decodedFormat, out string formatName);

                    if (!formats.TryGetValue(formatName, out ComparisonStats? stats))
                        formats.Add(formatName, stats = new ComparisonStats());

                    uint[] tsPixels = new uint[checked(w * h)];
                    if (!stats.Warmed)
                    {
                        BcImageDecoder.DecodeImage(baseMip, w, h, decodedFormat, tsPixels);
                        if (cf is not (CompressionFormat.Bgra or CompressionFormat.Rgba))
                            _ = bcn.DecodeRaw(new Memory<byte>(baseMip), w, h, cf);
                        stats.Warmed = true;
                    }

                    var parseTimer = Stopwatch.StartNew();
                    if (file.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                        _ = new TexReader(raw);
                    else
                        _ = new DdsReader(raw);
                    parseTimer.Stop();

                    long tsAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    long tsStarted = Stopwatch.GetTimestamp();
                    BcImageDecoder.DecodeImage(baseMip, w, h, decodedFormat, tsPixels);
                    long tsTicks = Stopwatch.GetTimestamp() - tsStarted;
                    long tsAllocated = GC.GetAllocatedBytesForCurrentThread() - tsAllocatedBefore;

                    long bcnAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    long bcnStarted = Stopwatch.GetTimestamp();
                    ColorRgba32[] bcnResult = cf is CompressionFormat.Bgra or CompressionFormat.Rgba
                        ? MemoryMarshal.Cast<byte, ColorRgba32>(baseMip.AsSpan()).ToArray()
                        : bcn.DecodeRaw(new Memory<byte>(baseMip), w, h, cf);
                    long bcnTicks = Stopwatch.GetTimestamp() - bcnStarted;
                    long bcnAllocated = GC.GetAllocatedBytesForCurrentThread() - bcnAllocatedBefore;

                    int pxCount = Math.Min(tsPixels.Length, bcnResult.Length);
                    stats.Files++;
                    stats.InputBytes += baseMip.Length;
                    stats.ParseTicks += parseTimer.ElapsedTicks;
                    stats.TexSharpTicks += tsTicks;
                    stats.BcnTicks += bcnTicks;
                    stats.TexSharpAllocated += tsAllocated;
                    stats.BcnAllocated += bcnAllocated;

                    for (int i = 0; i < pxCount; i++)
                    {
                        totalPixels++;
                        uint t = tsPixels[i];
                        ColorRgba32 b = bcnResult[i];
                        int dr = Math.Abs((int)(t & 0xFF) - b.r);
                        int dg = Math.Abs((int)((t >> 8) & 0xFF) - b.g);
                        int db = Math.Abs((int)((t >> 16) & 0xFF) - b.b);
                        int da = Math.Abs((int)((t >> 24) & 0xFF) - b.a);
                        maxDiffR = Math.Max(maxDiffR, dr);
                        maxDiffG = Math.Max(maxDiffG, dg);
                        maxDiffB = Math.Max(maxDiffB, db);
                        maxDiffA = Math.Max(maxDiffA, da);
                        if (dr == 0 && dg == 0 && db == 0 && da == 0) { exactMatch++; stats.Exact++; }
                        else if (dr <= 1 && dg <= 1 && db <= 1 && da <= 1) { within1++; stats.Within1++; }
                        else { beyond1++; stats.Beyond1++; }
                    }

                    comparedFiles++;
                    if ((fi + 1) % 100 == 0 || fi + 1 == filesToCompare)
                        Console.WriteLine($"[{fi + 1}/{filesToCompare}] compared={comparedFiles} skipped={skippedFiles} failed={failedFiles}");
                }
                catch (NotSupportedException ex)
                {
                    skippedFiles++;
                    if (skippedFiles <= 10) Console.WriteLine($"[SKIP] {Path.GetFileName(file)} | {ex.Message}");
                }
                catch (Exception ex)
                {
                    failedFiles++;
                    Console.WriteLine($"[ERROR] {Path.GetFileName(file)} | {ex.Message}");
                }
            }

            Console.WriteLine("\n--- PER-FORMAT PERFORMANCE ---");
            Console.WriteLine("Format Files Input MiB Parse ms TexSharp MiB/s BCnEncoder MiB/s Ratio TS B/file BCn B/file");
            foreach (var (format, stats) in formats.OrderBy(pair => pair.Key))
            {
                double inputMiB = stats.InputBytes / (1024.0 * 1024.0);
                double tsRate = inputMiB / (stats.TexSharpTicks / (double)Stopwatch.Frequency);
                double bcnRate = inputMiB / (stats.BcnTicks / (double)Stopwatch.Frequency);
                double parseMs = stats.ParseTicks * 1000.0 / Stopwatch.Frequency;
                Console.WriteLine($"{format,-7} {stats.Files,5} {inputMiB,9:F1} {parseMs,8:F1} {tsRate,14:F1} {bcnRate,16:F1} {tsRate / bcnRate,5:F2}x {stats.TexSharpAllocated / stats.Files,9} {stats.BcnAllocated / stats.Files,10}");
            }

            Console.WriteLine("\n--- PIXEL COMPARISON ---");
            Console.WriteLine($"Files:        compared={comparedFiles} skipped={skippedFiles} failed={failedFiles}");
            Console.WriteLine($"Total pixels: {totalPixels}");
            Console.WriteLine($"Exact match:  {exactMatch} ({Percent(exactMatch, totalPixels):F2}%)");
            Console.WriteLine($"Within +/-1:  {within1} ({Percent(within1, totalPixels):F2}%)");
            Console.WriteLine($"Beyond +/-1:  {beyond1} ({Percent(beyond1, totalPixels):F2}%)");
            Console.WriteLine($"Max diff R={maxDiffR} G={maxDiffG} B={maxDiffB} A={maxDiffA}");
            if (failedFiles > 0) Environment.ExitCode = 1;
#else
            Console.Error.WriteLine("[UNAVAILABLE] BCnEncoder comparison was not compiled. " +
                "Build with -p:BcnEncoderPath=<path-to-BCnEncoder.dll> to enable --compare.");
            Environment.ExitCode = 2;
#endif
        }

#if BCN_ENCODER_AVAILABLE
        static double Percent(long value, long total) => total == 0 ? 0 : 100.0 * value / total;

        sealed class ComparisonStats
        {
            public bool Warmed;
            public int Files;
            public long InputBytes;
            public long ParseTicks;
            public long TexSharpTicks;
            public long BcnTicks;
            public long TexSharpAllocated;
            public long BcnAllocated;
            public long Exact;
            public long Within1;
            public long Beyond1;
        }
#endif

        static void Benchmark(string? corpusPath)
        {
            Console.WriteLine("--- TexSharp decode-core benchmark ---");
            Console.WriteLine("I/O, container parsing and output allocations are excluded from these measurements.");
            Console.WriteLine("Each case is warmed up and calibrated to run for at least 500 ms.");

            foreach (int size in new[] { 256, 1024 })
            {
                BenchmarkDecodeCore(DecodedFormat.Bc1, "BC1", size, size);
                BenchmarkDecodeCore(DecodedFormat.Bc2, "BC2", size, size);
                BenchmarkDecodeCore(DecodedFormat.Bc3, "BC3", size, size);
                BenchmarkDecodeCore(DecodedFormat.Bc4, "BC4", size, size);
                BenchmarkDecodeCore(DecodedFormat.Bc5, "BC5", size, size);
                BenchmarkDecodeCore(DecodedFormat.Bc7, "BC7", size, size);
            }

            string path = corpusPath ?? GetSamplesPath();
            if (Directory.Exists(path))
                BenchmarkContainerPipeline(path);
        }

        static void BenchmarkDecodeCore(DecodedFormat format, string name, int width, int height)
        {
            byte[] data = new byte[BcImageDecoder.MipSize(width, height, format)];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 149 + 37);

            uint[] output = new uint[width * height];
            for (int i = 0; i < 3; i++)
                BcImageDecoder.DecodeImage(data, width, height, format, output);

            int iterations = 1;
            TimeSpan calibration;
            do
            {
                var calibrationTimer = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                    BcImageDecoder.DecodeImage(data, width, height, format, output);
                calibrationTimer.Stop();
                calibration = calibrationTimer.Elapsed;
                if (calibration.TotalMilliseconds < 250) iterations *= 2;
            }
            while (calibration.TotalMilliseconds < 250);

            iterations = Math.Max(iterations, (int)Math.Ceiling(iterations * 500 / calibration.TotalMilliseconds));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            uint checksum = 0;
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                BcImageDecoder.DecodeImage(data, width, height, format, output);
                checksum ^= output[(i * 4099) & (output.Length - 1)];
            }
            timer.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            double inputMiB = (double)data.Length * iterations / (1024 * 1024);
            double outputMiB = (double)width * height * sizeof(uint) * iterations / (1024 * 1024);
            double allocatedPerOperation = (double)allocated / iterations;
            Console.WriteLine($"{name,-4} {width,4}x{height,-4}  in={inputMiB / timer.Elapsed.TotalSeconds,8:F1} MiB/s" +
                $"  out={outputMiB / timer.Elapsed.TotalSeconds,8:F1} MiB/s" +
                $"  alloc={allocatedPerOperation,6:F1} B/op  n={iterations,5}  checksum={checksum:X8}");
        }

        static void BenchmarkContainerPipeline(string corpusPath)
        {
            const long maxInputBytes = 256L * 1024 * 1024;
            var samples = new List<(string File, byte[] Data)>();
            long inputBytes = 0;

            foreach (string file in GetSampleFiles(corpusPath))
            {
                byte[] data = File.ReadAllBytes(file);
                if (inputBytes + data.Length > maxInputBytes) break;
                samples.Add((file, data));
                inputBytes += data.Length;
            }

            if (samples.Count == 0)
            {
                Console.WriteLine("[WARN] No corpus files were loaded for the container benchmark.");
                return;
            }

            foreach (var sample in samples)
                _ = DecodeSampleData(sample.File, sample.Data);

            long outputBytes = 0;
            uint checksum = 0;
            var timer = Stopwatch.StartNew();
            foreach (var sample in samples)
            {
                uint[] pixels = DecodeSampleData(sample.File, sample.Data);
                outputBytes += pixels.Length * sizeof(uint);
                checksum ^= pixels[0];
            }
            timer.Stop();

            double throughput = outputBytes / (1024.0 * 1024.0) / timer.Elapsed.TotalSeconds;
            Console.WriteLine($"Container pipeline ({samples.Count} files, {inputBytes / (1024.0 * 1024.0):F1} MiB input): {throughput:F1} MB/s  checksum={checksum:X8}");
        }

        static uint[] DecodeSampleData(string file, byte[] data)
        {
            return file.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                ? new TexReader(data).DecodeMip(0)
                : new DdsReader(data).DecodeMip(0);
        }

    // Escritor de bits little-endian para construir bloques BC7 en los tests.
    class BitWriter
    {
        private byte[] _buf = new byte[16];
        private int _pos = 0;

        public void WriteBit(int b)
        {
            if (b == 1) _buf[_pos >> 3] |= (byte)(1 << (_pos & 7));
            _pos++;
        }

        public void Write(int count, int value)
        {
            for (int i = 0; i < count; i++) WriteBit((value >> i) & 1);
        }

        public void CopyTo(byte[] target)
        {
            Array.Copy(_buf, target, 16);
        }
    }
}
}
