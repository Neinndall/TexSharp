using System;

namespace TexSharp.Utils
{
    public ref struct BitReader
    {
        private ReadOnlySpan<byte> _data;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitOffset = 0;
        }

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteIdx = _bitOffset >> 3;
                int bitIdx = _bitOffset & 0x7;
                
                if ((_data[byteIdx] & (1 << bitIdx)) != 0)
                {
                    value |= (1 << i);
                }
                _bitOffset++;
            }
            return value;
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
