using Godot;
using HomuraLog.Domain;
using STS2RitsuLib.Ui.Shell.Theme;

namespace HomuraLog.UI;

/// <summary>A compact, independently laid out viewport over the current timeline neighborhood.</summary>
internal sealed partial class TimelineMiniGraph : Control
{
    private const float MinReadableZoom = 0.9f;
    private readonly List<HitArea> _hitAreas = [];
    private TimelineSnapshot? _snapshot;
    private CompactTimelineLayoutResult? _layout;
    private string? _selectedNodeId;
    private string? _lastCurrentNodeId;
    private string? _hoveredItemId;
    private Vector2 _hoverPosition;
    private Vector2 _pan;
    private float _zoom = 0.9f;
    private bool _panning;
    private bool _dragged;
    private float _dragDistance;

    public TimelineMiniGraph()
    {
        CustomMinimumSize = new Vector2(340, 245);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        TooltipText = HomuraText.GraphHelp;
        Resized += QueueRedraw;
    }

    public event Action<string>? NodeActivated;
    public event Action<string>? MoreBranchesActivated;

    public void RefreshLocalization()
    {
        TooltipText = HomuraText.GraphHelp;
        _layout = null;
        QueueRedraw();
    }

    public void ResetToCurrent()
    {
        _zoom = CalculateReadableZoom();
        CenterCurrent();
    }

