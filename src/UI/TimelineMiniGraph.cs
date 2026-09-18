using Godot;
using HomuraLog.Domain;
using STS2RitsuLib.Ui.Shell.Theme;

namespace HomuraLog.UI;

/// <summary>A compact, independently laid out viewport over the current timeline neighborhood.</summary>
internal sealed partial class TimelineMiniGraph : Control
{
    private const float MinReadableZoom = 0.86f;
    private const float NavigatorHeight = 92f;
    private const float OverflowSliver = 2f;
    private const float FirstStepWidth = 160f;
    private const float RegularMaxWidth = 190f;
    private readonly List<HitArea> _hitAreas = [];
    private HashSet<string> _immediateNextNodeIds = new(StringComparer.Ordinal);
    private HashSet<string> _firstStepSegmentNodeIds = new(StringComparer.Ordinal);
    private TimelineSnapshot? _snapshot;
    private CompactTimelineLayoutResult? _layout;
    private string? _focusedNodeId;
    private int _selectedBranchIndex;
    private int _branchWindowStart;
    private MiniTimelineProjection? _projection;
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
    public event Action<string>? FocusChanged;
    public event Action<string>? MoreBranchesActivated;

    internal string? SmokeFocusedNodeId => _focusedNodeId;
    internal int SmokeSelectedBranchIndex => _selectedBranchIndex;
    internal float SmokeZoom => _zoom;
    internal Vector2 SmokePan => _pan;
    internal Rect2 SmokeContentViewport => ContentViewport;

    internal Vector2 SmokeNavigationCenter(string direction)
    {
        NavigationDirection parsed = Enum.Parse<NavigationDirection>(direction, true);
        Rect2 rect = BranchButtons().First(button => button.Direction == parsed).Rect;
        return GetGlobalTransformWithCanvas() * rect.GetCenter();
    }

    internal Vector2 SmokeCanvasPoint()
        => GetGlobalTransformWithCanvas() * new Vector2(18, Math.Max(82, Size.Y - 18));

    public void RefreshLocalization()
    {
        TooltipText = HomuraText.GraphHelp;
        _layout = null;
        QueueRedraw();
    }

    public void ResetView(string nodeId)
    {
        if (_snapshot == null || Find(_snapshot.Root, nodeId) == null) return;
        _focusedNodeId = nodeId;
        InitializeBranchSelection();
        _layout = null;
        _zoom = CalculateReadableZoom();
        CenterCurrent();
    }

    public void SetFocusedNode(string nodeId, bool center = true)
    {
        if (_snapshot == null || Find(_snapshot.Root, nodeId) == null) return;
        if (_focusedNodeId != nodeId)
        {
            _focusedNodeId = nodeId;
            InitializeBranchSelection();
            _layout = null;
        }
        if (center)
        {
            _zoom = CalculateReadableZoom();
            CenterCurrent();
        }
        QueueRedraw();
    }

    internal void SelectBranchForSmoke(int branchIndex)
    {
        if (_snapshot == null || _focusedNodeId == null) return;
        TimelineNodeSnapshot? focused = Find(_snapshot.Root, _focusedNodeId);
        if (focused == null || focused.Children.Count == 0) return;
        _selectedBranchIndex = Math.Clamp(branchIndex, 0, focused.Children.Count - 1);
        _branchWindowStart = Math.Clamp(
            _selectedBranchIndex - MiniTimelineProjector.VisibleBranchCount + 1,
            0, Math.Max(0, focused.Children.Count - MiniTimelineProjector.VisibleBranchCount));
        _layout = null;
        _zoom = CalculateReadableZoom();
        CenterCurrent();
    }

