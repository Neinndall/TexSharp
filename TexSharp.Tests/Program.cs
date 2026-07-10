using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
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
                    CompareDetailed();
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
            TestContainerValidation();
            TestImageEdgeDimensions();
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

        static byte[] ExtractBaseMipData(byte[] raw, out int w, out int h, out CompressionFormat cf)
        {
            w = BitConverter.ToUInt16(raw, 4);
            h = BitConverter.ToUInt16(raw, 6);
            byte fmt = raw[9];
            int headerSize = 12;

            cf = fmt switch
            {
                10 or 11 => CompressionFormat.Bc1WithAlpha,
                12 => CompressionFormat.Bc3,
                13 => CompressionFormat.Bc7,
                14 => CompressionFormat.Bc5,
                20 => CompressionFormat.Bgra,
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

        static void CompareDetailed()
        {
            var files = GetSampleFiles(GetSamplesPath());
            if (files.Count == 0) return;

            var bcn = new BcDecoder();
            int maxFiles = Math.Min(100, files.Count);
            int totalPixels = 0, exactMatch = 0, within1 = 0, beyond1 = 0;
            int maxDiffR = 0, maxDiffG = 0, maxDiffB = 0, maxDiffA = 0;

            for (int fi = 0; fi < maxFiles; fi++)
            {
                var file = files[fi];
                try
                {
                    byte[] raw = File.ReadAllBytes(file);
                    byte[] baseMip = ExtractBaseMipData(raw, out int w, out int h, out CompressionFormat cf);

                    // TexSharp
                    uint[] tsPixels = DecodeSampleFile(file);

                    // BCnEncoder
                    ColorRgba32[] bcnResult;
                    if (cf == CompressionFormat.Bgra)
                        bcnResult = MemoryMarshal.Cast<byte, ColorRgba32>(baseMip.AsSpan()).ToArray();
                    else
                        bcnResult = bcn.DecodeRaw(new Memory<byte>(baseMip), w, h, cf);

                    int pxCount = Math.Min(tsPixels.Length, bcnResult.Length);
                    for (int i = 0; i < pxCount; i++)
                    {
                        totalPixels++;
                        uint t = tsPixels[i];
                        ColorRgba32 b = bcnResult[i];
                        int dr = Math.Abs((int)(t & 0xFF) - b.r);
                        int dg = Math.Abs((int)((t >> 8) & 0xFF) - b.g);
                        int db = Math.Abs((int)((t >> 16) & 0xFF) - b.b);
                        int da = Math.Abs((int)((t >> 24) & 0xFF) - b.a);
                        if (dr > maxDiffR) maxDiffR = dr;
                        if (dg > maxDiffG) maxDiffG = dg;
                        if (db > maxDiffB) maxDiffB = db;
                        if (da > maxDiffA) maxDiffA = da;
                        if (dr == 0 && dg == 0 && db == 0 && da == 0)
                            exactMatch++;
                        else if (dr <= 1 && dg <= 1 && db <= 1 && da <= 1)
                            within1++;
                        else
                            beyond1++;
                    }

                    string name = Path.GetFileName(file);
                    Console.WriteLine($"[{fi + 1}/{maxFiles}] {name} | px={pxCount} exact={exactMatch} ±1={within1} >1={beyond1}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{fi + 1}/{maxFiles}] {Path.GetFileName(file)} | ERROR: {ex.Message}");
                }
            }

            Console.WriteLine($"\n--- COMPARISON SUMMARY (first {maxFiles} files) ---");
            Console.WriteLine($"Total pixels: {totalPixels}");
            Console.WriteLine($"Exact match:  {exactMatch} ({100.0 * exactMatch / totalPixels:F2}%)");
            Console.WriteLine($"Within ±1:    {within1} ({100.0 * within1 / totalPixels:F2}%)");
            Console.WriteLine($"Beyond ±1:    {beyond1} ({100.0 * beyond1 / totalPixels:F2}%)");
            Console.WriteLine($"Max diff R={maxDiffR} G={maxDiffG} B={maxDiffB} A={maxDiffA}");
        }

        static void Benchmark(string? corpusPath)
        {
            Console.WriteLine("--- TexSharp decode-core benchmark ---");
            Console.WriteLine("I/O, container parsing and output allocations are excluded from these measurements.");

            BenchmarkDecodeCore(DecodedFormat.Bc1, "BC1", 1024, 1024, 32);
            BenchmarkDecodeCore(DecodedFormat.Bc2, "BC2", 1024, 1024, 16);
            BenchmarkDecodeCore(DecodedFormat.Bc3, "BC3", 1024, 1024, 16);
            BenchmarkDecodeCore(DecodedFormat.Bc4, "BC4", 1024, 1024, 24);
            BenchmarkDecodeCore(DecodedFormat.Bc5, "BC5", 1024, 1024, 16);
            BenchmarkDecodeCore(DecodedFormat.Bc7, "BC7", 1024, 1024, 8);

            string path = corpusPath ?? GetSamplesPath();
            if (Directory.Exists(path))
                BenchmarkContainerPipeline(path);
        }

        static void BenchmarkDecodeCore(DecodedFormat format, string name, int width, int height, int iterations)
        {
            byte[] data = new byte[BcImageDecoder.MipSize(width, height, format)];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 149 + 37);

            uint[] output = new uint[width * height];
            for (int i = 0; i < 3; i++)
                BcImageDecoder.DecodeImage(data, width, height, format, output);

            uint checksum = 0;
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                BcImageDecoder.DecodeImage(data, width, height, format, output);
                checksum ^= output[(i * 4099) & (output.Length - 1)];
            }
            timer.Stop();

            double megabytes = (double)width * height * sizeof(uint) * iterations / (1024 * 1024);
            Console.WriteLine($"{name,-4} {megabytes / timer.Elapsed.TotalSeconds,8:F1} MB/s  {timer.Elapsed.TotalMilliseconds,8:F1} ms  checksum={checksum:X8}");
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
