namespace HomuraLog.Domain;

public sealed record MiniTimelineRow(TimelineNodeSnapshot? Node, int OmittedCount = 0,
    bool IsFocused = false, bool IsSelectedBranch = false, bool IsBranchFirstStep = false)
{
    public bool IsOmission => Node == null;
}

public sealed record MiniTimelineSegment(string Id, IReadOnlyList<MiniTimelineRow> Rows,
    IReadOnlyList<MiniTimelineSegment> Children, int HiddenBranchCount, string MoreBranchesNodeId);

public sealed record MiniTimelineProjection(MiniTimelineSegment Root, string FocusedNodeId,
    int BranchCount, int SelectedBranchIndex, int BranchWindowStart,
    IReadOnlyList<string> VisibleBranchNodeIds);

/// <summary>Builds the small graph around a browsing focus, independently of the combat cursor.</summary>
public static class MiniTimelineProjector
{
    public const int ParentPreviewCount = 2;
    public const int VisibleBranchCount = 3;
    public const int BranchPreviewLength = 3;

    public static MiniTimelineProjection Create(TimelineSnapshot snapshot, string? focusedNodeId,
        int selectedBranchIndex, int branchWindowStart)
    {
        List<TimelineNodeSnapshot> path = [];
        if (focusedNodeId == null || !FindPath(snapshot.Root, focusedNodeId, path))
        {
            path.Clear();
            FindPath(snapshot.Root, snapshot.CurrentNodeId, path);
        }
        TimelineNodeSnapshot focus = path[^1];
        IReadOnlyList<TimelineNodeSnapshot> branches = focus.Children;
        int branchCount = branches.Count;
        int selected = branchCount == 0 ? -1 : Math.Clamp(selectedBranchIndex, 0, branchCount - 1);
        int start = Math.Clamp(branchWindowStart, 0, Math.Max(0, branchCount - VisibleBranchCount));
        if (selected >= 0)
        {
            if (selected < start) start = selected;
            else if (selected >= start + VisibleBranchCount) start = selected - VisibleBranchCount + 1;
        }

        int firstParentRow = Math.Max(0, path.Count - ParentPreviewCount - 1);
        if (path[firstParentRow].Action?.Kind == TimelineActionKind.CardChoice)
        {
            int context = firstParentRow;
            while (context > 0 && path[context].Action?.Kind == TimelineActionKind.CardChoice)
                context--;
            if (path[context].Action?.Kind == TimelineActionKind.PlayCard)
                firstParentRow = context;
        }
        MiniTimelineRow[] parentRows = path
            .Skip(firstParentRow)
            .Select(node => new MiniTimelineRow(node, IsFocused: node.NodeId == focus.NodeId))
            .ToArray();
        List<MiniTimelineSegment> children = [];
        List<string> visibleIds = [];
        for (int index = start; index < Math.Min(branchCount, start + VisibleBranchCount); index++)
        {
            TimelineNodeSnapshot first = branches[index];
            visibleIds.Add(first.NodeId);
            List<MiniTimelineRow> rows = [];
            TimelineNodeSnapshot cursor = first;
            for (int depth = 0; depth < BranchPreviewLength; depth++)
            {
                rows.Add(new MiniTimelineRow(cursor, IsSelectedBranch: index == selected && depth == 0,
                    IsBranchFirstStep: depth == 0));
                if (cursor.Children.Count == 0) break;
                cursor = cursor.Children.OrderByDescending(child => child.IsOnCurrentPath)
                    .ThenByDescending(child => child.LastVisitedAt).First();
            }
            children.Add(new MiniTimelineSegment(first.NodeId, rows, [], 0, first.NodeId));
        }
        MiniTimelineSegment root = new(focus.NodeId, parentRows, children, 0, focus.NodeId);
        return new MiniTimelineProjection(root, focus.NodeId, branchCount, selected, start, visibleIds);
    }

    private static bool FindPath(TimelineNodeSnapshot node, string nodeId, List<TimelineNodeSnapshot> path)
    {
        path.Add(node);
        if (node.NodeId == nodeId) return true;
        foreach (TimelineNodeSnapshot child in node.Children)
            if (FindPath(child, nodeId, path)) return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }
}
