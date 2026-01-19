using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RuntimeCore.Buffers;

public sealed unsafe class UnmanagedMemory<T> : MemoryManager<T>, IBuffer<T> where T : unmanaged
{
    private readonly T* _pointer;
    private bool _disposed;

    internal UnmanagedMemory(int length, bool isZeroed)
    {
        Length = length;
        var memoryBlock = NativeMemory.Alloc((uint)length, (uint)sizeof(T));

        if (isZeroed)
        {
            int byteCount = length * sizeof(T);
            Unsafe.InitBlock(memoryBlock, 0, (uint)byteCount);
        }

        _pointer = (T*)memoryBlock;
    }

    public ref T this[long index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return ref _pointer[index];
        }
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return ref _pointer[index];
        }
    }

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => GetSpan();
    }

    public override Memory<T> Memory
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return CreateMemory(Length);
        }
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    public void Dispose()
    {
        Dispose(true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Span<T> GetSpan()
    {
        ThrowIfDisposed();
        return new Span<T>(_pointer, Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ThrowIfDisposed();
        return new MemoryHandle(_pointer + elementIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Unpin()
    {
        // NO-OP, native memory is always pinned
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            NativeMemory.Free(_pointer);
            _disposed = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if !UNCHECKED
        if (!_disposed) return;
        throw new ObjectDisposedException(nameof(UnmanagedMemory<T>));
#endif
    }
}