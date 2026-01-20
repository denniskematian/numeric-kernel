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
    private const long MaxPrime = 1L << IntBits;
    private const int IntBits = sizeof(uint) * 8;
    private const int SegmentSize = 128 * 32 * 1024;

    private readonly UnmanagedMemory<uint> _bits = MemoryAllocator.Allocate<uint>((int)(MaxPrime / IntBits));
    private UnmanagedMemory<PrimeSeed>? _primeSeed;
    private long _currentSegment;
    private volatile int _state = NotGenerated;

    public void Dispose()
    {
        _bits.Dispose();
        _primeSeed?.Dispose();
    }

    private void Initialize()
    {
        var currentState = Interlocked.CompareExchange(ref _state, Generating, NotGenerated);
        switch (currentState)
        {
            case Generating:
                throw new InvalidOperationException("The generator is currently generating primes.");
            case Generated:
                return;
        }

        _bits[0] = 0xA08A28AC; // bit-packed primes < 32
        WheelFactorization();
        _primeSeed = GeneratePrimeSeed();
        SegmentedSieve(SegmentSize);

        _state = Generated;
    }

    public IEnumerable<uint> EnumeratePrimes(long max = MaxPrime)
    {
        if (max < 2) yield break;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxPrime);

        Initialize();
        yield return 2;
        for (uint i = 0; i < _bits.Length; i++)
        for (var j = 1; j < IntBits; j += 2)
        {
            if ((_bits[i] & (1u << j)) == 0)
                continue;

            var num = i * IntBits + j;
            if(num > _currentSegment)
                SegmentedSieve(_currentSegment + SegmentSize);

            if (num >= max) yield break;
            yield return (uint)num;
        }
    }

    public int Count(long max = MaxPrime)
    {
        if (max < 2) return 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxPrime);

        Initialize();
        if (max > _currentSegment)
        {
            var nextSegment = Discrete.DivCeil(max, SegmentSize) * SegmentSize;
            SegmentedSieve(nextSegment);
        }

        var count = 0;
        var length = max / IntBits;
        var remainder = max % IntBits;
        var i = 0;
        for (; i < length; i++) count += BitOperations.PopCount(_bits[i]);

        if (remainder != 0)
        {
            var mask = (1u << (int)remainder) - 1;
            count += BitOperations.PopCount(_bits[i] & mask);
        }

        return count;
    }

    public bool IsPrime(long n)
    {
        if(n < 2) return false;

        Initialize();
        if (n < _currentSegment)
        {
            return (_bits[n / 32] & (1u << (int)(n & 31))) != 0;
        }

        var sqrt = Discrete.Sqrt(n);
        foreach (var prime in EnumeratePrimes())
        {
            if(n % prime == 0) return false;
            if(prime > sqrt) break;
        }

        return true;
    }

    private void WheelFactorization()
    {
        Debug.Write("Start wheel factorization... ");

        const int maskSize = 3 * 5 * 7 * 11 * 13 * 32 / 32; // lcm of union(primes < 16, 32) / 32
        WheelFactor[] factors =
        [
            new(2),
            new(3),
            new(5),
            new(7),
            new(11),
            new(13)
        ];

        long offset = 1;
        for (var i = 0; i < maskSize; i++)
        {
            uint mask = 0;
            foreach (var t in factors) mask |= t.Next();

            _bits[offset++] = ~mask;
        }

        const int maxChunkSize = 8 * 1024 * 1024;
        ref var chunkRef = ref Unsafe.As<uint, byte>(ref _bits[1]);
        var byteCount = (uint)(maskSize * sizeof(uint));

        // mask expansion up to 2 * maxChunkSize
        while (byteCount < maxChunkSize)
        {
            ref var destination = ref Unsafe.As<uint, byte>(ref _bits[offset]);
            Unsafe.CopyBlock(ref destination, ref chunkRef, byteCount);
            offset += byteCount / sizeof(uint);
            byteCount = byteCount << 1;
        }

        var segmentSize = byteCount / sizeof(uint);
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
    }

    private UnmanagedMemory<PrimeSeed> GeneratePrimeSeed()
    {
        const int seedMax = 65536;

        // store primes up to 65536
        var primeSeed = MemoryAllocator.Allocate<PrimeSeed>(6542 - 6);
        var n = 0;

        for (var i = 17; i < IntBits; i += 2)
        {
            if ((_bits[0] & (1u << i)) == 0)
                continue;

            long num = i;
            var k = num * 3L;
            for (; k < seedMax; k += num << 1)
            {
                var mask = 1u << (int)(k & 31);
                _bits[k >> 5] &= ~mask;
            }

            primeSeed[n++] = new PrimeSeed((uint)num, k);
        }

        for (uint i = 1; i < seedMax >> 5; i++)
        for (var j = 1; j < IntBits; j += 2)
        {
            if ((_bits[i] & (1u << j)) == 0)
                continue;

            var num = i * IntBits + j;
            var k = num * 3L;
            for (; k < seedMax; k += num << 1)
            {
                var mask = 1u << (int)(k & 31);
                _bits[k >> 5] &= ~mask;
            }

            primeSeed[n++] = new PrimeSeed((uint)num, k);
        }

        return primeSeed;
    }

    private void SegmentedSieve(long segmentSize)
    {
        const int workerCount = 8;
        if(_currentSegment == MaxPrime || _currentSegment >= segmentSize)
            return;

        Debug.Assert(_primeSeed is not null);
        Parallel.For(0, workerCount, t =>
        {
            for (long segment = SegmentSize; segment <= segmentSize; segment += SegmentSize)
            for (var i = t; i < _primeSeed.Length; i += workerCount)
            {
                ref var seed = ref _primeSeed[i];
                for (; seed.Current < segment; seed.Current += seed.Base << 1)
                {
                    var mask = ~(1u << (int)(seed.Current & 31));
                    var index = seed.Current >> 5;
                    var current = _bits[index];
                    while (current != Interlocked.CompareExchange(ref _bits[index], current & mask, current))
                        current = _bits[index];
                }
            }
        });

        _currentSegment = segmentSize;
        if (_currentSegment == MaxPrime)
        {
            (var primeSeed, _primeSeed) = (_primeSeed, null);
            primeSeed.Dispose();
        }
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
            var list = new List<uint>(16);
            var shift = 0;
            while (true)
            {
                uint mask = 0;
                for (; shift < IntBits; shift += n) mask |= 1u << shift;

                if (list.Contains(mask)) break;
                list.Add(mask);

                shift -= IntBits;
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