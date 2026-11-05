using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Core.Analysis;

/// <summary>Totals of a node restricted to what matches a filter.</summary>
public readonly record struct FilterTotals(long Size, long Allocated, int Files, int Folders, long Modified)
{
    public int Items => Files + Folders;

    public FilterTotals Add(in FilterTotals other) => new(Size + other.Size, Allocated + other.Allocated, Files + other.Files, Folders + other.Folders, Math.Max(Modified, other.Modified));
}

/// <summary>
/// Name pattern: alternatives separated by ';' or ',', with '*' and '?' wildcards; text without
/// wildcard matches anywhere in the name. Case-insensitive through the volume's case table.
/// </summary>
public sealed class NamePattern
{
    private readonly string[] _alternatives;
    private readonly bool[] _wildcard;
    private readonly UpCaseTable _upCase;

    private NamePattern(string[] alternatives, UpCaseTable upCase)
    {
        _upCase = upCase;
        _alternatives = alternatives.Select(a =>
        {
            var chars = a.ToCharArray();
            upCase.ToUpper(chars, chars);
            return new string(chars);
        }).ToArray();
        _wildcard = _alternatives.Select(a => a.AsSpan().IndexOfAny('*', '?') >= 0).ToArray();
    }

    public string Text => string.Join(";", _alternatives);

