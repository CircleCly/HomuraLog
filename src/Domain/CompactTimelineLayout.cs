namespace HomuraLog.Domain;

public sealed record CompactTimelineItem(
    string Id,
    MiniTimelineRow? Row,
    MiniTimelineSegment Segment,
    bool IsMoreBranches,
    float X,
    float Y,
    float Width,
    float Height);

public sealed record CompactTimelineEdge(string FromItemId, string ToItemId, bool IsCurrentPath);

public sealed record CompactTimelineLayoutResult(
    IReadOnlyList<CompactTimelineItem> Items,
    IReadOnlyList<CompactTimelineEdge> Edges,
    float Width,
    float Height,
    string? CurrentItemId);

/// <summary>
/// Packs the mini projection into a center spine and two side lanes. The layout is
/// deliberately UI-framework agnostic so its collision and topology rules can be tested.
/// </summary>
public static class CompactTimelineLayout
{
    public const float ItemHeight = 31f;
    public const float ItemGap = 3f;
    private const float BranchGap = 8f;
    private const float LaneGap = 10f;
    private const float SideIndent = 8f;
    private const float Margin = 6f;

    public static CompactTimelineLayoutResult Create(
        MiniTimelineSegment root,
        Func<MiniTimelineRow, float> rowWidth,
        Func<MiniTimelineSegment, float> moreWidth,
        Func<MiniTimelineRow, float>? rowHeight = null)
    {
        rowHeight ??= _ => ItemHeight;
        Dictionary<string, SegmentMetrics> metrics = [];
        Measure(root);
        float centerWidth = Flatten(root)
            .Where(ContainsFocus)
            .Select(segment => metrics[segment.Id].Width)
            .DefaultIfEmpty(180f)
            .Max();

        List<CompactTimelineItem> items = [];
        List<(MiniTimelineSegment Parent, MiniTimelineSegment Child)> segmentEdges = [];
        float leftCursor = 0;
        float rightCursor = 0;

        PlaceSpine(root, 0);
        AddEdges(root);

        float minX = items.Min(item => item.X);
        float minY = items.Min(item => item.Y);
        float maxX = items.Max(item => item.X + item.Width);
        float maxY = items.Max(item => item.Y + item.Height);
        float offsetX = Margin - minX;
        float offsetY = Margin - minY;
        CompactTimelineItem[] normalized = items.Select(item => item with
        {
            X = item.X + offsetX,
            Y = item.Y + offsetY,
        }).ToArray();
        Dictionary<string, CompactTimelineItem> byId = normalized.ToDictionary(item => item.Id);

        List<CompactTimelineEdge> edges = [];
        foreach (MiniTimelineSegment segment in Flatten(root))
        {
            List<CompactTimelineItem> segmentItems = normalized
                .Where(item => ReferenceEquals(item.Segment, segment) && !item.IsMoreBranches)
                .OrderBy(item => item.Y).ToList();
            for (int index = 1; index < segmentItems.Count; index++)
                edges.Add(new CompactTimelineEdge(segmentItems[index - 1].Id, segmentItems[index].Id,
                    IsCurrentPath(segmentItems[index])));
        }
        foreach ((MiniTimelineSegment parent, MiniTimelineSegment child) in segmentEdges)
        {
            CompactTimelineItem? from = normalized.LastOrDefault(item =>
                ReferenceEquals(item.Segment, parent) && !item.IsMoreBranches);
            CompactTimelineItem? to = normalized.FirstOrDefault(item =>
                ReferenceEquals(item.Segment, child) && !item.IsMoreBranches);
            if (from != null && to != null)
                edges.Add(new CompactTimelineEdge(from.Id, to.Id, ContainsFocus(child)));
        }

        string? currentId = normalized.FirstOrDefault(item => item.Row?.IsFocused == true)?.Id;
        return new CompactTimelineLayoutResult(normalized, edges,
            maxX - minX + Margin * 2, maxY - minY + Margin * 2, currentId);

        SegmentMetrics Measure(MiniTimelineSegment segment)
        {
            float width = segment.Rows.Select(rowWidth).DefaultIfEmpty(150f).Max();
            if (segment.HiddenBranchCount > 0) width = Math.Max(width, moreWidth(segment));
            int count = segment.Rows.Count + (segment.HiddenBranchCount > 0 ? 1 : 0);
            float[] heights = segment.Rows.Select(rowHeight).ToArray();
            float ownHeight = heights.Sum()
                + (segment.HiddenBranchCount > 0 ? ItemHeight : 0)
                + Math.Max(0, count - 1) * ItemGap;
            float sideHeight = ownHeight;
            if (segment.Children.Count > 0)
                sideHeight += BranchGap + segment.Children.Sum(child => Measure(child).SideHeight)
                    + Math.Max(0, segment.Children.Count - 1) * BranchGap;
            return metrics[segment.Id] = new SegmentMetrics(width, ownHeight, sideHeight, heights);
        }

        void PlaceSpine(MiniTimelineSegment segment, float y)
        {
            SegmentMetrics size = metrics[segment.Id];
            float x = (centerWidth - size.Width) / 2;
            PlaceItems(segment, x, y);
            float childY = y + size.OwnHeight + BranchGap;
            MiniTimelineSegment? spineChild = segment.Children.FirstOrDefault(ContainsFocus);
            int sideIndex = 0;
            foreach (MiniTimelineSegment child in segment.Children.Where(child => !ReferenceEquals(child, spineChild)))
            {
                bool left = sideIndex++ % 2 == 0;
                float requestedY = childY;
                if (left)
                {
                    float branchY = Math.Max(requestedY, leftCursor);
                    PlaceSide(child, -LaneGap, branchY, -1);
                    leftCursor = branchY + metrics[child.Id].SideHeight + BranchGap;
                }
                else
                {
                    float branchY = Math.Max(requestedY, rightCursor);
                    PlaceSide(child, centerWidth + LaneGap, branchY, 1);
                    rightCursor = branchY + metrics[child.Id].SideHeight + BranchGap;
                }
            }
            if (spineChild != null) PlaceSpine(spineChild, childY);
        }

        void PlaceSide(MiniTimelineSegment segment, float anchorX, float y, int direction)
        {
            SegmentMetrics size = metrics[segment.Id];
            float x = direction < 0 ? anchorX - size.Width : anchorX;
            PlaceItems(segment, x, y);
            float nextY = y + size.OwnHeight + BranchGap;
            foreach (MiniTimelineSegment child in segment.Children)
            {
                float childAnchor = direction < 0 ? anchorX - SideIndent : anchorX + SideIndent;
                PlaceSide(child, childAnchor, nextY, direction);
                nextY += metrics[child.Id].SideHeight + BranchGap;
            }
        }

        void PlaceItems(MiniTimelineSegment segment, float x, float y)
        {
            float cursorY = y;
            for (int index = 0; index < segment.Rows.Count; index++)
            {
                MiniTimelineRow row = segment.Rows[index];
                float width = rowWidth(row);
                float height = metrics[segment.Id].RowHeights[index];
                items.Add(new CompactTimelineItem($"{segment.Id}:row:{index}", row, segment, false,
                    x + (metrics[segment.Id].Width - width) / 2,
                    cursorY, width, height));
                cursorY += height + ItemGap;
            }
            if (segment.HiddenBranchCount > 0)
            {
                float width = moreWidth(segment);
                items.Add(new CompactTimelineItem($"{segment.Id}:more", null, segment, true,
                    x + (metrics[segment.Id].Width - width) / 2,
                    cursorY, width, ItemHeight));
            }
        }

        void AddEdges(MiniTimelineSegment segment)
        {
            foreach (MiniTimelineSegment child in segment.Children)
            {
                segmentEdges.Add((segment, child));
                AddEdges(child);
            }
        }

        bool IsCurrentPath(CompactTimelineItem item) => item.Row?.Node?.IsOnCurrentPath == true;
    }

    private static bool ContainsFocus(MiniTimelineSegment segment) =>
        segment.Rows.Any(row => row.IsFocused)
        || segment.Children.Any(ContainsFocus);

    private static IEnumerable<MiniTimelineSegment> Flatten(MiniTimelineSegment root)
    {
        yield return root;
        foreach (MiniTimelineSegment child in root.Children)
        foreach (MiniTimelineSegment descendant in Flatten(child)) yield return descendant;
    }

    private sealed record SegmentMetrics(float Width, float OwnHeight, float SideHeight,
        IReadOnlyList<float> RowHeights);
}
