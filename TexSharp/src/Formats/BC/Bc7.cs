using System;
using System.Linq;
using TexSharp.Utils;

namespace TexSharp.Formats.BC
{
    /// <summary>
    /// Decodificador BC7 (BPTC) completo: modos 0-7.
    /// Basado en la tabla de parámetros y particiones de la especificación de BC7.
    /// </summary>
    public static class Bc7Block
    {
        // Pesos de interpolación por precisión de índice.
        private static readonly int[] Weights2 = { 0, 21, 43, 64 };
        private static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        // Tablas de partición (índice por píxel 0..15) para 2 y 3 subconjuntos.
        private static readonly byte[][] Partition2 = BuildPartition2();
        private static readonly byte[][] Partition3 = BuildPartition3();

        // Anclas de fix-up por (subconjuntos-1, forma, subconjunto).
        private static readonly byte[][][] FixUp =
        {
            BuildFixUp1(),
            BuildFixUp2(),
            BuildFixUp3()
        };

        // Tabla de parámetros por modo (ver BC7 format mode reference).
        private static readonly ModeParams[] ModeTable =
        {
            // Mode 0
            new ModeParams(3, 4, 6, 0, 0, 3, 0, 4,4,4,0, 5,5,5,0),
            // Mode 1
            new ModeParams(2, 6, 2, 0, 0, 3, 0, 6,6,6,0, 7,7,7,0),
            // Mode 2
            new ModeParams(3, 6, 0, 0, 0, 2, 0, 5,5,5,0, 5,5,5,0),
            // Mode 3
            new ModeParams(2, 6, 4, 0, 0, 2, 0, 7,7,7,0, 8,8,8,0),
            // Mode 4
            new ModeParams(1, 0, 0, 2, 1, 2, 3, 5,5,5,6, 5,5,5,6),
            // Mode 5
            new ModeParams(1, 0, 0, 2, 0, 2, 2, 7,7,7,8, 7,7,7,8),
            // Mode 6
            new ModeParams(1, 0, 2, 0, 0, 4, 0, 7,7,7,7, 8,8,8,8),
            // Mode 7
            new ModeParams(2, 6, 4, 0, 0, 2, 0, 5,5,5,5, 6,6,6,6)
        };

