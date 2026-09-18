namespace HomuraLog.Domain;

/// <summary>Owns the shared, transient focus used by all timeline views.</summary>
public sealed class TimelineFocusState
{
    private string? _lastCurrentNodeId;

    public string? FocusedNodeId { get; private set; }

    public void UpdateSnapshot(TimelineSnapshot snapshot)
    {
        bool currentChanged = !string.Equals(_lastCurrentNodeId, snapshot.CurrentNodeId,
            StringComparison.Ordinal);
        _lastCurrentNodeId = snapshot.CurrentNodeId;
        if (currentChanged || FocusedNodeId == null || Find(snapshot.Root, FocusedNodeId) == null)
            FocusedNodeId = snapshot.CurrentNodeId;
    }

    public bool TrySet(TimelineSnapshot snapshot, string nodeId)
    {
        if (Find(snapshot.Root, nodeId) == null) return false;
        FocusedNodeId = nodeId;
        return true;
    }

    public string Reset(TimelineSnapshot snapshot)
    {
        _lastCurrentNodeId = snapshot.CurrentNodeId;
        return FocusedNodeId = snapshot.CurrentNodeId;
    }

    public void Clear()
    {
        FocusedNodeId = null;
        _lastCurrentNodeId = null;
    }

    private static TimelineNodeSnapshot? Find(TimelineNodeSnapshot node, string nodeId)
    {
        if (node.NodeId == nodeId) return node;
        foreach (TimelineNodeSnapshot child in node.Children)
        {
            TimelineNodeSnapshot? found = Find(child, nodeId);
            if (found != null) return found;
        }
        return null;
    }
}
