namespace HomuraLog.Domain;

public sealed class TimelineTree
{
    private readonly EncounterRecord _record;
    private readonly List<TimelineNode> _path = [];

    public TimelineTree(EncounterRecord record)
    {
        _record = record;
        _path.Add(record.Root);
    }

    public EncounterRecord Record => _record;
    public TimelineNode Current => _path[^1];

    public TimelineNode Append(TimelineAction action, CombatStateSummary state, DateTimeOffset now)
    {
        if (!Current.Children.TryGetValue(action.Key, out TimelineNode? node))
        {
            node = new TimelineNode
            {
                ParentNodeId = Current.NodeId,
                Action = action,
                State = state,
                FirstVisitedAt = now,
                LastVisitedAt = now,
                VisitCount = 1,
                Outcome = TimelineOutcome.Ongoing,
            };
            Current.Children.Add(action.Key, node);
        }
        else
        {
            // Enrich records created before hand position was captured without changing
            // their stable action key or creating a duplicate branch.
            if (action.HandPosition.HasValue) node.Action = action;
            node.VisitCount++;
            node.LastVisitedAt = now;
            node.State = state;
            node.Outcome = TimelineOutcome.Ongoing;
        }
        _path.Add(node);
        return node;
    }

    public void MarkCurrent(TimelineOutcome outcome, DateTimeOffset now)
    {
        Current.Outcome = outcome;
        Current.LastVisitedAt = now;
        _record.Outcome = outcome;
        if (outcome is TimelineOutcome.Victory or TimelineOutcome.Defeat)
            _record.EndedAt = now;
    }

    public bool Remove(string nodeId)
    {
        if (nodeId == _record.Root.NodeId) return false;
        TimelineNode? parent = FindParent(_record.Root, nodeId);
        if (parent == null) return false;
        bool removed = parent.Children.Remove(nodeId);
        if (removed)
        {
            int index = _path.FindIndex(node => node.NodeId == nodeId);
            if (index >= 0) _path.RemoveRange(index, _path.Count - index);
            if (_path.Count == 0) _path.Add(_record.Root);
        }
        return removed;
    }

    private static TimelineNode? FindParent(TimelineNode node, string id)
    {
        foreach (TimelineNode child in node.Children.Values)
        {
            if (child.NodeId == id) return node;
            TimelineNode? found = FindParent(child, id);
            if (found != null) return found;
        }
        return null;
    }


    public TimelineSnapshot Snapshot()
    {
        TimelineAction[] path = _path.Skip(1).Select(node => node.Action!).ToArray();
        TimelineBranchSnapshot[] children = Current.Children.Values
            .OrderByDescending(node => node.LastVisitedAt)
            .Select(node => new TimelineBranchSnapshot(node.Action!, node.VisitCount, node.Outcome, node.State)).ToArray();
        HashSet<string> currentPath = _path.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        return new TimelineSnapshot(_record.EncounterKey, Current.NodeId, path, children,
            Count(_record.Root), BuildSnapshot(_record.Root, currentPath));
    }

    private TimelineNodeSnapshot BuildSnapshot(TimelineNode node, HashSet<string> currentPath) => new(
        node.NodeId, node.Action, node.VisitCount, node.Outcome, node.State,
        node.NodeId == Current.NodeId, currentPath.Contains(node.NodeId),
        node.Children.Values.OrderBy(child => child.FirstVisitedAt)
            .Select(child => BuildSnapshot(child, currentPath)).ToArray());

    private static int Count(TimelineNode node) => 1 + node.Children.Values.Sum(Count);
}
