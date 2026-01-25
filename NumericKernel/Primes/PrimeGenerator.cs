using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NumericKernel.RuntimeCore.Buffers;

namespace NumericKernel.Primes;

internal class PrimeGenerator : IDisposable
{
    public const long MaxPrime = 1L << WordSize;
    private const int WordSize = sizeof(uint) * 8;
    private const int SegmentSize = 128 * 32 * 1024;
    private const long PrimeCount32 = 203280221; // pi(2^32)
    private const int PrimeCount16 = 6542; // pi(2^16)

    private readonly UnmanagedMemory<uint> _bits = MemoryAllocator.Allocate<uint>((int)(MaxPrime / WordSize));
    private long _currentSegment;
    private UnmanagedMemory<long>? _primeSeed;
    private readonly Lock _lock = new();

    internal PrimeGenerator()
    {
        _bits[0] = 0xA08A28AC; // bit-packed primes < 32
        WheelFactorization();
        _primeSeed = GeneratePrimeSeed();
    }

    public void Dispose()
    {
        _bits.Dispose();
        _primeSeed?.Dispose();
    }

    public IEnumerable<uint> EnumeratePrimes(long max = MaxPrime)
    {
        if (max < 2) yield break;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxPrime);

        yield return 2;
        for (uint i = 0; i < _bits.Length; i++)
        for (var j = 1; j < WordSize; j += 2)
        {
            if (!IsBitSet(i, j)) continue;
            var num = i * WordSize + j;
            if (num >= max) yield break;
            if (num > _currentSegment)
            {
                SegmentedSieve(_currentSegment + SegmentSize);
                if (!IsBitSet(i, j))
                    continue;
            }

            yield return (uint)num;
        }
    }

    public int Count(long max = MaxPrime)
    {
        if (max < 2) return 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxPrime);

        if (max > _currentSegment)
        {
            var nextSegment = Discrete.DivCeil(max, SegmentSize) * SegmentSize;
            SegmentedSieve(nextSegment);
        }

        var count = 0;
        var length = max / WordSize;
        var remainder = max % WordSize;
        for (var i = 0; i < length; i++) count += BitOperations.PopCount(_bits[i]);

        if (remainder != 0)
        {
            var mask = (1u << (int)remainder) - 1;
            count += BitOperations.PopCount(_bits[length] & mask);
        }

        return count;
    }

    public long NthPrime(long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(n, PrimeCount32);

        if (n == 0) return 2;

        var count = 0L;
        for (long i = 0; i < _bits.Length; i++)
        {
            var num = i * WordSize;
            if (num > _currentSegment)
            {
                SegmentedSieve(_currentSegment + SegmentSize);
            }

            var c = count + BitOperations.PopCount(_bits[i]);
            if (n < c)
            {
                for (int j = 0; j < WordSize; j++, num++)
                {
                    if (IsBitSet(i, j) && count++ == n)
                    {
                        return num;
                    }
                }

                break;
            }

            count = c;
        }

        throw new InvalidOperationException("Prime count is incorrect.");
    }

    public bool IsPrime(long n)
    {
        if (n < 2) return false;

        if (n < _currentSegment) return IsBitSet(n);

        if (n < MaxPrime)
        {
            var nextSegment = Discrete.DivCeil(n, SegmentSize) * SegmentSize;
            SegmentedSieve(nextSegment);
            return IsBitSet(n);
        }

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
        const int maskSize = 3 * 5 * 7 * 11 * 13 * 32 / 32; // LCM of (16#, 32)
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

        // store primes up to 65,536
        var primeSeed = MemoryAllocator.Allocate<long>(PrimeCount16 - 6);
        var n = 0;

        for (var num = 17; num < WordSize; num += 2)
        {
            if (!IsBitSet(0, num))
                continue;

            var k = num * num;
            for (; k < seedMax; k += num << 1) ClearBit(k);

            primeSeed[n++] = num;
        }

        for (uint i = 1; i < seedMax >> 5; i++)
        for (var j = 1; j < WordSize; j += 2)
        {
            if (!IsBitSet(i, j))
                continue;

            var num = i * WordSize + j;
            var k = num * num;
            num = num << 1;
            for (; k < seedMax; k += num) ClearBit(k);

            primeSeed[n++] = num >> 1;
        }

        return primeSeed;
    }

    private void SegmentedSieve(long segmentSize)
    {
        if (_currentSegment == MaxPrime || _currentSegment >= segmentSize) return;
        lock (_lock)
        {
            if (_currentSegment != MaxPrime && _currentSegment < segmentSize)
                SegmentedSieveCore(segmentSize);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SegmentedSieveCore(long segmentSize)
    {
        Debug.Assert(_primeSeed is not null);

        // TODO: ensure worker count align with segment size
        var workerCount = Environment.ProcessorCount;
        var chunkSize = SegmentSize / workerCount;
        Parallel.For(0, workerCount, t =>
        {
            for (var segment = _currentSegment; segment < segmentSize; segment += SegmentSize)
            {
                var start = segment + t * chunkSize;
                var end = start + chunkSize;
                for (var i = 0; i < _primeSeed.Length; i++)
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
                        ClearBit(current);
                        current += step;
                    }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearBit(long index)
    {
        _bits[index >> 5] &= ~(1u << (int)(index & 31));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsBitSet(long index)
    {
        return (_bits[index >> 5] & (1u << (int)(index & 31))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsBitSet(long wordIndex, int bitIndex)
    {
        return (_bits[wordIndex] & (1u << bitIndex)) != 0;
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
                for (; shift < WordSize; shift += n) mask |= 1u << shift;

                if (list.Contains(mask)) break;
                list.Add(mask);

                shift -= WordSize;
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