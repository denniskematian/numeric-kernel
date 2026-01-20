using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NumericKernel.RuntimeCore.Buffers;

namespace NumericKernel.Primes;

public class PrimeGenerator : IDisposable
{
    private const int NotGenerated = 0;
    private const int Generating = 1;
    private const int Generated = 2;
    private const long MaxPrime = 4294967296;

    private readonly UnmanagedMemory<uint> _bits = MemoryAllocator.Allocate<uint>((int)(MaxPrime / 32));
    private int _primeCount;
    private volatile int _state = NotGenerated;

    public void Dispose()
    {
        _bits.Dispose();
    }

    public void Generate()
    {
        var currentState = Interlocked.CompareExchange(ref _state, Generating, NotGenerated);
        switch (currentState)
        {
            case Generating:
                throw new InvalidOperationException("The generator is currently generating primes.");
            case Generated:
                return;
        }

        Debug.WriteLine("Generating primes up to {0}...", MaxPrime);

        _bits[0] = 0xA08A28AC; // bit-packed primes < 32
        WheelFactorization();
        using var primeSeed = GeneratePrimeSeed();
        SegmentedSieving(primeSeed);

        _state = Generated;
    }

    public IEnumerable<uint> EnumeratePrimes()
    {
        Generate();
        for (uint i = 0; i < _bits.Length; i++)
        for (var j = 1; j < 32; j += 2)
        {
            if ((_bits[i] & (1u << j)) == 0)
                continue;

            var num = i * 32 + j;
            yield return (uint)num;
        }
    }

    public int Count()
    {
        if (_primeCount != 0)
            return _primeCount;

        Generate();
        var count = 0;
        for (var i = 0; i < _bits.Length; i++) count += BitOperations.PopCount(_bits[i]);

        return _primeCount = count;
    }

    private void WheelFactorization()
    {
        Debug.Write("Start wheel factorization... ");

        const int maskSize = 3 * 5 * 7 * 11 * 13; // lcm of union(primes < 16, 32) / 32
        using var masks = MemoryAllocator.Allocate<uint>(maskSize);
        WheelFactor[] factors =
        [
            new(2),
            new(3),
            new(5),
            new(7),
            new(11),
            new(13)
        ];

        for (var i = 0; i < maskSize; i++)
        {
            uint mask = 0;
            for (var j = 0; j < factors.Length; j++) mask |= factors[j].Next();
            masks[i] = ~mask;
        }

        const int segmentSize = maskSize; // 120 * 1024;
        for (int i = 1, j = 0; i <= segmentSize;)
        {
            _bits[i++] = masks[j++];
            if (j == masks.Length) j = 0;
        }

        var offset = segmentSize + 1;
        ref var chunkRef = ref Unsafe.As<uint, byte>(ref _bits[1]);
        var byteCount = (uint)(segmentSize * sizeof(uint));
        while (offset + segmentSize < _bits.Length)
        {
            ref var destination = ref Unsafe.As<uint, byte>(ref _bits[offset]);
            Unsafe.CopyBlock(ref destination, ref chunkRef, byteCount);
            offset += segmentSize;
        }

        if (offset < _bits.Length)
        {
            byteCount = (uint)((_bits.Length - offset) * sizeof(uint));
            ref var destination = ref Unsafe.As<uint, byte>(ref _bits[offset]);
            Unsafe.CopyBlock(ref destination, ref chunkRef, byteCount);
        }

        Debug.WriteLine("Done.");
    }

    private UnmanagedMemory<PrimeSeed> GeneratePrimeSeed()
    {
        const int seedMax = 65536;

        // store primes up to 65536
        var primeSeed = MemoryAllocator.Allocate<PrimeSeed>(6542 - 6);
        var n = 0;

        for (var i = 17; i < 32; i += 2)
        {
            if ((_bits[0] & (1u << i)) == 0)
                continue;

            long num = i;
            var k = num * 3L;
            for (; k < seedMax; k += 2L * num)
            {
                var mask = 1u << (int)(k & 31);
                _bits[k >> 5] &= ~mask;
            }

            primeSeed[n++] = new PrimeSeed((uint)num, k);
        }

        for (uint i = 1; i < seedMax >> 5; i++)
        for (var j = 1; j < 32; j += 2)
        {
            if ((_bits[i] & (1u << j)) == 0)
                continue;

            var num = i * 32 + j;
            var k = num * 3L;
            for (; k < seedMax; k += 2L * num)
            {
                var mask = 1u << (int)(k & 31);
                _bits[k >> 5] &= ~mask;
            }

            primeSeed[n++] = new PrimeSeed((uint)num, k);
        }

        return primeSeed;
    }

    private void SegmentedSieving(UnmanagedMemory<PrimeSeed> primeSeed)
    {
        const int segmentSize = 128 * 32 * 1024;
        const int workerCount = 8;

        Parallel.For(0, workerCount, t =>
        {
            for (long segment = segmentSize; segment <= MaxPrime; segment += segmentSize)
            for (var i = t; i < primeSeed.Length; i += workerCount)
            {
                ref var seed = ref primeSeed[i];
                for (; seed.Current < segment; seed.Current += 2L * seed.Base)
                {
                    var mask = ~(1u << (int)(seed.Current & 31));
                    var index = seed.Current >> 5;
                    var current = _bits[index];
                    while (current != Interlocked.CompareExchange(ref _bits[index], current & mask, current))
                        current = _bits[index];
                }
            }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrimeSeed
    {
        public readonly uint Base;
        public long Current;

        public PrimeSeed(uint baseNum, long current)
        {
            Base = baseNum;
            Current = current;
        }
    }

    private class WheelFactor
    {
        private readonly uint[] _wheel;
        private int _index;

        public WheelFactor(int n)
        {
            var list = new List<uint>(32);
            var shift = 0;
            while (true)
            {
                uint mask = 0;
                for (; shift < sizeof(uint) * 8; shift += n) mask |= 1u << shift;

                if (list.Contains(mask)) break;
                list.Add(mask);

                shift -= sizeof(uint) * 8;
            }

            _wheel = list.ToArray();
        }

        public uint Next()
        {
            if (++_index == _wheel.Length) _index = 0;
            return _wheel[_index];
        }
    }
}