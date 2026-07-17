# TexSharp

Ultra-high performance texture decoding library for .NET 10, with native PNG export with no external dependencies. Handles .tex (Riot Games) and .dds formats: BC1, BC2, BC3, BC4, BC5, BC7, BGRA8, including DDS BC4/BC5 UNORM and SNORM variants.

## Features

- **Decode** .tex and .dds to raw RGBA pixels into caller-provided `Span<uint>` buffers or convenience arrays
- **Export to PNG** directly, no ImageSharp or any external library needed
- **Mipmap support** — read any level, or all levels
- **No hot-loop allocations** — block decoders use spans and stack buffers
- **Specialized image paths** — BC2/BC4/BC5 avoid delegate dispatch, while BC4/BC5 write decoded channels directly to the destination image
- **Reproducible** — calibrated per-format benchmarks and versioned golden fixtures
- **Validated** — historical external corpus runs cover more than 13,000 real `.tex` files

## Usage

```csharp
// Decode
using TexSharp.Containers.Tex;

byte[] data = File.ReadAllBytes("texture.tex");
var reader = new TexReader(data);
uint[] pixels = reader.Decode(); // or reader.DecodeMip(level)

// Export to PNG (no ImageSharp needed)
using TexSharp;

TextureExporter.SaveToPng("texture.tex", "output.png");
```

## Validation

The native test suite has no external dependencies:

```console
dotnet run --project TexSharp.Tests/TexSharp.Tests.csproj -- --unit
```

It includes a small versioned TEX/DDS corpus with SHA-256 golden pixel hashes. Private game
assets can still be supplied separately for large-scale regression testing.

BCnEncoder remains available as an optional compatibility oracle. Provide the DLL explicitly
when building or running the pixel comparison:

```console
dotnet run --project TexSharp.Tests/TexSharp.Tests.csproj \
  -p:BcnEncoderPath=/path/to/BCnEncoder.dll -- --compare
```

`--benchmark` reports calibrated decode-core input/output throughput and allocations for each
BC format at 256×256 and 1024×1024. An optional corpus path adds an end-to-end container run.

## Performance

Historical external-corpus result for **13,415** League of Legends `.tex` files. Run
`--benchmark` on the current machine instead of comparing these hardware-dependent figures directly.

| Engine | MB/s | Ratio | Errors |
|--------|------|-------|--------|
| TexSharp | 379.6 MB/s | 1.00x | 0 |
| BCnEncoder | 856.9 MB/s | 2.26x | 0 |

Pixel comparison (100 files, 7.3M pixels): **96.17% exact match**, 3.83% within ±1 alpha rounding, **0% beyond ±1**.

## PNG Export

Zero-dependency PNG writer built-in:

```csharp
TextureExporter.SaveToPng("input.tex", "output.png");
TextureExporter.SaveToPng("input.dds", "output.png");
TextureExporter.SaveToPng(pixels, width, height, "output.png");
```

Uses .NET's `ZLibStream` and custom CRC32 — no ImageSharp, no StbImageWrite, no external packages.
PNG data is emitted as bounded streaming IDAT chunks, avoiding a full compressed-image buffer.

## License

GNU General Public License v3.0
