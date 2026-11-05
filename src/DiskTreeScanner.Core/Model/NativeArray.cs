using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DiskTreeScanner.Core.Model;

/// <summary>Unmanaged scratch array, freed deterministically (keeps transient buffers out of the GC heap).</summary>
internal sealed unsafe class NativeArray<T> : IDisposable where T : unmanaged
{
    public NativeArray(long length, bool zeroed)
    {
        Length = length;
        nuint bytes = (nuint)Math.Max(1, length) * (nuint)sizeof(T);
        Pointer = (T*)(zeroed ? NativeMemory.AllocZeroed(bytes) : NativeMemory.Alloc(bytes));
    }

    public T* Pointer { get; private set; }

    public long Length { get; }

    public ref T this[long index] => ref Pointer[index];

    public void Dispose()
    {
        if (Pointer != null)
        {
            NativeMemory.Free(Pointer);
            Pointer = null;
        }
    }
}

/// <summary>Growable unmanaged stack.</summary>
internal sealed unsafe class NativeStack<T> : IDisposable where T : unmanaged
{
    private T* _items;
    private int _capacity;

    public NativeStack(int capacity)
    {
        _capacity = Math.Max(16, capacity);
        _items = (T*)NativeMemory.Alloc((nuint)_capacity, (nuint)sizeof(T));
    }

    public int Count { get; private set; }

    /// <summary>Reference to the top item; invalidated by the next <see cref="Push"/>.</summary>
    public ref T Top => ref _items[Count - 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in T item)
    {
        if (Count == _capacity)
            Grow();
        _items[Count++] = item;
    }

    public void Pop() => Count--;

    public void Clear() => Count = 0;

    private void Grow()
    {
        _capacity *= 2;
        _items = (T*)NativeMemory.Realloc(_items, (nuint)_capacity * (nuint)sizeof(T));
    }

    public void Dispose()
    {
        if (_items != null)
        {
            NativeMemory.Free(_items);
            _items = null;
        }
    }
}

internal static unsafe class Bits
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSet(ulong* bits, int index) => (bits[index >> 6] & (1UL << index)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(ulong* bits, int index) => bits[index >> 6] |= 1UL << index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear(ulong* bits, int index) => bits[index >> 6] &= ~(1UL << index);
}
