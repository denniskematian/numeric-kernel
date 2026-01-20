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
    private const int PartiallyGenerated = 2;
    private const int FullyGenerated = 3;

    private const long MaxPrime = 1L << IntBits;
    private const int IntBits = sizeof(uint) * 8;
    private const int SegmentSize = 128 * 32 * 1024;

    private readonly UnmanagedMemory<uint> _bits = MemoryAllocator.Allocate<uint>((int)(MaxPrime / IntBits));
    private long _currentSegment;
    private UnmanagedMemory<long>? _primeSeed;
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
            case PartiallyGenerated:
            case FullyGenerated:
                return;
        }

        _bits[0] = 0xA08A28AC; // bit-packed primes < 32
        WheelFactorization();
        _primeSeed = GeneratePrimeSeed();
        _state = PartiallyGenerated;

        SegmentedSieve(SegmentSize);
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
            if (num > _currentSegment)
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
        if (n < 2) return false;

        Initialize();
        if (n < _currentSegment) return (_bits[n >> 5] & (1u << (int)(n & 31))) != 0;

        var sqrt = Discrete.Sqrt(n);
        foreach (var prime in EnumeratePrimes())
        {
            if (n % prime == 0) return false;
            if (prime > sqrt) break;
        }

        return true;
    }

    private void WheelFactorization()
    {
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

    private UnmanagedMemory<long> GeneratePrimeSeed()
    {
        const int seedMax = 65536;

        // store primes up to 65536
        var primeSeed = MemoryAllocator.Allocate<long>(6542 - 6);
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

            primeSeed[n++] = num;
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

            primeSeed[n++] = num;
        }

        return primeSeed;
    }

    private void SegmentedSieve(long segmentSize)
    {
        if (_currentSegment == MaxPrime || _currentSegment >= segmentSize)
            return;

        Debug.Assert(_primeSeed is not null);

        var currentState = Interlocked.CompareExchange(ref _state, Generating, PartiallyGenerated);
        switch (currentState)
        {
            case Generating:
                throw new InvalidOperationException("The generator is currently generating primes.");
            case FullyGenerated:
                return;
        }

        int workerCount = Environment.ProcessorCount;
        var chunkSize = SegmentSize / workerCount;
        Parallel.For(0, workerCount, t =>
        {
            for (long segment = _currentSegment; segment < segmentSize; segment += SegmentSize)
            {
                var start = segment + t * chunkSize;
                var end = start + chunkSize;
                for (int i = 0; i < _primeSeed.Length; i++)
                {
                    var num = _primeSeed[i];
                    var pp = num * num;
                    if (pp >= end) break;

                    var current = pp > start ? pp : Discrete.DivCeil(start, num) * num;

                    if ((current & 1) == 0)
                        current += num;

                    var step = num * 2L;
                    while (current < end)
                    {
                        _bits[current >> 5] &= ~(1u << (int)(current & 31));
                        current += step;
                    }
                }
            }
        });

        _currentSegment = segmentSize;
        _state = PartiallyGenerated;
        if (_currentSegment == MaxPrime)
        {
            (var primeSeed, _primeSeed) = (_primeSeed, null);
            primeSeed.Dispose();
            _state = FullyGenerated;
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