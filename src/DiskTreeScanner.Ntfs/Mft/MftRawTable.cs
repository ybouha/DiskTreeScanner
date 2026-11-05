using System.Runtime.InteropServices;

namespace DiskTreeScanner.Ntfs.Mft;

/// <summary>
/// Per-slot arrays filled by the parsers. Slots <c>[0, RecordSlots)</c> are the in-use MFT records
/// (compact numbering from the MFT bitmap); slots after them hold extra hard-link names and the
/// &lt;orphans&gt; folder. After the tree is built these same arrays are permuted in place into DFS
/// order and become the model, so no second copy of the per-node data is ever allocated.
/// </summary>
/// <remarks>
/// Sequence numbers are only needed to validate references, so they live in native memory and are
/// released as soon as parents are resolved, before the tree build reaches its memory peak.
/// </remarks>
internal sealed unsafe class MftRawTable : IDisposable
{
    /// <summary>Internal marker in <see cref="Flags"/>: the slot is a base record usable as a node.</summary>
    public const uint RawValid = 0x0080_0000;

    /// <summary><see cref="ParentRecord"/> code: the slot is not a node.</summary>
    public const int NotANode = -1;

    /// <summary><see cref="ParentRecord"/> code: base record whose names are all in extension records.</summary>
    public const int AwaitingName = -3;

    private ushort* _sequence;
    private ushort* _parentSequence;
    private int _parentSequenceCapacity;

    public MftRawTable(int recordSlots)
    {
        RecordSlots = recordSlots;
        Count = recordSlots;
        // Hard links need extra slots; 1/16 covers typical systems (WinSxS) without a resize.
        Capacity = recordSlots + recordSlots / 16 + 4096;
        _sequence = (ushort*)NativeMemory.AllocZeroed((nuint)recordSlots + 1, sizeof(ushort));
        _parentSequenceCapacity = Capacity;
        _parentSequence = (ushort*)NativeMemory.AllocZeroed((nuint)Capacity, sizeof(ushort));
        ParentRecord = new int[Capacity];
        Array.Fill(ParentRecord, NotANode);
        Size = new long[Capacity];
        Allocated = new long[Capacity];
        Modified = new long[Capacity];
        Flags = new uint[Capacity];
        NameOffset = new int[Capacity];
        NameLength = new ushort[Capacity];
    }

    public int RecordSlots { get; }

    public int Count { get; private set; }

    public int Capacity { get; private set; }

    /// <summary>Sequence number of each record slot (validates parent and base references).</summary>
    public Span<ushort> Sequence => _sequence != null ? new Span<ushort>(_sequence, RecordSlots) : throw new ObjectDisposedException(nameof(Sequence));

    /// <summary>Sequence number expected for each slot's parent.</summary>
    public Span<ushort> ParentSequence => _parentSequence != null ? new Span<ushort>(_parentSequence, _parentSequenceCapacity) : throw new ObjectDisposedException(nameof(ParentSequence));

    /// <summary>Parent record number, then (in place) parent slot, then the DFS order, then the record number per node.</summary>
    public int[] ParentRecord;

    public long[] Size;

    public long[] Allocated;

    public long[] Modified;

    public uint[] Flags;

    public int[] NameOffset;

    public ushort[] NameLength;

    /// <summary>Grows once, exactly, before extra slots are added (avoids repeated reallocation).</summary>
    public void EnsureCapacity(int required)
    {
        if (required <= Capacity)
            return;
        int capacity = required + 1024;
        Array.Resize(ref ParentRecord, capacity);
        Array.Resize(ref Size, capacity);
        Array.Resize(ref Allocated, capacity);
        Array.Resize(ref Modified, capacity);
        Array.Resize(ref Flags, capacity);
        Array.Resize(ref NameOffset, capacity);
        Array.Resize(ref NameLength, capacity);
        _parentSequence = (ushort*)NativeMemory.Realloc(_parentSequence, (nuint)capacity * sizeof(ushort));
        _parentSequenceCapacity = capacity;
        Capacity = capacity;
        // The old arrays are garbage now: reclaim them before the peak of the tree build.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    public int AddSlot()
    {
        if (Count == Capacity)
            EnsureCapacity(Count + Math.Max(Count / 64, 4096));
        int slot = Count++;
        ParentRecord[slot] = NotANode;
        return slot;
    }

    /// <summary>Frees the sequence numbers once references are validated.</summary>
    public void ReleaseSequences()
    {
        if (_sequence != null)
        {
            NativeMemory.Free(_sequence);
            _sequence = null;
        }
        if (_parentSequence != null)
        {
            NativeMemory.Free(_parentSequence);
            _parentSequence = null;
        }
    }

    public void Dispose() => ReleaseSequences();
}
