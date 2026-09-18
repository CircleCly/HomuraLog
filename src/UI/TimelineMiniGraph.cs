using Godot;
using HomuraLog.Domain;
using STS2RitsuLib.Ui.Shell.Theme;

namespace HomuraLog.UI;

/// <summary>Compact viewport over the same compressed worldline graph used by fullscreen mode.</summary>
internal sealed partial class TimelineMiniGraph : Control
{
    private const float NodeWidth = 320f;
    private const float RowHeight = 34f;
    private readonly List<(Rect2 Rect, string NodeId)> _hitAreas = [];
    private readonly List<(Rect2 Rect, string NodeId)> _moreBranchHitAreas = [];
    private TimelineSnapshot? _snapshot;
    private string? _selectedNodeId;
    private string? _lastCurrentNodeId;
    private Vector2 _pan;
    private float _zoom = 0.9f;
    private bool _panning;
    private bool _dragged;

    public TimelineMiniGraph()
    {
        CustomMinimumSize = new Vector2(560, 390);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        Resized += QueueRedraw;
    }

    public event Action<string>? NodeActivated;
    public event Action<string>? MoreBranchesActivated;

    public void ResetToCurrent() => CenterCurrent();

    public void SetSnapshot(TimelineSnapshot? snapshot)
    {
        bool currentChanged = snapshot?.CurrentNodeId != _lastCurrentNodeId;
        _snapshot = snapshot;
        _lastCurrentNodeId = snapshot?.CurrentNodeId;
        if (snapshot == null)
        {
            _selectedNodeId = null;
            return;
        }
        if (currentChanged || _selectedNodeId == null || Find(snapshot.Root, _selectedNodeId) == null)
            _selectedNodeId = snapshot.CurrentNodeId;
        if (currentChanged) Callable.From(CenterCurrent).CallDeferred();
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton wheel
            && wheel.Pressed && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float oldZoom = _zoom;
            _zoom = Math.Clamp(_zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.12f : 0.89f), 0.45f, 1.6f);
            Vector2 worldAtCursor = (wheel.Position - _pan) / oldZoom;
            _pan = wheel.Position - worldAtCursor * _zoom;
            QueueRedraw();
            AcceptEvent();
            return;
        }
        if (inputEvent is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            if (button.Pressed)
            {
                _panning = true;
                _dragged = false;
            }
            else
            {
                _panning = false;
                if (!_dragged)
                {
                    Vector2 world = (button.Position - _pan) / _zoom;
                    for (int index = _hitAreas.Count - 1; index >= 0; index--)
                    {
                        if (!_hitAreas[index].Rect.HasPoint(world)) continue;
                        _selectedNodeId = _hitAreas[index].NodeId;
                        NodeActivated?.Invoke(_selectedNodeId);
                        QueueRedraw();
                        break;
                    }
                    for (int index = _moreBranchHitAreas.Count - 1; index >= 0; index--)
                    {
                        if (!_moreBranchHitAreas[index].Rect.HasPoint(world)) continue;
                        MoreBranchesActivated?.Invoke(_moreBranchHitAreas[index].NodeId);
                        break;
                    }
                }
            }
            AcceptEvent();
        }
        else if (inputEvent is InputEventMouseMotion motion && _panning)
        {
            if (motion.Relative.LengthSquared() > 0.5f) _dragged = true;
            _pan += motion.Relative;
            QueueRedraw();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        _hitAreas.Clear();
        _moreBranchHitAreas.Clear();
        RitsuShellTheme theme = RitsuShellTheme.Current;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(theme.Surface.Inset.Bg, 0.98f), true);
        if (_snapshot == null) return;

        MiniTimelineSegment root = MiniTimelineProjector.Create(_snapshot);
        List<MiniTimelineSegment> segments = Flatten(root).ToList();
        Dictionary<string, Vector2> positions = Layout(root);
        DrawSetTransform(_pan, 0, Vector2.One * _zoom);
        foreach (MiniTimelineSegment parent in segments)
        foreach (MiniTimelineSegment child in parent.Children)
        {
            Rect2 from = SegmentRect(parent, positions[parent.Id]);
            Rect2 to = SegmentRect(child, positions[child.Id]);
            Vector2 start = new(from.GetCenter().X, from.End.Y);
            Vector2 end = new(to.GetCenter().X, to.Position.Y);
            float middle = (start.Y + end.Y) / 2;
            bool currentPath = child.Rows.Any(row => row.Node?.IsOnCurrentPath == true);
            Color line = currentPath ? new Color("70b7ed") : new Color("71859a");
            DrawPolyline([start, new Vector2(start.X, middle), new Vector2(end.X, middle), end],
                line, currentPath ? 4 : 2.5f, true);
            DrawColoredPolygon([end, end + new Vector2(-8, -14), end + new Vector2(8, -14)], line);
        }
        foreach (MiniTimelineSegment segment in segments) DrawSegment(segment, positions[segment.Id], theme);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);

        DrawString(theme.Font.Body, new Vector2(12, Size.Y - 10), HomuraText.GraphHelp,
            HorizontalAlignment.Left, Size.X - 24, 12, new Color(theme.Text.LabelSecondary, 0.85f));
    }

    private void DrawSegment(MiniTimelineSegment segment, Vector2 position, RitsuShellTheme theme)
    {
        Rect2 rect = SegmentRect(segment, position);
        TimelineNodeSnapshot? tail = segment.Rows.LastOrDefault(row => row.Node != null)?.Node;
        bool current = segment.Rows.Any(row => row.Node?.IsCurrent == true);
        bool currentPath = segment.Rows.Any(row => row.Node?.IsOnCurrentPath == true);
        Color accent = current ? new Color("f4b860")
            : tail?.Outcome == TimelineOutcome.Victory ? new Color("62d69b")
            : tail?.Outcome == TimelineOutcome.Defeat ? new Color("e96b70")
            : currentPath ? new Color("70b7ed") : new Color("9a8fb5");
        DrawCard(rect, new Color(theme.Surface.Entry.Bg, 0.98f), accent, current ? 4 : 2);

        Font body = theme.Font.Body;
        Font bold = theme.Font.BodyBold;
        TimelineNodeSnapshot? firstNode = segment.Rows.FirstOrDefault(row => row.Node != null)?.Node;
        int actualRows = segment.Rows.Count(row => row.Node != null);
        string title = firstNode?.Action == null ? HomuraText.Root
            : actualRows == 1 ? HomuraText.Decision : $"{actualRows} {HomuraText.Decision}";
        DrawString(bold, rect.Position + new Vector2(12, 23), title,
            HorizontalAlignment.Left, rect.Size.X - 24, 16, accent);
        for (int index = 0; index < segment.Rows.Count; index++)
        {
            MiniTimelineRow projectionRow = segment.Rows[index];
            Rect2 row = new(rect.Position + new Vector2(8, 31 + index * RowHeight),
                new Vector2(rect.Size.X - 16, RowHeight - 3));
            if (projectionRow.IsOmission)
            {
                DrawString(body, row.Position + new Vector2(9, 22), HomuraText.OmittedActions(projectionRow.OmittedCount),
                    HorizontalAlignment.Center, row.Size.X - 18, 13, theme.Text.LabelSecondary);
                continue;
            }
            TimelineNodeSnapshot node = projectionRow.Node!;
            bool selected = node.NodeId == _selectedNodeId;
            if (selected) DrawCard(row, new Color("755522"), new Color("ffd166"), 4);
            string text = node.Action == null ? HomuraText.Root : HomuraOverlay.ActionText(node.Action);
            DrawString(selected ? bold : body, row.Position + new Vector2(9, 22),
                (node.IsCurrent ? "▶ " : "") + Clip(text, 39), HorizontalAlignment.Left,
                row.Size.X - 18, 14, selected ? Colors.White
                    : node.IsCurrent ? new Color("f4b860")
                    : node.IsOnCurrentPath ? new Color("70b7ed") : theme.Text.LabelPrimary);
            _hitAreas.Add((row, node.NodeId));
        }
        if (segment.HiddenBranchCount > 0)
        {
            Rect2 more = new(rect.Position + new Vector2(8, 34 + segment.Rows.Count * RowHeight),
                new Vector2(rect.Size.X - 16, 28));
            DrawString(bold, more.Position + new Vector2(8, 20), HomuraText.MoreBranches(segment.HiddenBranchCount),
                HorizontalAlignment.Center, more.Size.X - 16, 13, new Color("70b7ed"));
            _moreBranchHitAreas.Add((more, segment.MoreBranchesNodeId));
        }
        else if (tail != null)
            DrawString(body, rect.End - new Vector2(rect.Size.X - 12, 10), HomuraText.Visits(tail.Visits),
                HorizontalAlignment.Left, rect.Size.X - 24, 12, theme.Text.LabelSecondary);
    }

    private void CenterCurrent()
    {
        if (_snapshot == null || Size.X <= 0 || Size.Y <= 0) return;
        MiniTimelineSegment root = MiniTimelineProjector.Create(_snapshot);
        MiniTimelineSegment? current = Flatten(root).FirstOrDefault(segment =>
            segment.Rows.Any(row => row.Node?.NodeId == _snapshot.CurrentNodeId));
        if (current == null) return;
        Dictionary<string, Vector2> positions = Layout(root);
        Rect2 rect = SegmentRect(current, positions[current.Id]);
        _pan = Size / 2 - rect.GetCenter() * _zoom;
        QueueRedraw();
    }

    private static Rect2 SegmentRect(MiniTimelineSegment segment, Vector2 position) =>
        new(position, new Vector2(NodeWidth, 56 + segment.Rows.Count * RowHeight
            + (segment.HiddenBranchCount > 0 ? 28 : 0)));

    private static IEnumerable<MiniTimelineSegment> Flatten(MiniTimelineSegment root)
    {
        yield return root;
        foreach (MiniTimelineSegment child in root.Children)
        foreach (MiniTimelineSegment descendant in Flatten(child)) yield return descendant;
    }

    private static Dictionary<string, Vector2> Layout(MiniTimelineSegment root)
    {
        Dictionary<string, Vector2> result = [];
        float column = 0;
        float Place(MiniTimelineSegment segment, float y)
        {
            float nextY = y + SegmentRect(segment, Vector2.Zero).Size.Y + 90;
            float x;
            if (segment.Children.Count == 0) x = column++ * 365;
            else
            {
                float first = Place(segment.Children[0], nextY), last = first;
                for (int index = 1; index < segment.Children.Count; index++)
                    last = Place(segment.Children[index], nextY);
                x = (first + last) / 2;
            }
            result[segment.Id] = new Vector2(x, y);
            return x;
        }
        Place(root, 0);
        return result;
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

    private void DrawCard(Rect2 rect, Color background, Color border, float borderWidth)
    {
        int width = (int)Math.Ceiling(borderWidth);
        DrawStyleBox(new StyleBoxFlat
        {
            BgColor = background, BorderColor = border,
            BorderWidthLeft = width, BorderWidthTop = width,
            BorderWidthRight = width, BorderWidthBottom = width,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
        }, rect);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

}