    public void SetSnapshot(TimelineSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _layout = null;
        if (snapshot == null)
        {
            _focusedNodeId = null;
            _projection = null;
            return;
        }
        if (_focusedNodeId == null || Find(snapshot.Root, _focusedNodeId) == null)
        {
            _focusedNodeId = snapshot.CurrentNodeId;
            InitializeBranchSelection();
        }
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton wheel
            && wheel.Pressed && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (!ContentViewport.HasPoint(wheel.Position)) return;
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
                if (!ContentViewport.HasPoint(button.Position)
                    && !BranchButtons().Any(candidate => candidate.Rect.HasPoint(button.Position))) return;
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
                if (ContentViewport.HasPoint(motion.Position))
                {
                    Vector2 world = ScreenToWorld(motion.Position);
                    _hoveredItemId = _hitAreas.LastOrDefault(area => area.Rect.HasPoint(world))?.ItemId;
                }
                else _hoveredItemId = null;
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
        DrawNavigationMask(theme);
        DrawOverflowCues(theme);
        DrawBranchNavigator(theme);
        DrawHoverTooltip(theme);
    }

    private CompactTimelineLayoutResult BuildLayout()
    {
        _projection = MiniTimelineProjector.Create(_snapshot!, _focusedNodeId,
            _selectedBranchIndex, _branchWindowStart);
        _focusedNodeId = _projection.FocusedNodeId;
        _selectedBranchIndex = _projection.SelectedBranchIndex;
        _branchWindowStart = _projection.BranchWindowStart;
        MiniTimelineSegment root = _projection.Root;
        Font font = RitsuShellTheme.Current.Font.Body;
        Font bold = RitsuShellTheme.Current.Font.BodyBold;
        _immediateNextNodeIds = _projection.VisibleBranchNodeIds.ToHashSet(StringComparer.Ordinal);
        _firstStepSegmentNodeIds = Flatten(root).SelectMany(segment => segment.Rows)
            .Where(row => row.Node != null && row.IsBranchFirstStep)
            .Select(row => row.Node!.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        return CompactTimelineLayout.Create(root,
            row => RowWidth(row, font),
            segment => MeasureWidth(HomuraText.MoreBranches(segment.HiddenBranchCount), font, 14),
            row => RowHeight(row, bold));
    }

    private float RowWidth(MiniTimelineRow row, Font font)
    {
        if (row.IsFocused) return RegularMaxWidth;
        if (row.Node != null && _firstStepSegmentNodeIds.Contains(row.Node.NodeId)) return FirstStepWidth;
        return MeasureWidth(RowText(row), font, 16);
    }

    private float RowHeight(MiniTimelineRow row, Font font)
    {
        if (row.Node == null || (!row.IsFocused && !_immediateNextNodeIds.Contains(row.Node.NodeId)))
            return CompactTimelineLayout.ItemHeight;
        float width = row.IsFocused ? RegularMaxWidth : FirstStepWidth;
        int lines = WrapText(RowText(row), font, width - 18, 16).Count;
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
        bool focused = row.IsFocused;
        bool selectedBranch = row.IsSelectedBranch;
        Color accent = node.IsCurrent ? new Color("f4b860")
            : node.Outcome == TimelineOutcome.Victory ? new Color("62d69b")
            : node.Outcome == TimelineOutcome.Defeat ? new Color("e96b70")
            : node.IsOnCurrentPath ? new Color("70b7ed") : new Color("9a8fb5");
        Color background = node.IsCurrent ? new Color("64461e")
            : selectedBranch ? new Color("233f58") : new Color(theme.Surface.Entry.Bg, 0.98f);
        Color border = focused ? new Color("d9fbff")
            : selectedBranch ? new Color("70d7ff") : accent;
        DrawCard(rect, background, border, focused ? 4 : selectedBranch ? 3 : node.IsCurrent ? 2.5f : 1.5f);
        if (focused)
        {
            Rect2 inner = rect.Grow(-4);
            DrawCard(inner, new Color(0, 0, 0, 0), new Color("66d9ef"), 1);
            Vector2 corner = rect.Position + new Vector2(9, 7);
            DrawColoredPolygon([corner + new Vector2(0, -5), corner + new Vector2(5, 0),
                corner + new Vector2(0, 5), corner + new Vector2(-5, 0)], new Color("d9fbff"));
        }
        string fullText = RowText(row);
        string prefix = node.IsCurrent ? "▶ " : focused ? "◇ " : "";
        Font textFont = focused || selectedBranch || _immediateNextNodeIds.Contains(node.NodeId)
            ? theme.Font.BodyBold : theme.Font.Body;
        if (focused || _immediateNextNodeIds.Contains(node.NodeId))
        {
            IReadOnlyList<string> lines = WrapText(fullText, textFont, rect.Size.X - 18, 16);
            for (int index = 0; index < lines.Count; index++)
                DrawString(textFont, rect.Position + new Vector2(9, 22 + index * 19),
                    (index == 0 ? prefix : "") + lines[index], HorizontalAlignment.Left,
                    rect.Size.X - 18, 16, focused || selectedBranch ? Colors.White : accent);
        }
        else
        {
            string shown = prefix + ClipToWidth(fullText, textFont,
                rect.Size.X - 18 - MeasureText(prefix, theme.Font.BodyBold, 16), 16);
            DrawString(textFont, rect.Position + new Vector2(9, 22), shown,
                HorizontalAlignment.Left, rect.Size.X - 18, 16, focused || selectedBranch ? Colors.White : accent);
        }
        List<string> states = [];
        if (focused) states.Add(HomuraText.FocusedNode);
        if (node.IsCurrent) states.Add(HomuraText.PlayerPosition);
        if (selectedBranch) states.Add(HomuraText.SelectedBranch);
        string tooltip = states.Count == 0 ? fullText : $"{fullText} · {string.Join(" · ", states)}";
        _hitAreas.Add(new HitArea(rect, item.Id, node.NodeId, null, tooltip));
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
        if (!ContentViewport.HasPoint(screenPosition)) return;
        Vector2 world = ScreenToWorld(screenPosition);
        HitArea? area = _hitAreas.LastOrDefault(candidate => candidate.Rect.HasPoint(world));
        if (area == null) return;
        if (area.NodeId != null)
        {
            SetFocus(area.NodeId);
            FocusChanged?.Invoke(area.NodeId);
            NodeActivated?.Invoke(area.NodeId);
        }
        else if (area.MoreBranchesNodeId != null)
            MoreBranchesActivated?.Invoke(area.MoreBranchesNodeId);
        QueueRedraw();
    }

    private void DrawBranchNavigator(RitsuShellTheme theme)
    {
        if (_snapshot == null || _focusedNodeId == null) return;
        TimelineNodeSnapshot? focused = Find(_snapshot.Root, _focusedNodeId);
        if (focused == null) return;
        foreach (BranchButton button in BranchButtons())
        {
            bool enabled = CanNavigate(button.Direction, focused);
            Color border = enabled ? new Color("70b7ed") : new Color("53606d");
            Color text = enabled ? theme.Text.LabelPrimary : theme.Text.LabelSecondary;
            DrawCard(button.Rect, new Color(theme.Surface.Entry.Bg, enabled ? 0.98f : 0.72f), border, 1.5f);
            DrawString(theme.Font.BodyBold, button.Rect.Position + new Vector2(0, 20), button.Label,
                HorizontalAlignment.Center, button.Rect.Size.X, 16, text);
        }
        if (_projection is { BranchCount: > 1 } projection)
        {
            int end = Math.Min(projection.BranchCount,
                projection.BranchWindowStart + MiniTimelineProjector.VisibleBranchCount);
            string position = HomuraText.BranchWindow(projection.SelectedBranchIndex + 1,
                projection.BranchCount, projection.BranchWindowStart + 1, end);
            Rect2 label = new(Math.Max(6, Size.X - 225), 70, Math.Min(218, Size.X - 12), 20);
            DrawString(theme.Font.BodyBold, label.Position + new Vector2(0, 16), position,
                HorizontalAlignment.Right, label.Size.X, 12, theme.Text.LabelSecondary);
        }
    }

    private Rect2 ContentViewport => new(0, NavigatorHeight, Size.X, Math.Max(0, Size.Y - NavigatorHeight));

    private Vector2 ScreenToWorld(Vector2 screen) => (screen - _pan) / _zoom;

    private void DrawNavigationMask(RitsuShellTheme theme)
    {
        DrawRect(new Rect2(0, 0, Size.X, NavigatorHeight), new Color(theme.Surface.Inset.Bg, 1f), true);
        DrawLine(new Vector2(0, NavigatorHeight - 1), new Vector2(Size.X, NavigatorHeight - 1),
            new Color("53606d"), 1f);
    }

    private void DrawOverflowCues(RitsuShellTheme theme)
    {
        if (_layout == null || ContentViewport.Size.Y <= 0) return;
        Rect2 visible = ContentViewport;
        const float thickness = 18f;
        CompactTimelineOverflow overflow = CompactTimelineLayout.Overflow(_layout.Bounds,
            _pan.X, _pan.Y, _zoom, visible.Position.X, visible.Position.Y,
            visible.End.X, visible.End.Y, OverflowSliver);
        if (overflow.Left) DrawFade(visible, FadeEdge.Left, theme.Surface.Inset.Bg, thickness);
        if (overflow.Right) DrawFade(visible, FadeEdge.Right, theme.Surface.Inset.Bg, thickness);
        if (overflow.Top) DrawFade(visible, FadeEdge.Top, theme.Surface.Inset.Bg, thickness);
        if (overflow.Bottom)
        {
            DrawFade(visible, FadeEdge.Bottom, theme.Surface.Inset.Bg, thickness);
            string hint = HomuraText.DragForMore;
            DrawString(theme.Font.BodyBold, new Vector2(8, visible.End.Y - 5), hint,
                HorizontalAlignment.Center, visible.Size.X - 16, 12, theme.Text.LabelSecondary);
        }
    }

    private void DrawFade(Rect2 viewport, FadeEdge edge, Color baseColor, float thickness)
    {
        const int bands = 6;
        float band = thickness / bands;
        for (int index = 0; index < bands; index++)
        {
            float alpha = 0.82f * (bands - index) / bands;
            Color color = new(baseColor, alpha);
            Rect2 rect = edge switch
            {
                FadeEdge.Left => new Rect2(viewport.Position.X + index * band, viewport.Position.Y,
                    band + 0.5f, viewport.Size.Y),
                FadeEdge.Right => new Rect2(viewport.End.X - (index + 1) * band, viewport.Position.Y,
                    band + 0.5f, viewport.Size.Y),
                FadeEdge.Top => new Rect2(viewport.Position.X, viewport.Position.Y + index * band,
                    viewport.Size.X, band + 0.5f),
                _ => new Rect2(viewport.Position.X, viewport.End.Y - (index + 1) * band,
                    viewport.Size.X, band + 0.5f),
            };
            DrawRect(rect, color, true);
        }
    }

    private IReadOnlyList<BranchButton> BranchButtons()
    {
        int branchCount = _snapshot != null && _focusedNodeId != null
            ? Find(_snapshot.Root, _focusedNodeId)?.Children.Count ?? 0 : 0;
        if (branchCount <= 1)
        {
            float center = Size.X / 2f - 14f;
            return
            [
                new BranchButton(new Rect2(center, 5, 28, 28), "↑", NavigationDirection.Up),
                new BranchButton(new Rect2(center, 37, 28, 28), "↓", NavigationDirection.Down),
            ];
        }
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
            NavigationDirection.Down => selected.Children.Count > 0 && _selectedBranchIndex >= 0,
            NavigationDirection.Left => _selectedBranchIndex > 0,
            NavigationDirection.Right => _selectedBranchIndex >= 0
                && _selectedBranchIndex + 1 < selected.Children.Count,
            _ => false,
        };
    }