    public static NamePattern? Parse(string? text, UpCaseTable upCase)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string[] parts = text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : new NamePattern(parts, upCase);
    }

    public bool IsMatch(ReadOnlySpan<char> name)
    {
        Span<char> buffer = name.Length <= 512 ? stackalloc char[name.Length] : new char[name.Length];
        _upCase.ToUpper(name, buffer);
        ReadOnlySpan<char> upper = buffer;
        for (int i = 0; i < _alternatives.Length; i++)
        {
            if (_wildcard[i] ? Wildcard(upper, _alternatives[i]) : upper.IndexOf(_alternatives[i].AsSpan(), StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>Iterative glob matching with backtracking on the last '*' (linear in practice).</summary>
    private static bool Wildcard(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}

public sealed record SearchResult(int[] Matches, int TotalMatches, long TotalSize, long TotalAllocated);

/// <summary>Search and filter over a subtree (one linear pass over its DFS range).</summary>
public static class NameSearch
{
    /// <summary>Matching files and folders below <paramref name="root"/>, largest first (capped).</summary>
    public static SearchResult Find(TreeModel model, int root, NamePattern pattern, int maxResults, CancellationToken cancellationToken) =>
        Find(model, root, pattern, null, maxResults, cancellationToken);

    /// <summary>
    /// Same, restricted by the value criteria of <paramref name="values"/> (size, allocation, date):
    /// with such criteria only files are listed, as folders have no value of their own to compare.
    /// </summary>
    public static SearchResult Find(TreeModel model, int root, NamePattern pattern, ItemFilter? values, int maxResults, CancellationToken cancellationToken)
    {
        bool checkValues = values is { HasValueCriteria: true };
        var heap = new PriorityQueue<int, long>();
        char[] buffer = new char[NameArena.MaxNameLength];
        int total = 0;
        long size = 0, allocated = 0;
        int end = model.SubtreeEnd[root];
        for (int i = root + 1; i < end; i++)
        {
            if ((i & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (checkValues && (model.IsDirectory(i) || !values!.MatchesValues(model, i)))
                continue;
            int n = model.GetName(i, buffer);
            if (!pattern.IsMatch(buffer.AsSpan(0, n)))
                continue;
            total++;
            size += model.Size[i];
            allocated += model.Allocated[i];
            long key = model.Allocated[i];
            if (heap.Count < maxResults)
                heap.Enqueue(i, key);
            else if (heap.TryPeek(out _, out long smallest) && key > smallest)
                heap.EnqueueDequeue(i, key);
            if (model.IsDirectory(i))
                i = model.SubtreeEnd[i] - 1; // a matching folder counts with its content once
        }
        var matches = new int[heap.Count];
        for (int k = matches.Length - 1; k >= 0; k--)
            matches[k] = heap.Dequeue();
        return new SearchResult(matches, total, size, allocated);
    }

    /// <summary>Per-node totals restricted to the names matching <paramref name="pattern"/> (see the other overload).</summary>
    public static Dictionary<int, FilterTotals> Filter(TreeModel model, int root, NamePattern pattern, CancellationToken cancellationToken) =>
        Filter(model, root, new ItemFilter { Name = pattern }, cancellationToken);

    /// <summary>
    /// Per-node totals restricted to what <paramref name="filter"/> keeps. A file counts itself when
    /// it meets every criterion. A folder whose name matches stands for its content: without value
    /// criteria it counts whole (and stays visible), otherwise its files are checked against the
    /// values only. Ancestors up to <paramref name="root"/> accumulate, and every visible folder
    /// counts as one folder. Nodes absent from the result have no match below them.
    /// </summary>
    public static Dictionary<int, FilterTotals> Filter(TreeModel model, int root, ItemFilter filter, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<int, FilterTotals>();
        char[] buffer = new char[NameArena.MaxNameLength];
        NamePattern? pattern = filter.Name;
        bool values = filter.HasValueCriteria;
        int end = model.SubtreeEnd[root];
        int matchedEnd = -1; // end of the outermost folder whose name matched (values mode)
        for (int i = root + 1; i < end; i++)
        {
            if ((i & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            bool inherited = i < matchedEnd;
            bool directory = model.IsDirectory(i);
            if (directory && pattern is null)
                continue; // folders show through their matching files
            if (!inherited && pattern is not null)
            {
                int n = model.GetName(i, buffer);
                if (!pattern.IsMatch(buffer.AsSpan(0, n)))
                    continue;
            }
            if (directory)
            {
                if (values)
                {
                    if (!inherited)
                        matchedEnd = model.SubtreeEnd[i];
                    continue;
                }
                // Name only: the folder counts with all its content, which stays visible.
                totals[i] = new FilterTotals(model.Size[i], model.Allocated[i], model.FileCount(i), model.FolderCount(i), model.Modified[i]);
                int last = model.SubtreeEnd[i];
                for (int d = i + 1; d < last; d++)
                {
                    totals[d] = model.IsDirectory(d)
                        ? new FilterTotals(model.Size[d], model.Allocated[d], model.FileCount(d), model.FolderCount(d), model.Modified[d])
                        : new FilterTotals(model.Size[d], model.Allocated[d], 0, 0, model.Modified[d]);
                }
                Reveal(totals, model, i, new FilterTotals(model.Size[i], model.Allocated[i], model.FileCount(i), model.FolderCount(i) + 1, model.Modified[i]), root);
                i = last - 1;
                continue;
            }
            if (values && !filter.MatchesValues(model, i))
                continue;
            totals[i] = new FilterTotals(model.Size[i], model.Allocated[i], 0, 0, model.Modified[i]);
            Reveal(totals, model, i, new FilterTotals(model.Size[i], model.Allocated[i], 1, 0, model.Modified[i]), root);
        }
        return totals;
    }

    /// <summary>
    /// Adds what a kept node brings (<paramref name="own"/>) to its ancestors up to <paramref name="root"/>.
    /// An ancestor reached for the first time becomes a visible folder: the ones above count it.
    /// </summary>
    private static void Reveal(Dictionary<int, FilterTotals> totals, TreeModel model, int node, FilterTotals own, int root)
    {
        int newFolders = 0;
        for (int a = model.Parent[node]; a >= 0; a = model.Parent[a])
        {
            bool existed = totals.TryGetValue(a, out FilterTotals t);
            FilterTotals sum = existed ? t.Add(own) : own;
            totals[a] = sum with { Folders = sum.Folders + newFolders };
            if (a == root)
                break;
            if (!existed)
                newFolders++;
        }
    }
}
