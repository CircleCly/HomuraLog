using Godot;
using HomuraLog.Domain;
using STS2RitsuLib.Ui.Shell.Theme;

namespace HomuraLog.UI;

/// <summary>A compact, independently laid out viewport over the current timeline neighborhood.</summary>
internal sealed partial class TimelineMiniGraph : Control
{
    private const float MinReadableZoom = 0.72f;
    private const float FirstStepWidth = 160f;
    private const float RegularMaxWidth = 190f;
    private readonly List<HitArea> _hitAreas = [];
    private HashSet<string> _immediateNextNodeIds = new(StringComparer.Ordinal);
    private HashSet<string> _firstStepSegmentNodeIds = new(StringComparer.Ordinal);
    private TimelineSnapshot? _snapshot;
    private CompactTimelineLayoutResult? _layout;
    private string? _selectedNodeId;
    private string? _preferredNextNodeId;
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
        if (currentChanged || _preferredNextNodeId != null
            && Find(snapshot.Root, _preferredNextNodeId) == null)
            _preferredNextNodeId = null;
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
        DrawBranchNavigator(theme);
        DrawHoverTooltip(theme);
    }

    private CompactTimelineLayoutResult BuildLayout()
    {
        MiniTimelineSegment root = MiniTimelineProjector.Create(_snapshot!, _preferredNextNodeId);
        Font font = RitsuShellTheme.Current.Font.Body;
        Font bold = RitsuShellTheme.Current.Font.BodyBold;
        TimelineNodeSnapshot? current = Find(_snapshot!.Root, _snapshot.CurrentNodeId);
        _immediateNextNodeIds = current?.Children.Select(child => child.NodeId)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        _firstStepSegmentNodeIds = Flatten(root)
            .Where(segment => segment.Rows.Any(row => row.Node != null
                && _immediateNextNodeIds.Contains(row.Node.NodeId)))
            .SelectMany(segment => segment.Rows.Where(row => row.Node != null).Select(row => row.Node!.NodeId))
            .ToHashSet(StringComparer.Ordinal);
        return CompactTimelineLayout.Create(root,
            row => RowWidth(row, font),
            segment => MeasureWidth(HomuraText.MoreBranches(segment.HiddenBranchCount), font, 14),
            row => RowHeight(row, bold));
    }

    private float RowWidth(MiniTimelineRow row, Font font)
    {
        if (row.Node != null && _firstStepSegmentNodeIds.Contains(row.Node.NodeId)) return FirstStepWidth;
        return MeasureWidth(RowText(row), font, 16);
    }

    private float RowHeight(MiniTimelineRow row, Font font)
    {
        if (row.Node == null || !_immediateNextNodeIds.Contains(row.Node.NodeId))
            return CompactTimelineLayout.ItemHeight;
        int lines = WrapText(RowText(row), font, FirstStepWidth - 18, 16).Count;
        return Math.Max(CompactTimelineLayout.ItemHeight, 10 + lines * 19);
    }

    private static float MeasureWidth(string text, Font font, int fontSize) =>
        Math.Clamp(font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X + 24f, 150f, RegularMaxWidth);

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
        bool selected = node.NodeId == _selectedNodeId || node.NodeId == _preferredNextNodeId;
        Color accent = node.IsCurrent ? new Color("f4b860")
            : node.Outcome == TimelineOutcome.Victory ? new Color("62d69b")
            : node.Outcome == TimelineOutcome.Defeat ? new Color("e96b70")
            : node.IsOnCurrentPath ? new Color("70b7ed") : new Color("9a8fb5");
        Color background = selected ? new Color("755522") : new Color(theme.Surface.Entry.Bg, 0.98f);
        DrawCard(rect, background, selected ? new Color("ffd166") : accent, node.IsCurrent || selected ? 3 : 1.5f);
        string fullText = RowText(row);
        string prefix = node.IsCurrent ? "▶ " : "";
        Font textFont = selected || _immediateNextNodeIds.Contains(node.NodeId)
            ? theme.Font.BodyBold : theme.Font.Body;
        if (_immediateNextNodeIds.Contains(node.NodeId))
        {
            IReadOnlyList<string> lines = WrapText(fullText, textFont, rect.Size.X - 18, 16);
            for (int index = 0; index < lines.Count; index++)
                DrawString(textFont, rect.Position + new Vector2(9, 22 + index * 19),
                    (index == 0 ? prefix : "") + lines[index], HorizontalAlignment.Left,
                    rect.Size.X - 18, 16, selected ? Colors.White : accent);
        }
        else
        {
            string shown = prefix + ClipToWidth(fullText, textFont,
                rect.Size.X - 18 - MeasureText(prefix, theme.Font.BodyBold, 16), 16);
            DrawString(textFont, rect.Position + new Vector2(9, 22), shown,
                HorizontalAlignment.Left, rect.Size.X - 18, 16, selected ? Colors.White : accent);
        }
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
        BranchButton? branchButton = BranchButtons().FirstOrDefault(button => button.Rect.HasPoint(screenPosition));
        if (branchButton != null)
        {
            Navigate(branchButton.Direction);
            return;
        }
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

    private void DrawBranchNavigator(RitsuShellTheme theme)
    {
        if (_snapshot == null || _selectedNodeId == null) return;
        TimelineNodeSnapshot? selected = Find(_snapshot.Root, _selectedNodeId);
        if (selected == null) return;
        foreach (BranchButton button in BranchButtons())
        {
            bool enabled = CanNavigate(button.Direction, selected);
            Color border = enabled ? new Color("70b7ed") : new Color("53606d");
            Color text = enabled ? theme.Text.LabelPrimary : theme.Text.LabelSecondary;
            DrawCard(button.Rect, new Color(theme.Surface.Entry.Bg, enabled ? 0.98f : 0.72f), border, 1.5f);
            DrawString(theme.Font.BodyBold, button.Rect.Position + new Vector2(0, 20), button.Label,
                HorizontalAlignment.Center, button.Rect.Size.X, 16, text);
        }
        TimelineNodeSnapshot? parent = FindParent(_snapshot.Root, selected.NodeId);
        IReadOnlyList<TimelineNodeSnapshot> siblings = parent == null ? [] : OrderedChildren(parent);
        if (siblings.Count > 1)
        {
            int index = siblings.ToList().FindIndex(node => node.NodeId == selected.NodeId);
            string position = $"{index + 1}/{siblings.Count}";
            Rect2 label = new(Size.X - 103, 70, 96, 20);
            DrawString(theme.Font.BodyBold, label.Position + new Vector2(0, 16), position,
                HorizontalAlignment.Center, label.Size.X, 13, theme.Text.LabelSecondary);
        }
    }

    private IReadOnlyList<BranchButton> BranchButtons()
    {
        float x = Size.X - 103;
        return
        [
            new BranchButton(new Rect2(x + 34, 5, 28, 28), "↑", NavigationDirection.Up),
            new BranchButton(new Rect2(x, 37, 28, 28), "←", NavigationDirection.Left),
            new BranchButton(new Rect2(x + 34, 37, 28, 28), "↓", NavigationDirection.Down),
            new BranchButton(new Rect2(x + 68, 37, 28, 28), "→", NavigationDirection.Right),
        ];
    }

    private bool CanNavigate(NavigationDirection direction, TimelineNodeSnapshot selected)
    {
        if (_snapshot == null) return false;
        TimelineNodeSnapshot? parent = FindParent(_snapshot.Root, selected.NodeId);
        return direction switch
        {
            NavigationDirection.Up => parent != null,
            NavigationDirection.Down => selected.Children.Count > 0,
            NavigationDirection.Left or NavigationDirection.Right =>
                (parent != null && parent.Children.Count > 1) || selected.Children.Count > 1,
            _ => false,
        };
    }

    private void Navigate(NavigationDirection direction)
    {
        if (_snapshot == null || _selectedNodeId == null) return;
        TimelineNodeSnapshot? selected = Find(_snapshot.Root, _selectedNodeId);
        if (selected == null || !CanNavigate(direction, selected)) return;
        TimelineNodeSnapshot? target = null;
        TimelineNodeSnapshot? parent = FindParent(_snapshot.Root, selected.NodeId);
        if (direction == NavigationDirection.Up) target = parent;
        else if (direction == NavigationDirection.Down)
            target = OrderedChildren(selected).FirstOrDefault();
        else
        {
            IReadOnlyList<TimelineNodeSnapshot> siblings;
            int index;
            if (parent != null && parent.Children.Count > 1)
            {
                siblings = OrderedChildren(parent);
                index = siblings.ToList().FindIndex(node => node.NodeId == selected.NodeId);
            }
            else
            {
                siblings = OrderedChildren(selected);
                index = direction == NavigationDirection.Right ? -1 : 0;
            }
            int delta = direction == NavigationDirection.Left ? -1 : 1;
            target = siblings[(index + delta + siblings.Count) % siblings.Count];
        }
        if (target == null) return;
        _selectedNodeId = target.NodeId;
        TimelineNodeSnapshot? current = Find(_snapshot.Root, _snapshot.CurrentNodeId);
        _preferredNextNodeId = current?.Children.Any(child => child.NodeId == target.NodeId) == true
            ? target.NodeId : null;
        _layout = null;
        ResetToCurrent();
        NodeActivated?.Invoke(_selectedNodeId);
        QueueRedraw();
    }

    private static IReadOnlyList<TimelineNodeSnapshot> OrderedChildren(TimelineNodeSnapshot node) =>
        node.Children.OrderByDescending(child => child.IsOnCurrentPath)
            .ThenByDescending(child => child.LastVisitedAt).ToArray();

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
        Rect2 focus = FocusBounds(_layout);
        if (focus.Size == Vector2.Zero) return;
        Vector2 center = focus.GetCenter();
        _pan = Size / 2 - center * _zoom;
        QueueRedraw();
    }

    private float CalculateReadableZoom()
    {
        if (_snapshot == null || Size.X <= 0) return 0.9f;
        _layout = BuildLayout();
        Rect2 focus = FocusBounds(_layout);
        float fitWidth = (Size.X - 16) / Math.Max(1, focus.Size.X);
        float fitHeight = (Size.Y - 16) / Math.Max(1, focus.Size.Y);
        return Math.Clamp(Math.Min(fitWidth, fitHeight), MinReadableZoom, 1f);
    }

    private Rect2 FocusBounds(CompactTimelineLayoutResult layout)
    {
        CompactTimelineItem[] focusItems = layout.Items.Where(item =>
            item.Id == layout.CurrentItemId
            || item.Row?.Node != null && _immediateNextNodeIds.Contains(item.Row.Node.NodeId)).ToArray();
        if (focusItems.Length == 0) return default;
        float left = focusItems.Min(item => item.X), top = focusItems.Min(item => item.Y);
        float right = focusItems.Max(item => item.X + item.Width);
        float bottom = focusItems.Max(item => item.Y + item.Height);
        return new Rect2(left - 6, top - 6, right - left + 12, bottom - top + 12);
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

    private static TimelineNodeSnapshot? FindParent(TimelineNodeSnapshot node, string nodeId)
    {
        if (node.Children.Any(child => child.NodeId == nodeId)) return node;
        foreach (TimelineNodeSnapshot child in node.Children)
        {
            TimelineNodeSnapshot? found = FindParent(child, nodeId);
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

    private static IReadOnlyList<string> WrapText(string text, Font font, float width, int fontSize)
    {
        List<string> lines = [];
        string remaining = text.Trim();
        while (remaining.Length > 0)
        {
            int low = 1, high = remaining.Length, fit = 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                if (MeasureText(remaining[..middle], font, fontSize) <= width)
                {
                    fit = middle;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
            if (fit < remaining.Length)
            {
                int space = remaining.LastIndexOf(' ', fit - 1, fit);
                if (space > 0) fit = space;
            }
            lines.Add(remaining[..fit].TrimEnd());
            remaining = remaining[fit..].TrimStart();
        }
        return lines.Count == 0 ? [""] : lines;
    }

    private static IEnumerable<MiniTimelineSegment> Flatten(MiniTimelineSegment root)
    {
        yield return root;
        foreach (MiniTimelineSegment child in root.Children)
        foreach (MiniTimelineSegment descendant in Flatten(child)) yield return descendant;
    }

    private sealed record HitArea(Rect2 Rect, string ItemId, string? NodeId, string? MoreBranchesNodeId, string FullText);
    private sealed record BranchButton(Rect2 Rect, string Label, NavigationDirection Direction);
    private enum NavigationDirection { Up, Down, Left, Right }
}
