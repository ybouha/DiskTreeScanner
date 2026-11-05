using System.Globalization;
using System.Text;
using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Core.Export;

/// <summary>CSV export of a subtree: one sequential pass over its DFS range, paths built incrementally.</summary>
public static class CsvExporter
{
    public const string Header = "Path,Type,Size,Allocated,Items,Files,Folders,Modified,Attributes";

    public static long Export(TreeModel model, int node, string path, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
        return Export(model, node, writer, cancellationToken, progress);
    }

    public static long Export(TreeModel model, int node, TextWriter writer, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        writer.WriteLine(Header);
        int end = model.SubtreeEnd[node];
        var buffer = new char[40_000];
        var stackNode = new List<int>();
        var stackLength = new List<int>();
        string rootPath = model.GetPath(node);
        Span<char> scratch = stackalloc char[64];
        long rows = 0;
        for (int i = node; i < end; i++)
        {
            int length;
            if (i == node)
            {
                rootPath.CopyTo(buffer);
                length = rootPath.Length;
            }
            else
            {
                int parent = model.Parent[i];
                while (stackNode[^1] != parent)
                {
                    stackNode.RemoveAt(stackNode.Count - 1);
                    stackLength.RemoveAt(stackLength.Count - 1);
                }
                length = stackLength[^1];
                if (length == 0 || buffer[length - 1] != '\\')
                    buffer[length++] = '\\';
                length += model.GetName(i, buffer.AsSpan(length));
            }
            bool directory = model.IsDirectory(i);
            if (directory)
            {
                stackNode.Add(i);
                stackLength.Add(length);
            }

            WriteQuoted(writer, buffer.AsSpan(0, length));
            writer.Write(directory ? ",Folder," : ",File,");
            WriteNumber(writer, model.Size[i], scratch);
            writer.Write(',');
            WriteNumber(writer, model.Allocated[i], scratch);
            writer.Write(',');
            if (directory)
            {
                WriteNumber(writer, model.ItemCount(i), scratch);
                writer.Write(',');
                WriteNumber(writer, model.FileCount(i), scratch);
                writer.Write(',');
                WriteNumber(writer, model.FolderCount(i), scratch);
            }
            else
            {
                writer.Write(",,");
            }
            writer.Write(',');
            WriteDate(writer, model.Modified[i], scratch);
            writer.Write(',');
            writer.Write(AttributeLetters(model.GetFlags(i), scratch));
            writer.WriteLine();
            rows++;
            if ((rows & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)(i - node) / Math.Max(1, end - node));
            }
        }
        progress?.Report(1);
        return rows;
    }

    private static void WriteQuoted(TextWriter writer, ReadOnlySpan<char> value)
    {
        writer.Write('"');
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '"')
            {
                writer.Write(value[start..(i + 1)]);
                writer.Write('"');
                start = i + 1;
            }
        }
        writer.Write(value[start..]);
        writer.Write('"');
    }

    private static void WriteNumber(TextWriter writer, long value, Span<char> scratch)
    {
        value.TryFormat(scratch, out int written, default, CultureInfo.InvariantCulture);
        writer.Write(scratch[..written]);
    }

    private static void WriteDate(TextWriter writer, long fileTime, Span<char> scratch)
    {
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
            return;
        DateTime local = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        if (local.TryFormat(scratch, out int written, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            writer.Write(scratch[..written]);
    }

    /// <summary>Compact attribute letters: D R H S A C E P L O W K X.</summary>
    public static ReadOnlySpan<char> AttributeLetters(NodeFlags f, Span<char> scratch)
    {
        int n = 0;
        if ((f & NodeFlags.Directory) != 0) scratch[n++] = 'D';
        if ((f & NodeFlags.ReadOnly) != 0) scratch[n++] = 'R';
        if ((f & NodeFlags.Hidden) != 0) scratch[n++] = 'H';
        if ((f & NodeFlags.System) != 0) scratch[n++] = 'S';
        if ((f & NodeFlags.Archive) != 0) scratch[n++] = 'A';
        if ((f & NodeFlags.Compressed) != 0) scratch[n++] = 'C';
        if ((f & NodeFlags.Encrypted) != 0) scratch[n++] = 'E';
        if ((f & NodeFlags.Sparse) != 0) scratch[n++] = 'P';
        if ((f & NodeFlags.ReparsePoint) != 0) scratch[n++] = 'L';
        if ((f & NodeFlags.CloudPlaceholderMask) != 0) scratch[n++] = 'O';
        if ((f & NodeFlags.WofCompressed) != 0) scratch[n++] = 'W';
        if ((f & NodeFlags.HardLinkSecondary) != 0) scratch[n++] = 'K';
        if ((f & NodeFlags.NtfsSpecial) != 0) scratch[n++] = 'X';
        return scratch[..n];
    }
}
