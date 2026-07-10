using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TexSharp.Utils
{
    public ref struct BitReader
    {
        private readonly ulong _low;
        private readonly ulong _high;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _low = data.Length >= sizeof(ulong) ? MemoryMarshal.Read<ulong>(data) : 0;
            _high = data.Length >= sizeof(ulong) * 2 ? MemoryMarshal.Read<ulong>(data.Slice(sizeof(ulong))) : 0;
            _bitOffset = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(int count)
        {
            if (count == 0) return 0;

            int offset = _bitOffset;
            ulong value;
            if (offset < 64)
            {
                value = _low >> offset;
                if (offset + count > 64)
                    value |= _high << (64 - offset);
            }
            else
            {
                value = _high >> (offset - 64);
            }

            _bitOffset = offset + count;
            return (int)(value & ((1UL << count) - 1));
        }

        public void Skip(int count)
        {
            _bitOffset += count;
        }

        public void Align(int alignment)
        {
            _bitOffset = (_bitOffset + alignment - 1) & ~(alignment - 1);
        }
    }
}