    private void Navigate(NavigationDirection direction)
    {
        if (_snapshot == null || _focusedNodeId == null) return;
        TimelineNodeSnapshot? selected = Find(_snapshot.Root, _focusedNodeId);
        if (selected == null || !CanNavigate(direction, selected)) return;
        if (direction == NavigationDirection.Left || direction == NavigationDirection.Right)
        {
            int delta = direction == NavigationDirection.Left ? -1 : 1;
            _selectedBranchIndex += delta;
            if (_selectedBranchIndex < _branchWindowStart) _branchWindowStart = _selectedBranchIndex;
            else if (_selectedBranchIndex >= _branchWindowStart + MiniTimelineProjector.VisibleBranchCount)
                _branchWindowStart = _selectedBranchIndex - MiniTimelineProjector.VisibleBranchCount + 1;
            _layout = null;
            _zoom = CalculateReadableZoom();
            CenterCurrent();
            QueueRedraw();
            return;
        }
        TimelineNodeSnapshot? target;
        int preferredChildIndex = -1;
        if (direction == NavigationDirection.Up)
        {
            target = FindParent(_snapshot.Root, selected.NodeId);
            if (target != null) preferredChildIndex = target.Children.ToList()
                .FindIndex(child => child.NodeId == selected.NodeId);
        }
        else target = selected.Children[_selectedBranchIndex];
        if (target == null) return;
        SetFocus(target.NodeId, preferredChildIndex);
        FocusChanged?.Invoke(target.NodeId);
        NodeActivated?.Invoke(target.NodeId);
    }