        public static void DecodeBlock(ReadOnlySpan<byte> data, Span<uint> rgbaOutput)
        {
            if (rgbaOutput.Length < 16)
                return;

            if (data.Length < 16)
            {
                for (int i = 0; i < 16; i++) rgbaOutput[i] = 0;
                return;
            }

            var reader = new BitReader(data);

            // Determinar el modo: posición del primer bit en 1 (de LSB a MSB).
            int mode = -1;
            for (int b = 0; b < 8; b++)
            {
                if (reader.Read(1) != 0) { mode = b; break; }
            }

            if (mode < 0 || mode > 7)
            {
                // Modo reservado (0x00) o inválido: bloque transparente negro.
                for (int i = 0; i < 16; i++) rgbaOutput[i] = 0;
                return;
            }

            ref readonly var info = ref ModeTable[mode];
            int subsetsMinus1 = info.Subsets - 1;
            int numEndPts = info.Subsets * 2;

            // Tras leer el bit de modo ya estamos en uStartBit = mode + 1.
            int shape = reader.Read(info.PartitionBits);
            int rotation = reader.Read(info.RotationBits);
            int indexMode = reader.Read(info.IndexModeBits);

            // Leer endpoints por canal (R,G,B,A).
            Span<int> cR = stackalloc int[6];
            Span<int> cG = stackalloc int[6];
            Span<int> cB = stackalloc int[6];
            Span<int> cA = stackalloc int[6];

            for (int i = 0; i < numEndPts; i++) cR[i] = reader.Read(info.PrecR);
            for (int i = 0; i < numEndPts; i++) cG[i] = reader.Read(info.PrecG);
            for (int i = 0; i < numEndPts; i++) cB[i] = reader.Read(info.PrecB);
            for (int i = 0; i < numEndPts; i++) cA[i] = info.PrecA > 0 ? reader.Read(info.PrecA) : 255;

            // Bits P.
            Span<int> pbits = stackalloc int[6];
            for (int i = 0; i < info.PBits; i++) pbits[i] = reader.Read(1);

            // Aplicar bits P a los endpoints.
            for (int i = 0; i < numEndPts; i++)
            {
                int pi = info.PBits == 0 ? 0 : i * info.PBits / numEndPts;
                if (info.PrecR != info.PrecPR) cR[i] = (cR[i] << 1) | pbits[pi];
                if (info.PrecG != info.PrecPG) cG[i] = (cG[i] << 1) | pbits[pi];
                if (info.PrecB != info.PrecPB) cB[i] = (cB[i] << 1) | pbits[pi];
                if (info.PrecA != info.PrecPA) cA[i] = (cA[i] << 1) | pbits[pi];
            }

            // Descuantizar a 8 bits.
            for (int i = 0; i < numEndPts; i++)
            {
                cR[i] = Unquantize(cR[i], info.PrecPR);
                cG[i] = Unquantize(cG[i], info.PrecPG);
                cB[i] = Unquantize(cB[i], info.PrecPB);
                cA[i] = info.PrecPA > 0 ? Unquantize(cA[i], info.PrecPA) : 255;
            }

            // Leer índices de color (y alpha si aplica).
            Span<int> w1 = stackalloc int[16];
            Span<int> w2 = stackalloc int[16];
            for (int i = 0; i < 16; i++)
            {
                int bits = IsFixUp(subsetsMinus1, shape, i) ? info.IndexPrec - 1 : info.IndexPrec;
                w1[i] = reader.Read(bits);
            }
            if (info.IndexPrec2 > 0)
            {
                for (int i = 0; i < 16; i++)
                {
                    int bits = IsFixUp(subsetsMinus1, shape, i) ? info.IndexPrec2 - 1 : info.IndexPrec2;
                    w2[i] = reader.Read(bits);
                }
            }

            // Precisión de los índices de color/alpha. En los modos 4/5 el segundo
            // juego de índices (w2) se usa para el canal "separado"; según indexMode
            // puede ser el color o el alpha quien use w2.
            int colorPrec = (info.IndexPrec2 > 0 && indexMode != 0) ? info.IndexPrec2 : info.IndexPrec;
            int alphaPrec = (info.IndexPrec2 > 0 && indexMode == 0) ? info.IndexPrec2 : info.IndexPrec;
            ReadOnlySpan<int> wTabC = Weights(colorPrec);
            ReadOnlySpan<int> wTabA = Weights(alphaPrec);

            for (int i = 0; i < 16; i++)
            {
                int region;
                if (subsetsMinus1 == 0) region = 0;
                else if (subsetsMinus1 == 1) region = Partition2[shape][i];
                else region = Partition3[shape][i];

                int ep0 = region * 2;
                int ep1 = region * 2 + 1;

                int ci = w1[i];
                int ai = w1[i];
                if (info.IndexPrec2 > 0)
                {
                    if (indexMode == 0) ai = w2[i];
                    else ci = w2[i];
                }

                int r = Interpolate(cR[ep0], cR[ep1], ci, wTabC);
                int g = Interpolate(cG[ep0], cG[ep1], ci, wTabC);
                int b = Interpolate(cB[ep0], cB[ep1], ci, wTabC);
                int a = Interpolate(cA[ep0], cA[ep1], ai, wTabA);

                // Rotación de canal (modos 4 y 5).
                switch (rotation)
                {
                    case 1: (r, a) = (a, r); break;
                    case 2: (g, a) = (a, g); break;
                    case 3: (b, a) = (a, b); break;
                }

                rgbaOutput[i] = (uint)(r | (g << 8) | (b << 16) | (a << 24));
            }
        }

        private static int Interpolate(int c0, int c1, int w, ReadOnlySpan<int> weights)
        {
            return (c0 * (64 - weights[w]) + c1 * weights[w] + 32) >> 6;
        }

        private static ReadOnlySpan<int> Weights(int prec) =>
            prec == 2 ? Weights2 : prec == 3 ? Weights3 : Weights4;

        private static int Unquantize(int comp, int prec)
        {
            if (prec <= 0) return 255;
            comp <<= (8 - prec);
            return comp | (comp >> prec);
        }

        // Métodos de inspección para tests (validación de tablas).
        public static ReadOnlySpan<byte> Partition2Debug(int shape) => Partition2[shape];
        public static ReadOnlySpan<byte> Partition3Debug(int shape) => Partition3[shape];
        public static int FixUp2Debug(int shape) => FixUp[1][shape][1];
        public static int FixUp3Debug(int shape, int subset) => FixUp[2][shape][subset];

        private static bool IsFixUp(int subsetsMinus1, int shape, int offset)
        {
            for (int p = 0; p <= subsetsMinus1; p++)
            {
                if (offset == FixUp[subsetsMinus1][shape][p]) return true;
            }
            return false;
        }

