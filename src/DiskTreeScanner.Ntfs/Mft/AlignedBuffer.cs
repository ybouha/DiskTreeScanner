using System.Runtime.InteropServices;

namespace DiskTreeScanner.Ntfs.Mft;

/// <summary>Sector/page-aligned native buffer, as required by unbuffered reads.</summary>
internal sealed unsafe class AlignedBuffer : IDisposable
{
    public const int Alignment = 4096;

    public AlignedBuffer(int length)
    {
        Length = length;
        Pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, Alignment);
    }

    public byte* Pointer { get; private set; }

    public int Length { get; }

    public Span<byte> Span => new(Pointer, Length);

    public void Dispose()
    {
        if (Pointer != null)
        {
            NativeMemory.AlignedFree(Pointer);
            Pointer = null;
        }
    }
}
