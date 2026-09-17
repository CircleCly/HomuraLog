using Godot;
using HomuraLog.Domain;
using HomuraLog.Runtime;
using STS2RitsuLib.Ui.Shell.Theme;
using STS2RitsuLib.Ui.Windows;

namespace HomuraLog.UI;

internal sealed partial class TimelineGraphWindow : CanvasLayer
{
    private const int MaxRenderedNodes = 600;
    private readonly RitsuFloatingWindow _window;
    private readonly GraphEdit _graph;
    private readonly ArrowOverlay _arrows;
    private readonly Label _hint;
    private readonly Label _details;
    private readonly Button _fullscreenButton;
    private readonly Button _jumpButton;
    private readonly Button _deleteButton;
    private readonly Dictionary<string, TimelineNodeSnapshot> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _segmentByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimelineNodeSnapshot> _segmentTail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _nodeRows = new(StringComparer.Ordinal);
    private TimelineSnapshot _snapshot;
    private string? _selectedNodeId;
    private bool _jumpArmed;
    private bool _fullscreen;
    private Vector2 _savedPosition;
    private Vector2 _savedSize;
    private bool _panning;

    public TimelineGraphWindow(TimelineSnapshot snapshot)
    {
        _snapshot = snapshot;
        Layer = 110;
        _window = new RitsuFloatingWindow(new RitsuFloatingWindowOptions
        {
            Title = HomuraText.FullGraph,
            InitialSize = new Vector2(1500, 900),
            MinimumSize = new Vector2(800, 500),
            FitInitialSizeToContent = false,
            Movable = true,
            Resizable = true,
            Closable = true,
            StartCentered = true,
        });
        _window.Closed += (_, _) => QueueFree();

        VBoxContainer content = new();
        ApplyBodyFont(content);
        HBoxContainer toolbar = new();
        Label help = new()
        {
            Text = HomuraText.GraphHelp,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Modulate = new Color("9fb4c8"),
        };
        ApplyBodyFont(help);
        toolbar.AddChild(help);
        Button center = new() { Text = HomuraText.ResetView, FocusMode = Control.FocusModeEnum.None };
        ApplyButtonFont(center);
        center.Pressed += CenterCurrent;
        toolbar.AddChild(center);
        _fullscreenButton = new Button { Text = HomuraText.Fullscreen, FocusMode = Control.FocusModeEnum.None };
        ApplyButtonFont(_fullscreenButton);
        _fullscreenButton.Pressed += ToggleFullscreen;
        toolbar.AddChild(_fullscreenButton);
        content.AddChild(toolbar);

        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        ApplyBodyFont(_hint);
        content.AddChild(_hint);
        HSplitContainer split = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _graph = new GraphEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(650, 410),
            ShowGrid = true,
            MinimapEnabled = true,
            ShowZoomLabel = true,
            RightDisconnects = false,
        };
        _graph.NodeSelected += OnNodeSelected;
        _graph.GuiInput += OnGraphGuiInput;
        _arrows = new ArrowOverlay(_graph)
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LayoutMode = 1,
        };
        _arrows.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _graph.AddChild(_arrows);
        split.AddChild(_graph);

        VBoxContainer inspector = new() { CustomMinimumSize = new Vector2(330, 0) };
        Label inspectorTitle = new() { Text = HomuraText.Details };
        inspectorTitle.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.BodyBold);
        inspectorTitle.AddThemeFontSizeOverride("font_size", 20);
        inspector.AddChild(inspectorTitle);
        HSeparator divider = new();
        inspector.AddChild(divider);
        _details = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        ApplyBodyFont(_details);
        inspector.AddChild(_details);
        _jumpButton = new Button { Text = HomuraText.JumpHere, Disabled = true };
        ApplyButtonFont(_jumpButton);
        _jumpButton.Pressed += RequestJump;
        inspector.AddChild(_jumpButton);
        _deleteButton = new Button { Text = HomuraText.DeleteNode, Disabled = true };
        ApplyButtonFont(_deleteButton);
        _deleteButton.Pressed += RequestDelete;
        inspector.AddChild(_deleteButton);
        split.AddChild(inspector);
        content.AddChild(split);
        _window.SetContent(content);
        AddChild(_window);
    }

    public event Action<string>? JumpRequested;
    public event Action<string>? DeleteRequested;

    public override void _Ready() => Render();

    internal void RunFullscreenSmokeCheck()
    {
        ToggleFullscreen();
        ToggleFullscreen();
        Entry.Logger.Info("UI smoke check toggled true fullscreen successfully.");
    }

    public void UpdateSnapshot(TimelineSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (IsInsideTree()) Render();
    }

    private void Render()
    {
        foreach (Node child in _graph.GetChildren())
            if (child is GraphNode)
            {
                _graph.RemoveChild(child);
                child.QueueFree();
            }
        _nodes.Clear();
        _segmentByNode.Clear();
        _segmentTail.Clear();
        _nodeRows.Clear();
        _jumpArmed = false;
        _jumpButton.Text = HomuraText.JumpHere;

        List<TimelineNodeSnapshot> nodes = SelectNodes(_snapshot.Root, MaxRenderedNodes);
        HashSet<string> included = nodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        GraphSegment rootSegment = BuildSegments(_snapshot.Root, included);
        List<GraphSegment> segments = Flatten(rootSegment).ToList();
        if (System.Environment.GetCommandLineArgs().Contains("--homuralog-smoke", StringComparer.OrdinalIgnoreCase))
            Entry.Logger.Info($"UI graph projection compacted {nodes.Count} timeline nodes into {segments.Count} visual segments.");
        Dictionary<string, Vector2> positions = Layout(rootSegment);
        foreach (GraphSegment segment in segments)
        {
            foreach (TimelineNodeSnapshot node in segment.Nodes)
            {
                _nodes[node.NodeId] = node;
                _segmentByNode[node.NodeId] = segment.Id;
            }
            _segmentTail[segment.Id] = segment.Nodes[^1];
            GraphNode view = CreateNode(segment);
            view.PositionOffset = positions[segment.Id];
            _graph.AddChild(view);
        }
        _arrows.SetEdges(segments.SelectMany(parent => parent.Children
            .Select(child => (parent.Id, child.Id, child.Nodes.Any(node => node.IsOnCurrentPath)))));
        _graph.MoveChild(_arrows, _graph.GetChildCount() - 1);
        _hint.Text = nodes.Count < _snapshot.TotalNodes
            ? HomuraText.LargeTreeHint(nodes.Count, _snapshot.TotalNodes)
            : HomuraText.Nodes(_snapshot.TotalNodes);

        string selection = _selectedNodeId != null && included.Contains(_selectedNodeId)
            ? _selectedNodeId : _snapshot.CurrentNodeId;
        SelectNode(selection);
        Callable.From(CenterCurrent).CallDeferred();
    }

    private void CenterCurrent()
    {
        if (!_segmentByNode.TryGetValue(_snapshot.CurrentNodeId, out string? segmentId)
            || !_graph.HasNode(segmentId)) return;
        GraphNode node = _graph.GetNode<GraphNode>(segmentId);
        _graph.ScrollOffset = node.PositionOffset - _graph.Size / 2 + node.Size / 2;
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _savedPosition = _window.Position;
            _savedSize = _window.Size;
            _window.InteractionLocked = true;
            _window.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _window.Position = Vector2.Zero;
            _window.Size = GetViewport().GetVisibleRect().Size;
        }
        else
        {
            _window.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            _window.Position = _savedPosition;
            _window.Size = _savedSize;
            _window.InteractionLocked = false;
        }
        _fullscreenButton.Text = _fullscreen ? HomuraText.ExitFullscreen : HomuraText.Fullscreen;
    }

    private void OnNodeSelected(Node node)
    {
        if (_segmentTail.TryGetValue(node.Name.ToString(), out TimelineNodeSnapshot? tail))
            SelectNode(tail.NodeId);
    }

    private void SelectNode(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out TimelineNodeSnapshot? node)) return;
        _selectedNodeId = nodeId;
        RefreshSelectionHighlight();
        _jumpArmed = false;
        _jumpButton.Text = HomuraText.JumpHere;
        _jumpButton.Disabled = node.Action == null;
        _deleteButton.Disabled = node.Action == null;
        string action = node.Action == null ? HomuraText.Root : HomuraOverlay.ActionText(node.Action);
        string state = node.State == null ? HomuraText.None :
            $"T{node.State.Turn}\n{HomuraText.Hp} {node.State.PlayerHp}/{node.State.PlayerMaxHp}\n" +
            $"{HomuraText.Block} {node.State.PlayerBlock}\n{HomuraText.Energy} {node.State.Energy}\n{HomuraText.EnemyHp}\n" +
            string.Join('\n', node.State.Enemies.Select(enemy =>
                $"  {enemy.ModelId}: {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" +{enemy.Block}" : "")
                + (string.IsNullOrWhiteSpace(enemy.Intent) ? "" : $" · {HomuraText.Intent}: {enemy.Intent}")));
        _details.Text = $"{action}\n\n{HomuraText.Visits(node.Visits)}\n{node.Outcome}\n\n{state}";
    }

    private void RequestJump()
    {
        if (_selectedNodeId == null || !_nodes.TryGetValue(_selectedNodeId, out TimelineNodeSnapshot? node)
            || node.Action == null) return;
        if (!_jumpArmed)
        {
            _jumpArmed = true;
            _jumpButton.Text = HomuraText.ConfirmJump;
            return;
        }
        _jumpButton.Disabled = true;
        JumpRequested?.Invoke(_selectedNodeId);
    }

    private void RequestDelete()
    {
        if (!string.IsNullOrEmpty(_selectedNodeId)) DeleteRequested?.Invoke(_selectedNodeId);
    }

    private GraphNode CreateNode(GraphSegment segment)
    {
        TimelineNodeSnapshot tail = segment.Nodes[^1];
        bool current = segment.Nodes.Any(node => node.IsCurrent);
        bool currentPath = segment.Nodes.Any(node => node.IsOnCurrentPath);
        Color color = current ? new Color("f4b860")
            : tail.Outcome == TimelineOutcome.Victory ? new Color("62d69b")
            : tail.Outcome == TimelineOutcome.Defeat ? new Color("e96b70")
            : currentPath ? new Color("70b7ed") : new Color("9a8fb5");
        string title = segment.Nodes[0].Action == null ? HomuraText.Root
            : segment.Nodes.Count == 1 ? HomuraText.Decision
            : $"{segment.Nodes.Count} {HomuraText.Decision}";
        GraphNode graphNode = new()
        {
            Name = segment.Id,
            Title = (current ? "● " : "") + title,
            CustomMinimumSize = new Vector2(340, Math.Max(104, 54 + segment.Nodes.Count * 38)),
        };
        graphNode.AddThemeFontOverride("title_font", RitsuShellTheme.Current.Font.BodyBold);
        VBoxContainer body = new();
        for (int index = 0; index < segment.Nodes.Count; index++)
        {
            TimelineNodeSnapshot node = segment.Nodes[index];
            string text = node.Action == null ? $"◎ {HomuraText.Root}" : HomuraOverlay.ActionText(node.Action);
            Button row = new()
            {
                Text = $"{(node.IsCurrent ? "▶" : "│")} {text}",
                Alignment = HorizontalAlignment.Left,
                Flat = true,
                FocusMode = Control.FocusModeEnum.None,
                TooltipText = text,
            };
            ApplyButtonFont(row);
            if (node.IsCurrent) row.AddThemeColorOverride("font_color", new Color("f4b860"));
            else if (node.IsOnCurrentPath) row.AddThemeColorOverride("font_color", new Color("70b7ed"));
            string nodeId = node.NodeId;
            row.Pressed += () => SelectNode(nodeId);
            _nodeRows[nodeId] = row;
            body.AddChild(row);
        }
        body.AddChild(new HSeparator());
        Label summary = new()
        {
            Text = $"{HomuraText.Visits(tail.Visits)}  ·  {ResultBadge(tail.Outcome)}" + StateSummary(tail),
            Modulate = new Color("aebdca"),
        };
        ApplyBodyFont(summary);
        body.AddChild(summary);
        graphNode.AddChild(body);
        graphNode.AddThemeColorOverride("title_color", color);
        return graphNode;
    }

    private void RefreshSelectionHighlight()
    {
        foreach ((string id, Button row) in _nodeRows)
        {
            row.RemoveThemeStyleboxOverride("normal");
            row.RemoveThemeStyleboxOverride("hover");
            row.RemoveThemeColorOverride("font_hover_color");
            if (_nodes.TryGetValue(id, out TimelineNodeSnapshot? node))
                row.AddThemeColorOverride("font_color", node.IsCurrent ? new Color("f4b860")
                    : node.IsOnCurrentPath ? new Color("70b7ed") : RitsuShellTheme.Current.Text.LabelPrimary);
            bool selectedRow = id == _selectedNodeId;
            row.Flat = !selectedRow;
            if (!selectedRow) continue;
            StyleBoxFlat selected = new()
            {
                BgColor = new Color("755522"),
                BorderColor = new Color("ffd166"),
                CornerRadiusTopLeft = 5,
                CornerRadiusTopRight = 5,
                CornerRadiusBottomLeft = 5,
                CornerRadiusBottomRight = 5,
                BorderWidthLeft = 3,
                BorderWidthTop = 3,
                BorderWidthRight = 3,
                BorderWidthBottom = 3,
                ContentMarginLeft = 8,
                ContentMarginRight = 8,
            };
            row.AddThemeStyleboxOverride("normal", selected);
            row.AddThemeStyleboxOverride("hover", selected);
            row.AddThemeColorOverride("font_color", Colors.White);
            row.AddThemeColorOverride("font_hover_color", Colors.White);
        }
    }

    private void OnGraphGuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            _panning = button.Pressed;
            _graph.AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion && _panning)
        {
            _graph.ScrollOffset -= motion.Relative / Math.Max(_graph.Zoom, 0.01f);
            _graph.AcceptEvent();
        }
    }

    private static void ApplyBodyFont(Control control) =>
        control.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Body);

    private static void ApplyButtonFont(Control control) =>
        control.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Button);

    private static string StateSummary(TimelineNodeSnapshot node)
    {
        if (node.State == null) return "";
        int enemyHp = node.State.Enemies.Where(enemy => enemy.Alive).Sum(enemy => enemy.Hp);
        return $"  ·  ♥ {node.State.PlayerHp}  ⚡ {node.State.Energy}  ·  {HomuraText.EnemyHp} {enemyHp}";
    }

    private static string ResultBadge(TimelineOutcome outcome) => outcome switch
    {
        TimelineOutcome.Victory => "★ WIN",
        TimelineOutcome.Defeat => "☠ LOSS",
        TimelineOutcome.Aborted => "↺ SL",
        _ => "…",
    };

    private static List<TimelineNodeSnapshot> SelectNodes(TimelineNodeSnapshot root, int limit)
    {
        List<TimelineNodeSnapshot> result = [];
        void Visit(TimelineNodeSnapshot node)
        {
            if (result.Count >= limit) return;
            result.Add(node);
            foreach (TimelineNodeSnapshot child in node.Children
                         .OrderByDescending(c => c.IsOnCurrentPath).ThenByDescending(c => c.Visits))
                Visit(child);
        }
        Visit(root);
        return result;
    }

    private sealed record GraphSegment(string Id, IReadOnlyList<TimelineNodeSnapshot> Nodes, List<GraphSegment> Children);

    private static GraphSegment BuildSegments(TimelineNodeSnapshot start, HashSet<string> included)
    {
        List<TimelineNodeSnapshot> chain = [start];
        TimelineNodeSnapshot tail = start;
        while (true)
        {
            TimelineNodeSnapshot[] children = tail.Children.Where(child => included.Contains(child.NodeId)).ToArray();
            if (children.Length != 1) break;
            tail = children[0];
            chain.Add(tail);
        }
        List<GraphSegment> branches = tail.Children.Where(child => included.Contains(child.NodeId))
            .Select(child => BuildSegments(child, included)).ToList();
        return new GraphSegment(start.NodeId, chain, branches);
    }

    private static IEnumerable<GraphSegment> Flatten(GraphSegment root)
    {
        yield return root;
        foreach (GraphSegment child in root.Children)
        foreach (GraphSegment descendant in Flatten(child))
            yield return descendant;
    }

    private static Dictionary<string, Vector2> Layout(GraphSegment root)
    {
        Dictionary<string, Vector2> result = [];
        float column = 0;
        float Place(GraphSegment segment, float y)
        {
            float x;
            float nextY = y + Math.Max(104, 54 + segment.Nodes.Count * 38) + 105;
            if (segment.Children.Count == 0) x = column++ * 390;
            else
            {
                float first = Place(segment.Children[0], nextY), last = first;
                for (int i = 1; i < segment.Children.Count; i++) last = Place(segment.Children[i], nextY);
                x = (first + last) / 2;
            }
            result[segment.Id] = new Vector2(x, y);
            return x;
        }
        Place(root, 0);
        return result;
    }

    private sealed partial class ArrowOverlay(GraphEdit graph) : Control
    {
        private (string From, string To, bool CurrentPath)[] _edges = [];

        public void SetEdges(IEnumerable<(string From, string To, bool CurrentPath)> edges)
        {
            _edges = edges.ToArray();
            QueueRedraw();
        }

        public override void _Process(double delta) => QueueRedraw();

        public override void _Draw()
        {
            foreach ((string from, string to, bool currentPath) in _edges)
            {
                GraphNode? parent = graph.GetNodeOrNull<GraphNode>(from);
                GraphNode? child = graph.GetNodeOrNull<GraphNode>(to);
                if (parent == null || child == null) continue;
                Color color = currentPath ? new Color("70b7ed") : new Color("71859a");
                Rect2 parentRect = parent.GetGlobalRect();
                Rect2 childRect = child.GetGlobalRect();
                Transform2D toLocal = GetGlobalTransformWithCanvas().AffineInverse();
                Vector2 start = toLocal * (parentRect.Position + new Vector2(parentRect.Size.X / 2, parentRect.Size.Y));
                Vector2 end = toLocal * (childRect.Position + new Vector2(childRect.Size.X / 2, 0));
                float middle = (start.Y + end.Y) / 2;
                Vector2[] line = [start, new Vector2(start.X, middle), new Vector2(end.X, middle), end];
                DrawPolyline(line, color, currentPath ? 4f : 2.5f, true);
                Vector2 tip = end - new Vector2(0, 5);
                Vector2[] triangle = [tip, tip + new Vector2(-8, -15), tip + new Vector2(8, -15)];
                DrawColoredPolygon(triangle, color);
            }
        }
    }
}