        private readonly struct ModeParams
        {
            public readonly int Subsets;
            public readonly int PartitionBits;
            public readonly int PBits;
            public readonly int RotationBits;
            public readonly int IndexModeBits;
            public readonly int IndexPrec;
            public readonly int IndexPrec2;
            public readonly int PrecR, PrecG, PrecB, PrecA;
            public readonly int PrecPR, PrecPG, PrecPB, PrecPA;

            public ModeParams(int subsets, int partBits, int pBits, int rotBits, int idxModeBits,
                int idxPrec, int idxPrec2, int pr, int pg, int pb, int pa, int ppr, int ppg, int ppb, int ppa)
            {
                Subsets = subsets;
                PartitionBits = partBits;
                PBits = pBits;
                RotationBits = rotBits;
                IndexModeBits = idxModeBits;
                IndexPrec = idxPrec;
                IndexPrec2 = idxPrec2;
                PrecR = pr; PrecG = pg; PrecB = pb; PrecA = pa;
                PrecPR = ppr; PrecPG = ppg; PrecPB = ppb; PrecPA = ppa;
            }
        }

        // --- Construcción de tablas (datos de la especificación BC7) ---

        private static byte[][] BuildFixUp1()
        {
            var t = new byte[64][];
            for (int i = 0; i < 64; i++) t[i] = new byte[] { 0, 0, 0 };
            return t;
        }

