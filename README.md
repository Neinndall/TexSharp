# TexSharp

Ultra-high performance texture decoding library for .NET 10, with native PNG export with no external dependencies. Handles .tex (Riot Games) and .dds formats: BC1, BC2, BC3, BC4, BC5, BC7, BGRA8.

## Features

- **Decode** .tex and .dds to raw RGBA pixels (`Span<uint>` based, zero-copy output via `ArrayPool`)
- **Export to PNG** directly, no ImageSharp or any external library needed
- **Mipmap support** — read any level, or all levels
- **Zero extra copies** — shares the original `byte[]`, no redundant allocation
- **Fast** — ~226 MB/s, **0.92x of BCnEncoder** (up from 0.43x)
- **Validated** — 13,846 .tex files decoded **with 0 failures**

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

## Performance

Tested against **13,846** real .tex files from League of Legends (BC1, BC3, BGRA8).

| Engine | MB/s | Ratio | Errors |
|--------|------|-------|--------|
| TexSharp | ~226 MB/s | 0.92x | 0 |
| BCnEncoder | ~244 MB/s | 1.00x | 4 |

Pixel comparison (100 files, 7.3M pixels): **96.17% exact match**, 3.83% within ±1 alpha rounding, **0% beyond ±1**.

## PNG Export

Zero-dependency PNG writer built-in:

```csharp
TextureExporter.SaveToPng("input.tex", "output.png");
TextureExporter.SaveToPng("input.dds", "output.png");
TextureExporter.SaveToPng(pixels, width, height, "output.png");
```

Uses .NET's `DeflateStream` + custom CRC32/Adler32 — no ImageSharp, no StbImageWrite, no external packages.

## License

GNU General Public License v3.0
