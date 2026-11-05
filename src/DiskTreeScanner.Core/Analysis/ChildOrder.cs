using DiskTreeScanner.Core.Model;

namespace DiskTreeScanner.Core.Analysis;

public enum SortColumn
{
    Name,
    Size,
    Allocated,
    Items,
    Files,
    Folders,
    Modified,
}

/// <summary>Sorted children of a node (computed on demand: only when a folder is expanded).</summary>
public static class ChildOrder
{
    public static int[] Sorted(TreeModel model, int node, SortColumn column, bool descending)
    {
        int n = model.ChildCount(node);
        var children = new int[n];
        int k = 0;
        foreach (int c in model.Children(node))
            children[k++] = c;
        Sort(model, children, column, descending);
        return children;
    }

    public static void Sort(TreeModel model, int[] nodes, SortColumn column, bool descending)
    {
        if (nodes.Length < 2)
            return;
        if (column == SortColumn.Name)
        {
            var comparer = new NameComparer(model, descending);
            Array.Sort(nodes, comparer);
            return;
        }
        var keys = new long[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            int x = nodes[i];
            long value = column switch
            {
                SortColumn.Size => model.Size[x],
                SortColumn.Allocated => model.Allocated[x],
                SortColumn.Items => model.ItemCount(x),
                SortColumn.Files => model.FileCount(x),
                SortColumn.Folders => model.FolderCount(x),
                SortColumn.Modified => model.Modified[x],
                _ => 0,
            };
            keys[i] = descending ? -value : value; // values are >= 0
        }
        Array.Sort(keys, nodes);
        // Array.Sort is not stable: order equal keys by position for a deterministic display.
        int start = 0;
        for (int i = 1; i <= nodes.Length; i++)
        {
            if (i == nodes.Length || keys[i] != keys[start])
            {
                if (i - start > 1)
                    Array.Sort(nodes, start, i - start);
                start = i;
            }
        }
    }

    private sealed class NameComparer : IComparer<int>
    {
        private readonly TreeModel _model;
        private readonly bool _descending;
        private readonly char[] _a = new char[NameArena.MaxNameLength];
        private readonly char[] _b = new char[NameArena.MaxNameLength];

        public NameComparer(TreeModel model, bool descending)
        {
            _model = model;
            _descending = descending;
        }

        public int Compare(int x, int y)
        {
            int la = _model.GetName(x, _a);
            int lb = _model.GetName(y, _b);
            int c = _model.UpCase.Compare(_a.AsSpan(0, la), _b.AsSpan(0, lb));
            if (c == 0)
                c = x.CompareTo(y);
            return _descending ? -c : c;
        }
    }
}