    public void SetSnapshot(TimelineSnapshot? snapshot)
    {
        bool currentChanged = snapshot?.CurrentNodeId != _lastCurrentNodeId;
        _snapshot = snapshot;
        _lastCurrentNodeId = snapshot?.CurrentNodeId;
        _layout = null;
        if (snapshot == null)
        {
            _selectedNodeId = null;
            return;
        }
        if (currentChanged || _selectedNodeId == null || Find(snapshot.Root, _selectedNodeId) == null)
            _selectedNodeId = snapshot.CurrentNodeId;
        if (currentChanged) Callable.From(ResetToCurrent).CallDeferred();
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton wheel
            && wheel.Pressed && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float oldZoom = _zoom;
            _zoom = Math.Clamp(_zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.12f : 0.89f), 0.55f, 1.6f);
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
                _dragDistance = 0;
            }
            else
            {
                _panning = false;
                if (!_dragged) ActivateAt(button.Position);
            }
            AcceptEvent();
            return;
        }
        if (inputEvent is InputEventMouseMotion motion)
        {
            _hoverPosition = motion.Position;
            if (_panning)
            {
                _dragDistance += motion.Relative.Length();
                if (_dragDistance >= 6f) _dragged = true;
                _pan += motion.Relative;
            }
            else
            {
                Vector2 world = (motion.Position - _pan) / _zoom;
                _hoveredItemId = _hitAreas.LastOrDefault(area => area.Rect.HasPoint(world))?.ItemId;
            }
            QueueRedraw();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        _hitAreas.Clear();
        RitsuShellTheme theme = RitsuShellTheme.Current;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(theme.Surface.Inset.Bg, 0.98f), true);
        if (_snapshot == null) return;

        _layout = BuildLayout();
        Dictionary<string, CompactTimelineItem> byId = _layout.Items.ToDictionary(item => item.Id);
        DrawSetTransform(_pan, 0, Vector2.One * _zoom);
        foreach (CompactTimelineEdge edge in _layout.Edges)
            DrawEdge(byId[edge.FromItemId], byId[edge.ToItemId], edge.IsCurrentPath);
        foreach (CompactTimelineItem item in _layout.Items) DrawItem(item, theme);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        DrawHoverTooltip(theme);
    }

    private CompactTimelineLayoutResult BuildLayout()
    {
        MiniTimelineSegment root = MiniTimelineProjector.Create(_snapshot!);
        Font font = RitsuShellTheme.Current.Font.Body;
        return CompactTimelineLayout.Create(root,
            row => MeasureWidth(RowText(row), font, 16),
            segment => MeasureWidth(HomuraText.MoreBranches(segment.HiddenBranchCount), font, 14));
    }

    private static float MeasureWidth(string text, Font font, int fontSize) =>
        Math.Clamp(font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X + 24f, 150f, 210f);

    private void DrawItem(CompactTimelineItem item, RitsuShellTheme theme)
    {
        Rect2 rect = new(item.X, item.Y, item.Width, item.Height);
        if (item.IsMoreBranches)
        {
            DrawCard(rect, new Color(theme.Surface.Entry.Bg, 0.96f), new Color("70b7ed"), 2);
            string text = HomuraText.MoreBranches(item.Segment.HiddenBranchCount);
            DrawString(theme.Font.BodyBold, rect.Position + new Vector2(9, 21), ClipToWidth(text, theme.Font.BodyBold, rect.Size.X - 18, 14),
                HorizontalAlignment.Center, rect.Size.X - 18, 14, new Color("70b7ed"));
            _hitAreas.Add(new HitArea(rect, item.Id, null, item.Segment.MoreBranchesNodeId, text));
            return;
        }

        MiniTimelineRow row = item.Row!;
        if (row.IsOmission)
        {
            string omitted = HomuraText.OmittedActions(row.OmittedCount);
            DrawString(theme.Font.Body, rect.Position + new Vector2(8, 21), ClipToWidth(omitted, theme.Font.Body, rect.Size.X - 16, 14),
                HorizontalAlignment.Center, rect.Size.X - 16, 14, theme.Text.LabelSecondary);
            return;
        }

        TimelineNodeSnapshot node = row.Node!;
        bool selected = node.NodeId == _selectedNodeId;
        Color accent = node.IsCurrent ? new Color("f4b860")
            : node.Outcome == TimelineOutcome.Victory ? new Color("62d69b")
            : node.Outcome == TimelineOutcome.Defeat ? new Color("e96b70")
            : node.IsOnCurrentPath ? new Color("70b7ed") : new Color("9a8fb5");
        Color background = selected ? new Color("755522") : new Color(theme.Surface.Entry.Bg, 0.98f);
        DrawCard(rect, background, selected ? new Color("ffd166") : accent, node.IsCurrent || selected ? 3 : 1.5f);
        string fullText = RowText(row);
        string prefix = node.IsCurrent ? "▶ " : "";
        string shown = prefix + ClipToWidth(fullText, selected ? theme.Font.BodyBold : theme.Font.Body,
            rect.Size.X - 18 - MeasureText(prefix, theme.Font.BodyBold, 16), 16);
        DrawString(selected ? theme.Font.BodyBold : theme.Font.Body, rect.Position + new Vector2(9, 22), shown,
            HorizontalAlignment.Left, rect.Size.X - 18, 16, selected ? Colors.White : accent);
        _hitAreas.Add(new HitArea(rect, item.Id, node.NodeId, null, fullText));
    }

    private static float MeasureText(string text, Font font, int size) =>
        font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;

    private static string RowText(MiniTimelineRow row)
    {
        if (row.IsOmission) return HomuraText.OmittedActions(row.OmittedCount);
        return row.Node!.Action == null ? HomuraText.Root : HomuraOverlay.ActionText(row.Node.Action);
    }

    private void DrawEdge(CompactTimelineItem from, CompactTimelineItem to, bool currentPath)
    {
        Rect2 fromRect = new(from.X, from.Y, from.Width, from.Height);
        Rect2 toRect = new(to.X, to.Y, to.Width, to.Height);
        Vector2 start;
        Vector2 end;
        Vector2[] line;
        if (Math.Abs(fromRect.GetCenter().X - toRect.GetCenter().X) < 8)
        {
            start = new Vector2(fromRect.GetCenter().X, fromRect.End.Y);
            end = new Vector2(toRect.GetCenter().X, toRect.Position.Y);
            line = [start, end];
        }
        else
        {
            bool targetLeft = toRect.GetCenter().X < fromRect.GetCenter().X;
            start = new Vector2(targetLeft ? fromRect.Position.X : fromRect.End.X, fromRect.GetCenter().Y);
            end = new Vector2(toRect.GetCenter().X, toRect.Position.Y);
            float gutterY = Math.Min(end.Y - 5, start.Y + 8);
            line = [start, new Vector2(start.X, gutterY), new Vector2(end.X, gutterY), end];
        }
        Color color = currentPath ? new Color("70b7ed") : new Color("71859a");
        DrawPolyline(line, color, currentPath ? 3f : 1.8f, true);
        DrawColoredPolygon([end, end + new Vector2(-4, -7), end + new Vector2(4, -7)], color);
    }

    private void ActivateAt(Vector2 screenPosition)
    {
        Vector2 world = (screenPosition - _pan) / _zoom;
        HitArea? area = _hitAreas.LastOrDefault(candidate => candidate.Rect.HasPoint(world));
        if (area == null) return;
        if (area.NodeId != null)
        {
            _selectedNodeId = area.NodeId;
            NodeActivated?.Invoke(area.NodeId);
        }
        else if (area.MoreBranchesNodeId != null)
            MoreBranchesActivated?.Invoke(area.MoreBranchesNodeId);
        QueueRedraw();
    }

    private void DrawHoverTooltip(RitsuShellTheme theme)
    {
        if (_hoveredItemId == null) return;
        HitArea? hit = _hitAreas.FirstOrDefault(area => area.ItemId == _hoveredItemId);
        if (hit == null || string.IsNullOrWhiteSpace(hit.FullText)) return;
        float width = Math.Min(330, Math.Max(130, MeasureText(hit.FullText, theme.Font.Body, 13) + 20));
        Vector2 position = _hoverPosition + new Vector2(12, 14);
        position.X = Math.Min(position.X, Size.X - width - 6);
        position.Y = Math.Min(position.Y, Size.Y - 35);
        Rect2 rect = new(position, new Vector2(width, 29));
        DrawCard(rect, new Color(theme.Surface.Entry.Bg, 0.99f), new Color("71859a"), 1);
        DrawString(theme.Font.Body, rect.Position + new Vector2(9, 20),
            ClipToWidth(hit.FullText, theme.Font.Body, rect.Size.X - 18, 13),
            HorizontalAlignment.Left, rect.Size.X - 18, 13, theme.Text.LabelPrimary);
    }

    private void CenterCurrent()
    {
        if (_snapshot == null || Size.X <= 0 || Size.Y <= 0) return;
        _layout = BuildLayout();
        CompactTimelineItem? current = _layout.Items.FirstOrDefault(item => item.Id == _layout.CurrentItemId);
        if (current == null) return;
        Vector2 center = new(current.X + current.Width / 2, current.Y + current.Height / 2);
        _pan = Size / 2 - center * _zoom;
        QueueRedraw();
    }

    private float CalculateReadableZoom()
    {
        if (_snapshot == null || Size.X <= 0) return 0.9f;
        _layout = BuildLayout();
        float fitWidth = (Size.X - 16) / Math.Max(1, _layout.Width);
        return Math.Clamp(fitWidth, MinReadableZoom, 1f);
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
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = width,
            BorderWidthTop = width,
            BorderWidthRight = width,
            BorderWidthBottom = width,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        }, rect);
    }

    private static string ClipToWidth(string text, Font font, float width, int fontSize)
    {
        if (MeasureText(text, font, fontSize) <= width) return text;
        const string ellipsis = "…";
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (MeasureText(text[..middle] + ellipsis, font, fontSize) <= width) low = middle;
            else high = middle - 1;
        }
        return text[..low] + ellipsis;
    }

    private sealed record HitArea(Rect2 Rect, string ItemId, string? NodeId, string? MoreBranchesNodeId, string FullText);
}