        private static byte[][] BuildFixUp2()
        {
            int[][] raw =
            {
                new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0},
                new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0},
                new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0},
                new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0},
                new[]{0,15,0}, new[]{0, 2,0}, new[]{0, 8,0}, new[]{0, 2,0},
                new[]{0, 2,0}, new[]{0, 8,0}, new[]{0, 8,0}, new[]{0,15,0},
                new[]{0, 2,0}, new[]{0, 8,0}, new[]{0, 2,0}, new[]{0, 2,0},
                new[]{0, 8,0}, new[]{0, 8,0}, new[]{0, 2,0}, new[]{0, 2,0},
                new[]{0,15,0}, new[]{0,15,0}, new[]{0, 6,0}, new[]{0, 8,0},
                new[]{0, 2,0}, new[]{0, 8,0}, new[]{0,15,0}, new[]{0,15,0},
                new[]{0, 2,0}, new[]{0, 8,0}, new[]{0, 2,0}, new[]{0, 2,0},
                new[]{0, 2,0}, new[]{0,15,0}, new[]{0,15,0}, new[]{0, 6,0},
                new[]{0, 6,0}, new[]{0, 2,0}, new[]{0, 6,0}, new[]{0, 8,0},
                new[]{0,15,0}, new[]{0,15,0}, new[]{0, 2,0}, new[]{0, 2,0},
                new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0}, new[]{0,15,0},
                new[]{0,15,0}, new[]{0, 2,0}, new[]{0, 2,0}, new[]{0,15,0}
            };
            var t = new byte[64][];
            for (int i = 0; i < 64; i++) t[i] = raw[i].Select(x => (byte)x).ToArray();
            return t;
        }

        private static byte[][] BuildFixUp3()
        {
            int[][] raw =
            {
                new[]{0, 3,15}, new[]{0, 3, 8}, new[]{0,15, 8}, new[]{0,15, 3},
                new[]{0, 8,15}, new[]{0, 3,15}, new[]{0,15, 3}, new[]{0,15, 8},
                new[]{0, 8,15}, new[]{0, 8,15}, new[]{0, 6,15}, new[]{0, 6,15},
                new[]{0, 6,15}, new[]{0, 5,15}, new[]{0, 3,15}, new[]{0, 3, 8},
                new[]{0, 3,15}, new[]{0, 3, 8}, new[]{0, 8,15}, new[]{0,15, 3},
                new[]{0, 3,15}, new[]{0, 3, 8}, new[]{0, 6,15}, new[]{0,10, 8},
                new[]{0, 5, 3}, new[]{0, 8,15}, new[]{0, 8, 6}, new[]{0, 6,10},
                new[]{0, 8,15}, new[]{0, 5,15}, new[]{0,15,10}, new[]{0,15, 8},
                new[]{0, 8,15}, new[]{0,15, 3}, new[]{0, 3,15}, new[]{0, 5,10},
                new[]{0, 6,10}, new[]{0,10, 8}, new[]{0, 8, 9}, new[]{0,15,10},
                new[]{0,15, 6}, new[]{0, 3,15}, new[]{0,15, 8}, new[]{0, 5,15},
                new[]{0,15, 3}, new[]{0,15, 6}, new[]{0,15, 6}, new[]{0,15, 8},
                new[]{0, 3,15}, new[]{0,15, 3}, new[]{0, 5,15}, new[]{0, 5,15},
                new[]{0, 5,15}, new[]{0, 8,15}, new[]{0, 5,15}, new[]{0,10,15},
                new[]{0, 5,15}, new[]{0,10,15}, new[]{0, 8,15}, new[]{0,13,15},
                new[]{0,15, 3}, new[]{0,12,15}, new[]{0, 3,15}, new[]{0, 3, 8}
            };
            var t = new byte[64][];
            for (int i = 0; i < 64; i++) t[i] = raw[i].Select(x => (byte)x).ToArray();
            return t;
        }

        private static byte[][] BuildPartition2()
        {
            int[][] raw =
            {
                new[]{0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1},
                new[]{0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1},
                new[]{0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1},
                new[]{0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1},
                new[]{0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1},
                new[]{0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1},
                new[]{0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1},
                new[]{0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1},
                new[]{0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1},
                new[]{0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1},
                new[]{0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1},
                new[]{0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1},
                new[]{0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1},
                new[]{0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1},
                new[]{0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1},
                new[]{0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1},
                new[]{0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1},
                new[]{0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0},
                new[]{0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0},
                new[]{0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0},
                new[]{0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0},
                new[]{0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0},
                new[]{0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0},
                new[]{0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1},
                new[]{0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0},
                new[]{0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0},
                new[]{0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0},
                new[]{0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0},
                new[]{0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0},
                new[]{0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0},
                new[]{0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0},
                new[]{0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0},
                new[]{0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1},
                new[]{0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1},
                new[]{0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0},
                new[]{0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0},
                new[]{0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0},
                new[]{0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0},
                new[]{0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1},
                new[]{0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1},
                new[]{0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0},
                new[]{0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0},
                new[]{0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0},
                new[]{0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0},
                new[]{0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0},
                new[]{0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1},
                new[]{0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1},
                new[]{0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0},
                new[]{0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0},
                new[]{0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0},
                new[]{0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0},
                new[]{0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0},
                new[]{0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1},
                new[]{0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1},
                new[]{0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0},
                new[]{0,0,1,1,1,0,0,1,1,1,0,0,0,1,1,0},
                new[]{0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1},
                new[]{0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1},
                new[]{0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1},
                new[]{0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1},
                new[]{0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1},
                new[]{0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0},
                new[]{0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0},
                new[]{0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1}
            };
            var t = new byte[64][];
            for (int i = 0; i < 64; i++) t[i] = raw[i].Select(x => (byte)x).ToArray();
            return t;
        }

        private static byte[][] BuildPartition3()
        {
            int[][] raw =
            {
                new[]{0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2},
                new[]{0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1},
                new[]{0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1},
                new[]{0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1},
                new[]{0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2},
                new[]{0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2},
                new[]{0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1},
                new[]{0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1},
                new[]{0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2},
                new[]{0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2},
                new[]{0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2},
                new[]{0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2},
                new[]{0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2},
                new[]{0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2},
                new[]{0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2},
                new[]{0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0},
                new[]{0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2},
                new[]{0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0},
                new[]{0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2},
                new[]{0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1},
                new[]{0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2},
                new[]{0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1},
                new[]{0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2},
                new[]{0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0},
                new[]{0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0},
                new[]{0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2},
                new[]{0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0},
                new[]{0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1},
                new[]{0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2},
                new[]{0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2},
                new[]{0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1},
                new[]{0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1},
                new[]{0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2},
                new[]{0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1},
                new[]{0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2},
                new[]{0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0},
                new[]{0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0},
                new[]{0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0},
                new[]{0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0},
                new[]{0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1},
                new[]{0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1},
                new[]{0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2},
                new[]{0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1},
                new[]{0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2},
                new[]{0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1},
                new[]{0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1},
                new[]{0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1},
                new[]{0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1},
                new[]{0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2},
                new[]{0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1},
                new[]{0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2},
                new[]{0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2},
                new[]{0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2},
                new[]{0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2},
                new[]{0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2},
                new[]{0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2},
                new[]{0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2},
                new[]{0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2},
                new[]{0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2},
                new[]{0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2},
                new[]{0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1},
                new[]{0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2},
                new[]{0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2},
                new[]{0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0}
            };
            var t = new byte[64][];
            for (int i = 0; i < 64; i++) t[i] = raw[i].Select(x => (byte)x).ToArray();
            return t;
        }
    }
}
