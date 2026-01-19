using System.Diagnostics;
using System.Numerics;
using RuntimeCore.Buffers;

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

        _bits[0] = 0xA08A28AC; // bit-packet primes < 32
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
        WheelFactor[] factors =
        [
            new WheelFactor(2),
            new WheelFactor(3),
            new WheelFactor(5),
            new WheelFactor(7),
            new WheelFactor(11),
            new WheelFactor(13),
            new WheelFactor(17),
            new WheelFactor(19),
            new WheelFactor(23),
            new WheelFactor(29),
            new WheelFactor(31),
        ];

        // Cross-out by wheel factorization
        for (int i = 1; i < _bits.Length; i++)
        {
            uint mask = 0;
            foreach (var factor in factors)
            {
                mask |= factor.Next();
            }

            _bits[i] = ~mask;
        }

        Debug.WriteLine("Done.");
    }

    private void Sieving()
    {
        Debug.WriteLine("Start sieving... ");
        for (uint i = 1; i < _bits.Length; i++)
        {
            for (int j = 1; j < 32; j += 2)
            {
                if ((_bits[i] & (1u << j)) == 0)
                    continue;

                long num = i * 32 + j;
                for (long k = num * 3L; k < MaxPrime; k += 2L * num)
                {
                    uint mask = 1u << (int)(k % 32);
                    _bits[k / 32] &= ~mask;
                }
            }
        }

        Debug.WriteLine("Done.");
    }

    private class WheelFactor
    {
        private readonly uint[] _wheel;
        private int _index;

        public WheelFactor(int n)
        {
            var list = new List<uint>(32);
            int shift = 0;
            while (true)
            {
                uint mask = 0;
                for (; shift < sizeof(uint) * 8; shift += n)
                {
                    mask |= 1u << shift;
                }

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

    public void Dispose()
    {
        _bits.Dispose();
    }
}