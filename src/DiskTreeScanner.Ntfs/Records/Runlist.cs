namespace DiskTreeScanner.Ntfs.Records;

/// <summary>A run of clusters: <see cref="Length"/> clusters from <see cref="Vcn"/> mapped to <see cref="Lcn"/> (-1 = sparse).</summary>
public readonly record struct DataRun(long Vcn, long Lcn, long Length)
{
    public bool IsSparse => Lcn < 0;
}

/// <summary>Decoder of non-resident attribute mapping pairs (runlists).</summary>
public static class Runlist
{
    /// <summary>
    /// Decodes mapping pairs starting at <paramref name="startVcn"/>. Each run is a header byte
    /// (low nibble: size of the length field, high nibble: size of the LCN delta field), the run
    /// length, then a <em>signed</em> LCN delta relative to the previous run. A run without delta is
    /// sparse and does not move the current LCN. Returns false on malformed input.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> mappingPairs, long startVcn, List<DataRun> runs)
    {
        long vcn = startVcn;
        long lcn = 0;
        int i = 0;
        while (true)
        {
            if (i >= mappingPairs.Length)
                return false; // no terminator
            byte header = mappingPairs[i++];
            if (header == 0)
                return true;
            int lengthBytes = header & 0x0F;
            int offsetBytes = header >> 4;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || i + lengthBytes + offsetBytes > mappingPairs.Length)
                return false;
            long length = ReadSigned(mappingPairs.Slice(i, lengthBytes));
            i += lengthBytes;
            if (length <= 0)
                return false;
            if (offsetBytes == 0)
            {
                runs.Add(new DataRun(vcn, -1, length));
            }
            else
            {
                lcn += ReadSigned(mappingPairs.Slice(i, offsetBytes));
                i += offsetBytes;
                if (lcn < 0)
                    return false;
                runs.Add(new DataRun(vcn, lcn, length));
            }
            vcn += length;
        }
    }

    /// <summary>Little-endian two's complement integer of 1 to 8 bytes, sign-extended.</summary>
    internal static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int k = bytes.Length - 1; k >= 0; k--)
            value = (value << 8) | bytes[k];
        int shift = 64 - 8 * bytes.Length;
        return shift == 0 ? value : (value << shift) >> shift;
    }

    /// <summary>Encodes runs back to mapping pairs (used by tests to build synthetic records).</summary>
    public static byte[] Encode(IReadOnlyList<DataRun> runs)
    {
        var output = new List<byte>();
        long previousLcn = 0;
        foreach (DataRun run in runs)
        {
            byte[] length = MinimalSigned(run.Length);
            byte[] delta = run.IsSparse ? Array.Empty<byte>() : MinimalSigned(run.Lcn - previousLcn);
            output.Add((byte)(length.Length | (delta.Length << 4)));
            output.AddRange(length);
            output.AddRange(delta);
            if (!run.IsSparse)
                previousLcn = run.Lcn;
        }
        output.Add(0);
        return output.ToArray();
    }

    private static byte[] MinimalSigned(long value)
    {
        for (int n = 1; n <= 8; n++)
        {
            long min = n == 8 ? long.MinValue : -(1L << (8 * n - 1));
            long max = n == 8 ? long.MaxValue : (1L << (8 * n - 1)) - 1;
            if (value >= min && value <= max)
            {
                var bytes = new byte[n];
                for (int k = 0; k < n; k++)
                    bytes[k] = (byte)(value >> (8 * k));
                return bytes;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(value));
    }
}
