using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace TexSharp
{
    /// <summary>
    /// Core engine for high-performance texture operations.
    /// Using SIMD and Span for maximum efficiency.
    /// </summary>
    public static class TextureEngine
    {
        public static bool IsHardwareAccelerated => Avx2.IsSupported || Sse41.IsSupported;

        // More implementation coming soon...
    }
}
