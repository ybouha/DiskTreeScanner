using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Core.Analysis;

/// <summary>
/// File criteria of the column filters: name pattern, size, allocation and last change (inclusive
/// bounds, null = unbounded). Files must meet every criterion; a folder whose name matches the
/// pattern stands for its content, which is then only checked against the value criteria.
/// </summary>
public sealed class ItemFilter
{
    public NamePattern? Name { get; init; }

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public long? MinAllocated { get; init; }

    public long? MaxAllocated { get; init; }

    /// <summary>FILETIME (UTC) bounds of the last change.</summary>
    public long? ModifiedFrom { get; init; }

    public long? ModifiedTo { get; init; }

    public bool HasValueCriteria =>
        MinSize is not null || MaxSize is not null || MinAllocated is not null || MaxAllocated is not null || ModifiedFrom is not null || ModifiedTo is not null;

    public bool IsEmpty => Name is null && !HasValueCriteria;

    /// <summary>Size, allocation and date of <paramref name="node"/> within the bounds.</summary>
    public bool MatchesValues(TreeModel model, int node)
    {
        long size = model.Size[node], allocated = model.Allocated[node], modified = model.Modified[node];
        return (MinSize is not { } minSize || size >= minSize)
            && (MaxSize is not { } maxSize || size <= maxSize)
            && (MinAllocated is not { } minAllocated || allocated >= minAllocated)
            && (MaxAllocated is not { } maxAllocated || allocated <= maxAllocated)
            && (ModifiedFrom is not { } from || modified >= from)
            && (ModifiedTo is not { } to || modified <= to);
    }
}