    private void SetFocus(string nodeId, int preferredChildIndex = -1)
    {
        if (_snapshot == null || Find(_snapshot.Root, nodeId) == null) return;
        _focusedNodeId = nodeId;
        InitializeBranchSelection(preferredChildIndex);
        _layout = null;
        _zoom = CalculateReadableZoom();
        CenterCurrent();
        QueueRedraw();
    }

    private void InitializeBranchSelection(int preferredChildIndex = -1)
    {
        if (_snapshot == null || _focusedNodeId == null) return;
        TimelineNodeSnapshot? focus = Find(_snapshot.Root, _focusedNodeId);
        if (focus == null || focus.Children.Count == 0)
        {
            _selectedBranchIndex = -1;
            _branchWindowStart = 0;
            return;
        }
        int currentPathIndex = focus.Children.ToList().FindIndex(child => child.IsOnCurrentPath);
        _selectedBranchIndex = preferredChildIndex >= 0 ? preferredChildIndex
            : currentPathIndex >= 0 ? currentPathIndex : 0;
        _branchWindowStart = Math.Clamp(_selectedBranchIndex - MiniTimelineProjector.VisibleBranchCount + 1,
            0, Math.Max(0, focus.Children.Count - MiniTimelineProjector.VisibleBranchCount));
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
        Rect2 focus = FocusBounds(_layout);
        if (focus.Size == Vector2.Zero) return;
        Vector2 center = focus.GetCenter();
        _pan = ContentViewport.GetCenter() - center * _zoom;
        QueueRedraw();
    }

    private float CalculateReadableZoom()
    {
        if (_snapshot == null || Size.X <= 0) return 0.9f;
        _layout = BuildLayout();
        Rect2 focus = FocusBounds(_layout);
        float left = _layout.Items.Min(item => item.X);
        float right = _layout.Items.Max(item => item.X + item.Width);
        float fitWidth = (ContentViewport.Size.X - 16) / Math.Max(1, right - left + 12);
        float fitHeight = (ContentViewport.Size.Y - 16) / Math.Max(1, focus.Size.Y);
        return Math.Clamp(Math.Min(fitWidth, fitHeight), MinReadableZoom, 1f);
    }

    private Rect2 FocusBounds(CompactTimelineLayoutResult layout)
    {
        CompactTimelineItem[] focusItems = layout.Items.Where(item => item.Id == layout.CurrentItemId).ToArray();
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
    private enum FadeEdge { Left, Right, Top, Bottom }
}
