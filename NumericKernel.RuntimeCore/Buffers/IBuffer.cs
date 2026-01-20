using System.Buffers;

namespace NumericKernel.RuntimeCore.Buffers;

public interface IBuffer<T> : IMemoryOwner<T> where T : unmanaged
{
    ref T this[int index] { get; }

    Span<T> Span { get; }

    int Length { get; }
}