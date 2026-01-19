using System.Buffers;
using System.Runtime.CompilerServices;

namespace RuntimeCore.Buffers;

public static class MemoryAllocator
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnmanagedMemory<T> Allocate<T>(int length, bool isZeroed = false) where T : unmanaged
    {
        return new UnmanagedMemory<T>(length, isZeroed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArrayOwner<T> AllocateArray<T>(int length, bool isZeroed = false) where T : unmanaged
    {
        return new ArrayOwner<T>(ArrayPool<T>.Shared, length, isZeroed);
    }

    public static ArrayOwner<T> Clone<T>(this ArrayOwner<T> buffer) where T : unmanaged
    {
        var newBuffer = AllocateArray<T>(buffer.Length);
        buffer.CopyTo(newBuffer);
        return newBuffer;
    }

    public static UnmanagedMemory<T> Clone<T>(this UnmanagedMemory<T> buffer) where T : unmanaged
    {
        var newBuffer = Allocate<T>(buffer.Length);
        buffer.CopyTo(newBuffer);
        return newBuffer;
    }

    extension<T>(IBuffer<T> source) where T : unmanaged
    {
        public void CopyTo(IBuffer<T> destination)
        {
            CopyTo(source, destination, int.Min(source.Length, destination.Length));
        }

        public void CopyTo(IBuffer<T> destination, int count)
        {
            uint byteCount = (uint)(Unsafe.SizeOf<T>() * count);

            switch (byteCount)
            {
                case <= 64:
                {
                    for (int i = 0; i < byteCount; i++)
                        destination[i] = source[i];
                    break;
                }
                default:
                {
                    ref byte sourcePtr = ref Unsafe.As<T, byte>(ref source[0]);
                    ref byte destinationPtr = ref Unsafe.As<T, byte>(ref destination[0]);
                    Unsafe.CopyBlock(ref destinationPtr, ref sourcePtr, byteCount);
                    break;
                }
            }
        }
    }
}