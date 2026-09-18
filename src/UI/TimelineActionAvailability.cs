using HomuraLog.Domain;

namespace HomuraLog.UI;

internal sealed record TimelineActionAvailability(
    bool JumpEnabled,
    string JumpReason,
    bool DeleteEnabled,
    string DeleteReason)
{
    public static TimelineActionAvailability Evaluate(
        TimelineSnapshot? snapshot,
        TimelineNodeSnapshot? node,
        bool replayBusy)
    {
        if (snapshot == null || node == null)
            return new(false, HomuraText.NodeUnavailable, false, HomuraText.NodeUnavailable);
        if (replayBusy)
            return new(false, HomuraText.ReplayBusy, false, HomuraText.ReplayBusy);
        if (node.Action == null)
            return new(false, HomuraText.JumpRootUnavailable, false, HomuraText.DeleteRootUnavailable);

        bool current = string.Equals(node.NodeId, snapshot.CurrentNodeId, StringComparison.Ordinal);
        return new(!current, current ? HomuraText.JumpCurrentUnavailable : HomuraText.JumpAvailable,
            true, HomuraText.DeleteAvailable);
    }
}
