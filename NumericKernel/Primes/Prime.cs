using System.Runtime.CompilerServices;

namespace NumericKernel.Primes;

public static class Prime
{
    private static readonly Lazy<PrimeGenerator> s_generator = new(() => new PrimeGenerator());

    private static PrimeGenerator Generator
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => s_generator.Value;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count(long max = PrimeGenerator.MaxPrime) => Generator.Count(max);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrime(long n) => Generator.IsPrime(n);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NthPrime(long n) => Generator.NthPrime(n);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<uint> EnumeratePrimes() => Generator.EnumeratePrimes();
}