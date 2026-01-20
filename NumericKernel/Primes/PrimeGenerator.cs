using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    private volatile int _state = NotGenerated;
    private int _primeCount = 0;

    public void Generate()
    {
        int currentState = Interlocked.CompareExchange(ref _state, Generating, NotGenerated);
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
        Sieving();

        _state = Generated;
    }

    public IEnumerable<uint> EnumeratePrimes()
    {
        Generate();
        for (uint i = 0; i < _bits.Length; i++)
        {
            for (int j = 1; j < 32; j += 2)
            {
                if ((_bits[i] & (1u << j)) == 0)
                    continue;

                long num = i * 32 + j;
                yield return (uint)num;
            }
        }
    }

    public int Count()
    {
        if (_primeCount != 0)
            return _primeCount;

        Generate();
        int count = 0;
        for (int i = 0; i < _bits.Length; i++)
        {
            count += BitOperations.PopCount(_bits[i]);
        }

        return _primeCount = count;
    }

    private void WheelFactorization()
    {
        Debug.Write("Start wheel factorization... ");
        Span<uint> masks = [
            ~0xd7dd75dfu,
            ~0xf5f75d77u,
            ~0x7d7dd75du,
            ~0xdf5f75d7u,
            ~0x77d7dd75u,
            ~0x5df5f75du,
            ~0xd77d7dd7u,
            ~0x75df5f75u,
            ~0x5d77d7ddu,
            ~0xd75df5f7u,
            ~0x75d77d7du,
            ~0xdd75df5fu,
            ~0xf75d77d7u,
            ~0x7dd75df5u,
            ~0x5f75d77du,
        ];

        const int segmentSize = 120 * 1024;
        for (int i = 1, j = 0; i <= segmentSize;)
        {
            _bits[i++] = masks[j++];
            if(j == masks.Length) j = 0;
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

    private UnmanagedMemory<(uint Base, long Current)> GeneratePrimeSeed()
    {
        const int seedMax = 65536;
    
        // store primes up to 65536
        var primeSeed = MemoryAllocator.Allocate<(uint Base, long Current)>(6539);
        int n = 0;
    
        for (int i = 7; i < 32; i += 2)
        {
            if ((_bits[0] & (1u << i)) == 0)
                continue;

            long num = i;
            long k = num * 3L;
            for (; k < seedMax; k += 2L * num)
            {
                uint mask = 1u << (int)(k & 31);
                _bits[k >> 5] &= ~mask;
            }

            primeSeed[n++] = ((uint)num, k);
        }

        for (uint i = 1; i < seedMax >> 5; i++)
        {
            for (int j = 1; j < 32; j += 2)
            {
                if ((_bits[i] & (1u << j)) == 0)
                    continue;

                long num = i * 32 + j;
                long k = num * 3L;
                for (; k < seedMax; k += 2L * num)
                {
                    uint mask = 1u << (int)(k & 31);
                    _bits[k >> 5] &= ~mask;
                }

                primeSeed[n++] = ((uint)num, k);
            }
        }

        return primeSeed;
    }

    private void Sieving()
    {
        using var primeSeed = GeneratePrimeSeed();

        const int segmentSize = 128 * 32 * 1024;
        const int workerCount = 8;

        Parallel.For(0, workerCount, t =>
        {
            for (long segment = segmentSize; segment <= MaxPrime; segment += segmentSize)
            {
                for (int i = t; i < primeSeed.Length; i += workerCount)
                {
                    ref var seed = ref primeSeed[i];
                    for (; seed.Current < segment; seed.Current += 2L * seed.Base)
                    {
                        uint mask = ~(1u << (int)(seed.Current & 31));
                        long index = seed.Current >> 5;
                        uint current = _bits[index];
                        while (current != Interlocked.CompareExchange(ref _bits[index], current & mask, current))
                        {
                            current = _bits[index];
                        }
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        _bits.Dispose();
    }
}