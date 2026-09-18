namespace HomuraLog.Domain;

public sealed record MiniTimelineRow(TimelineNodeSnapshot? Node, int OmittedCount = 0)
{
    public bool IsOmission => Node == null;
}

public sealed record MiniTimelineSegment(
    string Id,
    IReadOnlyList<MiniTimelineRow> Rows,
    IReadOnlyList<MiniTimelineSegment> Children,
    int HiddenBranchCount,
    string MoreBranchesNodeId);

public static class MiniTimelineProjector
{
    private const int AncestorDecisions = 2;
    private const int DescendantDecisions = 2;
    private const int BranchCap = 6;
    private const int ActionsBeforeCurrent = 3;
    private const int ActionsAfterCurrent = 2;

    public static MiniTimelineSegment Create(TimelineSnapshot snapshot, string? preferredNextNodeId = null)
    {
        RawSegment root = BuildSegments(snapshot.Root, null);
        RawSegment current = Flatten(root).First(segment =>
            segment.Nodes.Any(node => node.NodeId == snapshot.CurrentNodeId));
        List<RawSegment> path = [];
        for (RawSegment? cursor = current; cursor != null; cursor = cursor.Parent) path.Add(cursor);
        path.Reverse();
        int startIndex = Math.Max(0, path.Count - 1 - AncestorDecisions);
        RawSegment start = path[startIndex];
        HashSet<string> mainPath = path.Skip(startIndex).Select(segment => segment.Id)
            .ToHashSet(StringComparer.Ordinal);
        int earlierActions = path.Take(startIndex)
            .Sum(segment => segment.Nodes.Count(node => node.Action != null));
        return Project(start, current.Id, mainPath, DescendantDecisions, earlierActions,
            preferredNextNodeId);
    }

    private static MiniTimelineSegment Project(RawSegment segment, string currentId,
        HashSet<string> mainPath, int descendantDepth, int earlierActions = 0,
        string? preferredNextNodeId = null)
    {
        bool isCurrent = segment.Id == currentId;
        RawSegment? pathChild = segment.Children.FirstOrDefault(child => mainPath.Contains(child.Id));
        RawSegment[] ordered = segment.Children
            .OrderByDescending(child => ReferenceEquals(child, pathChild))
            .ThenByDescending(child => child.Nodes.Max(node => node.LastVisitedAt)).ToArray();
        RawSegment[] selected = ordered.Take(BranchCap).ToArray();
        if (isCurrent && preferredNextNodeId != null)
        {
            RawSegment? preferred = ordered.FirstOrDefault(child =>
                child.Nodes[0].NodeId == preferredNextNodeId);
            if (preferred != null && !selected.Contains(preferred))
                selected[^1] = preferred;
        }
        int hidden = Math.Max(0, segment.Children.Count - selected.Length);
        List<MiniTimelineSegment> children = [];
        foreach (RawSegment child in selected)
        {
            bool continuesToCurrent = mainPath.Contains(child.Id);
            children.Add(continuesToCurrent
                ? Project(child, currentId, mainPath, descendantDepth, preferredNextNodeId: preferredNextNodeId)
                : ProjectPreview(child, isCurrent ? descendantDepth : 1));
        }
        return new MiniTimelineSegment(segment.Id,
            ClipRows(segment.Nodes, isCurrent, earlierActions), children, hidden, segment.Nodes[^1].NodeId);
    }

    private static MiniTimelineSegment ProjectPreview(RawSegment segment, int depth)
    {
        if (depth <= 1)
            return new MiniTimelineSegment(segment.Id, ClipEdge(segment.Nodes), [],
                segment.Children.Count, segment.Nodes[^1].NodeId);
        RawSegment[] selected = segment.Children
            .OrderByDescending(child => child.Nodes.Max(node => node.LastVisitedAt))
            .Take(BranchCap).ToArray();
        return new MiniTimelineSegment(segment.Id, ClipEdge(segment.Nodes),
            selected.Select(child => ProjectPreview(child, depth - 1)).ToArray(),
            Math.Max(0, segment.Children.Count - selected.Length), segment.Nodes[^1].NodeId);
    }

    private static IReadOnlyList<MiniTimelineRow> ClipRows(
        IReadOnlyList<TimelineNodeSnapshot> nodes, bool containsCurrent, int earlierActions)
    {
        List<MiniTimelineRow> rows = [];
        if (!containsCurrent)
        {
            int keep = Math.Min(2, nodes.Count);
            int omitted = earlierActions + nodes.Count - keep;
            if (omitted > 0) rows.Add(new MiniTimelineRow(null, omitted));
            rows.AddRange(nodes.Skip(nodes.Count - keep).Select(node => new MiniTimelineRow(node)));
            return rows;
        }
        int current = nodes.ToList().FindIndex(node => node.IsCurrent);
        int first = Math.Max(0, current - ActionsBeforeCurrent);
        int last = Math.Min(nodes.Count - 1, current + ActionsAfterCurrent);
        if (first > 0) rows.Add(new MiniTimelineRow(null, first));
        for (int index = first; index <= last; index++) rows.Add(new MiniTimelineRow(nodes[index]));
        if (last + 1 < nodes.Count) rows.Add(new MiniTimelineRow(null, nodes.Count - last - 1));
        return rows;
    }

    private static IReadOnlyList<MiniTimelineRow> ClipEdge(IReadOnlyList<TimelineNodeSnapshot> nodes)
    {
        const int previewCount = 2;
        List<MiniTimelineRow> rows = nodes.Take(previewCount).Select(node => new MiniTimelineRow(node)).ToList();
        if (nodes.Count > previewCount) rows.Add(new MiniTimelineRow(null, nodes.Count - previewCount));
        return rows;
    }

    private static RawSegment BuildSegments(TimelineNodeSnapshot start, RawSegment? parent)
    {
        List<TimelineNodeSnapshot> chain = [start];
        TimelineNodeSnapshot tail = start;
        while (tail.Children.Count == 1)
        {
            tail = tail.Children[0];
            chain.Add(tail);
        }
        RawSegment segment = new(start.NodeId, chain, parent);
        segment.Children.AddRange(tail.Children.Select(child => BuildSegments(child, segment)));
        return segment;
    }

    private static IEnumerable<RawSegment> Flatten(RawSegment root)
    {
        yield return root;
        foreach (RawSegment child in root.Children)
        foreach (RawSegment descendant in Flatten(child)) yield return descendant;
    }

    private sealed class RawSegment(string id, IReadOnlyList<TimelineNodeSnapshot> nodes, RawSegment? parent)
    {
        public string Id { get; } = id;
        public IReadOnlyList<TimelineNodeSnapshot> Nodes { get; } = nodes;
        public RawSegment? Parent { get; } = parent;
        public List<RawSegment> Children { get; } = [];
    }
}
