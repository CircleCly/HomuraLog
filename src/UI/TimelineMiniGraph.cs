using Godot;
using HomuraLog.Domain;
using STS2RitsuLib.Ui.Shell.Theme;

namespace HomuraLog.UI;

/// <summary>A compact, scrollable projection of the complete encounter tree.</summary>
internal sealed partial class TimelineMiniGraph : Control
{
    private const float RowHeight = 48f;
    private readonly List<(Rect2 Rect, string NodeId)> _hitAreas = [];
    private TimelineSnapshot? _snapshot;
    private string? _selectedNodeId;
    private float _scrollY;
    private float _contentHeight;

    public TimelineMiniGraph()
    {
        CustomMinimumSize = new Vector2(560, 390);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        Resized += QueueRedraw;
    }

    public event Action<string>? NodeActivated;
    public event Action<string>? NodeJumpRequested;

    public void SetSnapshot(TimelineSnapshot? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot == null)
        {
            _selectedNodeId = null;
            _scrollY = 0;
        }
        else if (_selectedNodeId == null || Find(snapshot.Root, _selectedNodeId) == null)
        {
            _selectedNodeId = snapshot.CurrentNodeId;
            ScrollSelectionIntoView();
        }
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton button || !button.Pressed) return;
        if (button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            float direction = button.ButtonIndex == MouseButton.WheelUp ? -1 : 1;
            _scrollY = Math.Clamp(_scrollY + direction * RowHeight * 3, 0, MaxScroll());
            QueueRedraw();
            AcceptEvent();
            return;
        }
        if (button.ButtonIndex != MouseButton.Left) return;
        for (int index = _hitAreas.Count - 1; index >= 0; index--)
        {
            if (!_hitAreas[index].Rect.HasPoint(button.Position)) continue;
            string nodeId = _hitAreas[index].NodeId;
            bool confirmJump = nodeId == _selectedNodeId
                && nodeId != _snapshot?.CurrentNodeId
                && Find(_snapshot!.Root, nodeId)?.Action != null;
            _selectedNodeId = nodeId;
            NodeActivated?.Invoke(nodeId);
            QueueRedraw();
            if (confirmJump) NodeJumpRequested?.Invoke(nodeId);
            AcceptEvent();
            return;
        }
    }

    public override void _Draw()
    {
        _hitAreas.Clear();
        if (_snapshot == null) return;
        RitsuShellTheme theme = RitsuShellTheme.Current;
        Font body = theme.Font.Body;
        Font bold = theme.Font.BodyBold;
        List<(TimelineNodeSnapshot Node, int Depth)> rows = [];
        Flatten(_snapshot.Root, 0, rows);
        _contentHeight = 38 + rows.Count * RowHeight;
        _scrollY = Math.Clamp(_scrollY, 0, MaxScroll());

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(theme.Surface.Inset.Bg, 0.97f), true);
        DrawString(bold, new Vector2(14, 25), $"{HomuraText.Tree} · {HomuraText.Nodes(_snapshot.TotalNodes)}",
            HorizontalAlignment.Left, Size.X - 40, 17, theme.Text.LabelPrimary);

        for (int index = 0; index < rows.Count; index++)
        {
            (TimelineNodeSnapshot node, int depth) = rows[index];
            float y = 34 + index * RowHeight - _scrollY;
            if (y + RowHeight < 34 || y > Size.Y) continue;
            float indent = Math.Min(depth, 10) * 22f;
            Rect2 row = new(new Vector2(8 + indent, y + 3), new Vector2(Math.Max(120, Size.X - 24 - indent), RowHeight - 6));
            bool selected = node.NodeId == _selectedNodeId;
            Color accent = node.IsCurrent ? new Color("f4b860")
                : node.IsOnCurrentPath ? new Color("70b7ed")
                : node.Outcome == TimelineOutcome.Victory ? new Color("62d69b")
                : node.Outcome == TimelineOutcome.Defeat ? new Color("e96b70") : new Color("71859a");

            if (depth > 0)
            {
                float lineX = row.Position.X - 11;
                DrawLine(new Vector2(lineX, y), new Vector2(lineX, y + RowHeight / 2), accent, 2);
                DrawLine(new Vector2(lineX, y + RowHeight / 2), new Vector2(row.Position.X, y + RowHeight / 2), accent, 2);
            }
            DrawCard(row, selected ? new Color("755522") : new Color(theme.Surface.Entry.Bg, 0.98f),
                selected ? new Color("ffd166") : accent, selected ? 4 : 2);
            string action = node.Action == null ? HomuraText.Root : HomuraOverlay.ActionText(node.Action);
            string marker = node.IsCurrent ? "▶ " : "";
            DrawString(selected ? bold : body, row.Position + new Vector2(10, 20), Clip(marker + action, 54),
                HorizontalAlignment.Left, row.Size.X - 20, 15, selected ? Colors.White : theme.Text.LabelPrimary);
            string meta = selected && !node.IsCurrent
                ? $"{HomuraText.Visits(node.Visits)} · {HomuraText.ConfirmJump}"
                : HomuraText.Visits(node.Visits);
            DrawString(body, row.Position + new Vector2(10, 38), Clip(meta, 58),
                HorizontalAlignment.Left, row.Size.X - 20, 12,
                selected ? new Color("ffe5a3") : theme.Text.LabelSecondary);
            _hitAreas.Add((row, node.NodeId));
        }

        if (_contentHeight > Size.Y)
        {
            float trackHeight = Size.Y - 42;
            float thumbHeight = Math.Max(30, trackHeight * Size.Y / _contentHeight);
            float thumbY = 36 + (_scrollY / Math.Max(1, MaxScroll())) * (trackHeight - thumbHeight);
            DrawRect(new Rect2(Size.X - 7, thumbY, 4, thumbHeight), new Color("70b7ed"), true);
        }
    }

    private void ScrollSelectionIntoView()
    {
        if (_snapshot == null || _selectedNodeId == null) return;
        List<(TimelineNodeSnapshot Node, int Depth)> rows = [];
        Flatten(_snapshot.Root, 0, rows);
        int index = rows.FindIndex(row => row.Node.NodeId == _selectedNodeId);
        if (index < 0) return;
        _contentHeight = 38 + rows.Count * RowHeight;
        _scrollY = Math.Clamp(34 + index * RowHeight - Size.Y / 2, 0, MaxScroll());
    }

    private float MaxScroll() => Math.Max(0, _contentHeight - Size.Y);

    private static void Flatten(TimelineNodeSnapshot node, int depth,
        List<(TimelineNodeSnapshot Node, int Depth)> output)
    {
        output.Add((node, depth));
        foreach (TimelineNodeSnapshot child in node.Children
                     .OrderByDescending(value => value.IsOnCurrentPath).ThenByDescending(value => value.Visits))
            Flatten(child, depth + 1, output);
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
            CornerRadiusTopLeft = 7,
            CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7,
            CornerRadiusBottomRight = 7,
        }, rect);
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

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
