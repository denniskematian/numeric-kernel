using System.Collections.ObjectModel;
using System.Numerics;
using NumericKernel.Primes;

namespace NumericKernel;

public static class Discrete
{
    public static long DivCeil(long a, long b) => (a + b - 1) / b;
    
    public static int DivCeil(int a, int b) => (a + b - 1) / b;
    
    public static uint DivCeil(uint a, uint b) => (a + b - 1) / b;

    public static long Sqrt(long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if(n < int.MaxValue) return Sqrt((int)n);

        var k = (int)long.Log2(n) >> 1;
        var low = 1L << k;
        var high = low * 2 - 1;
        var root = 0L;
        while (low <= high)
        {
            var mid = (high + low) >> 1;
            if (mid * mid <= n)
            {
                root = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
    
        return root;
    }

    public static int Sqrt(int n)
    {
        // All value <= n is safe to calculate sqrt using FP sqrt and cast it to int.
        return (int)Math.Sqrt(n);
    }
    
    public static int ExtractDigit(int n, int digit)
    {
        return digit switch
        {
            1  => n / 1 % 10,
            2  => n / 10 % 10,
            3  => n / 100 % 10,
            4  => n / 1000 % 10,
            5  => n / 10000 % 10,
            6  => n / 100000 % 10,
            7  => n / 1000000 % 10,
            8  => n / 10000000 % 10,
            9  => n / 100000000 % 10,
            10 => n / 1000000000 % 10,
            _ => throw new ArgumentOutOfRangeException(nameof(digit))
        };
    }
    
    public static IEnumerable<int> PrimeFactors(int n)
    {
        if(n <= 1) yield break;
        if(Prime.IsPrime(n)) yield break;
        
        foreach (var prime in Prime.EnumeratePrimes())
        {
            if(prime > n) break;
            while (n % prime == 0)
            {
                n /= (int)prime;
                yield return (int)prime;
            }
        }
    }

    public static IEnumerable<long> PrimeFactors(long n)
    {
        if(n <= 1) yield break;
        if(Prime.IsPrime(n)) yield break;
        
        foreach (var prime in Prime.EnumeratePrimes())
        {
            if(prime > n) break;
            if (n % prime == 0)
            {
                n /= prime;
                yield return prime;
                if (Prime.IsPrime(n))
                {
                    yield return n;
                    yield break;
                }
            }
        }
    }

    private static ReadOnlyDictionary<int, int> PrimeFactorGroup(int n)
    {
        var dict = new Dictionary<int, int>();
        foreach (var prime in PrimeFactors(n))
        {
            dict[prime] = dict.GetValueOrDefault(prime, 0) + 1;
        }

        return dict.AsReadOnly();
    }
    
    public static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    public static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);

    public static long Lcm(IReadOnlyList<int> nums)
    {
        var dict = new Dictionary<int, int>();
        foreach (var n in nums)
        {
            if (n <= 1) continue;

            if (Prime.IsPrime(n) && dict.TryAdd(n, 1))
            {
                continue;
            }

            foreach (var (p, t) in PrimeFactorGroup(n))
            {
                dict[p] = Math.Max(dict.GetValueOrDefault(p, 0), t);
            }
        }
        
        if (dict.Count == 0) return 0;

        long result = 1;
        foreach (var (p, t) in dict)
        {
            for(int i = 0; i < t; i++) result *= p;
        }

        return result;
    }
}