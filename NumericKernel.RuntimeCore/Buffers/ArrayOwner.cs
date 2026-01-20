using System.Buffers;
using System.Runtime.CompilerServices;

namespace NumericKernel.RuntimeCore.Buffers;

public sealed class ArrayOwner<T> : IBuffer<T> where T : unmanaged
{
    private readonly T[] _array;
    private readonly ArrayPool<T> _owner;
    private bool _isDisposed;

    internal ArrayOwner(ArrayPool<T> owner, int length, bool isZeroed)
    {
        _owner = owner;
        Length = length;
        _array = owner.Rent(length);

        if (isZeroed)
            for (int i = 0; i < length; i++)
                _array[i] = default;
    }

    public ref T this[int index]
    {
        get
        {
            ThrowIfDisposed();
            if (index >= Length || index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));

            return ref _array[index];
        }
    }

    public Span<T> Span
    {
        get
        {
            ThrowIfDisposed();
            return _array.AsSpan(0, Length);
        }
    }

    public Memory<T> Memory
    {
        get
        {
            ThrowIfDisposed();
            return _array.AsMemory(0, Length);
        }
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _owner.Return(_array);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if !UNCHECKED
        if (!_isDisposed) return;
        throw new ObjectDisposedException(nameof(ArrayOwner<T>));
#endif
    }
}