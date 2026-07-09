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
                    Benchmark();
                    return;
                }
                if (args[0] == "--compare")
                {
                    CompareDetailed();
                    return;
                }
                if (args[0] == "--png")
                {
                    TestPngExport();
                    return;
                }
                if (args[0] == "--test" && args.Length > 1)
                {
                    ProcessExtra(args[1]);
                    return;
                }
            }

            Console.WriteLine("--- TexSharp Technical Test ---");
            Console.WriteLine($"Hardware Acceleration: {TextureEngine.IsHardwareAccelerated}");

            TestBc1Decoding();
            TestBc3Decoding();
            TestBc5Decoding();
            TestBc2Decoding();
            TestBc4Decoding();
            TestBc7Invariants();
            TestBc7Interpolation();
            TestBc7TableSpotChecks();
            TestTexMipmaps();
            TestDdsMipmaps();
            ProcessSamples();
            VerifyGoldenHashes();

            Console.WriteLine(_failures == 0
                ? "\n[ALL TESTS PASSED]"
                : $"\n[{_failures} TEST(S) FAILED]");
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
            header[7] = 0x02; // Flags (DDSD_HEIGHT|WIDTH simplified)
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

        static string GetSamplesPath()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        static List<string> GetSampleFiles()
        {
            string samplesPath = GetSamplesPath();
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
            var files = GetSampleFiles();
            if (files.Count == 0) return;

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
                }
            }
        }

        static void ProcessExtra(string dirPath)
        {
            if (!Directory.Exists(dirPath)) { Console.WriteLine($"[ERR] Directory not found: {dirPath}"); return; }
            var files = Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)).ToList();
            int ok = 0, fail = 0;
            foreach (var file in files)
            {
                try
                {
                    uint[] pixels = DecodeSampleFile(file);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAIL] {Path.GetFileName(file)} | {ex.Message}");
                    fail++;
                }
            }
            Console.WriteLine($"\nExtra: OK={ok} FAIL={fail} TOTAL={ok + fail}");
        }

        static void ComputeAndSaveGoldenHashes()
        {
            var files = GetSampleFiles();
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
            var files = GetSampleFiles();
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
            var files = GetSampleFiles();
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

        static void Benchmark()
    {
        var files = GetSampleFiles();
        if (files.Count == 0)
        {
            Console.WriteLine("[WARN] No sample files to benchmark.");
            return;
        }

        var bcn = new BcDecoder();
        long texSharpBytes = 0, bcnBytes = 0;
        int texSharpOk = 0, bcnOk = 0;
        int mismatchedPixels = 0;

        var ts = Stopwatch.StartNew();
        foreach (var file in files)
        {
            try
            {
                uint[] pixels = DecodeSampleFile(file);
                texSharpBytes += pixels.Length * 4;
                texSharpOk++;
            }
            catch { }
        }
        ts.Stop();

        var be = Stopwatch.StartNew();
        foreach (var file in files)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(file);
                byte[] baseMip = ExtractBaseMipData(raw, out int w, out int h, out CompressionFormat cf);

                ColorRgba32[] result;
                if (cf == CompressionFormat.Bgra)
                {
                    result = MemoryMarshal.Cast<byte, ColorRgba32>(baseMip.AsSpan()).ToArray();
                }
                else
                {
                    result = bcn.DecodeRaw(new Memory<byte>(baseMip), w, h, cf);
                }

                bcnBytes += (uint)result.Length * 4;
                bcnOk++;
            }
            catch { }
        }
        be.Stop();

        // Compare pixel outputs
        foreach (var file in files)
        {
            try
            {
                uint[] tsPixels = DecodeSampleFile(file);

                byte[] raw = File.ReadAllBytes(file);
                byte[] baseMip = ExtractBaseMipData(raw, out int w, out int h, out CompressionFormat cf);

                ColorRgba32[] bcnResult;
                if (cf == CompressionFormat.Bgra)
                {
                    bcnResult = MemoryMarshal.Cast<byte, ColorRgba32>(baseMip.AsSpan()).ToArray();
                }
                else
                {
                    bcnResult = bcn.DecodeRaw(new Memory<byte>(baseMip), w, h, cf);
                }

                for (int i = 0; i < tsPixels.Length && i < bcnResult.Length; i++)
                {
                    uint tsP = tsPixels[i];
                    uint bcnP = (uint)bcnResult[i].r | (uint)bcnResult[i].g << 8 |
                                (uint)bcnResult[i].b << 16 | (uint)bcnResult[i].a << 24;
                    if (tsP != bcnP)
                    {
                        mismatchedPixels++;
                    }
                }
            }
            catch { }
        }

        double tsMbps = texSharpBytes / (1024.0 * 1024.0) / (ts.Elapsed.TotalSeconds);
        double bcnMbps = bcnBytes / (1024.0 * 1024.0) / (be.Elapsed.TotalSeconds);

        Console.WriteLine($"\n--- BENCHMARK ---");
        Console.WriteLine($"Files: {files.Count}");
        Console.WriteLine($"TexSharp:   {texSharpOk} files  {ts.Elapsed.TotalSeconds:F3}s  {tsMbps:F1} MB/s");
        Console.WriteLine($"BCnEncoder: {bcnOk} files  {be.Elapsed.TotalSeconds:F3}s  {bcnMbps:F1} MB/s");
        Console.WriteLine($"Ratio: {be.Elapsed.TotalSeconds / ts.Elapsed.TotalSeconds:F2}x");
        Console.WriteLine($"Pixel mismatches: {mismatchedPixels}");
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
