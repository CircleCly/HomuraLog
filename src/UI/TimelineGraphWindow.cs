using Godot;
using HomuraLog.Domain;
using HomuraLog.Persistence;
using HomuraLog.Runtime;
using STS2RitsuLib.Ui.Shell.Theme;
using STS2RitsuLib.Ui.Windows;

namespace HomuraLog.UI;

internal sealed partial class TimelineGraphWindow : CanvasLayer
{
    private const int MaxRenderedNodes = 600;
    private const float WindowOpacity = 0.82f;
    private readonly RitsuFloatingWindow _window;
    private readonly UiLayoutStore _layoutStore;
    private readonly GraphEdit _graph;
    private readonly ArrowOverlay _arrows;
    private readonly Label _hint;
    private readonly Label _details;
    private readonly Button _jumpButton;
    private readonly Button _deleteButton;
    private readonly Dictionary<string, TimelineNodeSnapshot> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _segmentByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimelineNodeSnapshot> _segmentTail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _nodeRows = new(StringComparer.Ordinal);
    private TimelineSnapshot _snapshot;
    private string? _selectedNodeId;
    private bool _panning;
    private bool _closedNotified;
    private Viewport? _viewport;
    private bool _applyingBounds;
    private bool _boundsInitialized;
    private bool _usingPreferredBounds;
    private bool _layoutDirty;
    private ulong _layoutSaveAt;

    public TimelineGraphWindow(TimelineSnapshot snapshot, string focusedNodeId)
    {
        _snapshot = snapshot;
        _selectedNodeId = focusedNodeId;
        _layoutStore = new UiLayoutStore(Path.Combine(OS.GetUserDataDir(), "HomuraLog", "ui-layout-v1.json"));
        Layer = 110;
        _window = new RitsuFloatingWindow(new RitsuFloatingWindowOptions
        {
            Title = HomuraText.FullGraph,
            InitialSize = new Vector2(1200, 720),
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(LargeWindowGeometry.MaximumWidth, LargeWindowGeometry.MaximumHeight),
            FitInitialSizeToContent = false,
            Movable = true,
            Resizable = true,
            Closable = true,
            StartCentered = true,
            ConstrainToViewport = true,
        });
        _window.Modulate = new Color(1f, 1f, 1f, WindowOpacity);
        _window.Closed += (_, _) =>
        {
            FlushLayout();
            NotifyClosed();
            QueueFree();
        };

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
        center.Pressed += () => ResetViewRequested?.Invoke();
        toolbar.AddChild(center);
        content.AddChild(toolbar);

        _hint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        ApplyBodyFont(_hint);
        content.AddChild(_hint);
        HSplitContainer split = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _graph = new GraphEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(430, 340),
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

        VBoxContainer inspector = new() { CustomMinimumSize = new Vector2(240, 0) };
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
    public event Action<string>? FocusChanged;
    public event Action? ResetViewRequested;
    public event Action? Closed;

    public bool IsAvailable => !_closedNotified && GodotObject.IsInstanceValid(this);

    public override void _Ready()
    {
        _viewport = GetViewport();
        _viewport.SizeChanged += ClampToViewport;
        _window.ItemRectChanged += OnWindowRectChanged;
        ApplyDefaultLargeBounds();
        SetProcess(true);
        Render();
        // RitsuFloatingWindow applies its own initial rect after entering the tree. Reapply
        // our persisted rect after that pass, then begin observing user-driven changes.
        Callable.From(() => Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            ApplyDefaultLargeBounds();
            _boundsInitialized = true;
            CenterSelected();
            if (_usingPreferredBounds)
            {
                _layoutDirty = true;
                _layoutSaveAt = Time.GetTicksMsec() + 500;
            }
        }).CallDeferred()).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (_layoutDirty && Time.GetTicksMsec() >= _layoutSaveAt) FlushLayout();
    }

    public override void _ExitTree()
    {
        FlushLayout();
        if (GodotObject.IsInstanceValid(_window))
            _window.ItemRectChanged -= OnWindowRectChanged;
        if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
            _viewport.SizeChanged -= ClampToViewport;
        NotifyClosed();
    }

    internal void RunLargeWindowSmokeCheck()
    {
        Rect2 visible = GetViewport().GetVisibleRect();
        bool inside = _window.Position.X >= LargeWindowGeometry.Margin
            && _window.Position.Y >= LargeWindowGeometry.Margin
            && _window.Position.X + _window.Size.X <= visible.Size.X - LargeWindowGeometry.Margin + 1
            && _window.Position.Y + _window.Size.Y <= visible.Size.Y - LargeWindowGeometry.Margin + 1;
        Entry.Logger.Info($"UI smoke check large graph size={_window.Size} opacity={_window.Modulate.A:0.00} inside={inside}.");
    }

    public void UpdateSnapshot(TimelineSnapshot snapshot, string focusedNodeId)
    {
        _snapshot = snapshot;
        _selectedNodeId = focusedNodeId;
        if (IsInsideTree()) Render();
    }

    public void SetFocusedNode(string nodeId, bool center = true)
    {
        if (!_nodes.ContainsKey(nodeId)) return;
        SelectNode(nodeId);
        if (center) CenterNode(nodeId);
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
        string target = selection;
        Callable.From(() => CenterNode(target)).CallDeferred();
    }

    private void CenterSelected()
        => CenterNode(_selectedNodeId ?? _snapshot.CurrentNodeId);

    public void ResetView(string nodeId)
    {
        _selectedNodeId = nodeId;
        SelectNode(nodeId);
        _graph.Zoom = 1f;
        Callable.From(() => CenterNode(nodeId)).CallDeferred();
    }

    private void CenterNode(string nodeId)
    {
        if (!_segmentByNode.TryGetValue(nodeId, out string? segmentId)
            || !_graph.HasNode(segmentId)) return;
        GraphNode node = _graph.GetNode<GraphNode>(segmentId);
        _graph.ScrollOffset = node.PositionOffset + node.Size / 2
            - _graph.Size / (2 * Math.Max(_graph.Zoom, 0.01f));
    }

    private void ApplyDefaultLargeBounds()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        SavedLargeWindowLayout? saved = _layoutStore.Load();
        LargeWindowBounds? restored = saved?.Resolve(viewport.X, viewport.Y);
        bool useSaved = restored != null
            && LargeWindowGeometry.IsReasonableSavedLayout(restored, viewport.X, viewport.Y);
        _usingPreferredBounds = !useSaved;
        LargeWindowBounds bounds = !useSaved
            ? LargeWindowGeometry.Default(viewport.X, viewport.Y)
            : LargeWindowGeometry.Clamp(viewport.X, viewport.Y,
                restored!.X, restored.Y, restored.Width, restored.Height);
        ApplyBounds(bounds);
        Entry.Logger.Info(!useSaved
            ? $"Large timeline window using preferred responsive bounds={bounds}."
            : $"Large timeline window restored saved bounds={bounds} savedViewport={saved!.ViewportWidth}x{saved.ViewportHeight}.");
    }

    private void ClampToViewport()
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        LargeWindowBounds bounds = LargeWindowGeometry.Clamp(viewport.X, viewport.Y,
            _window.Position.X, _window.Position.Y, _window.Size.X, _window.Size.Y);
        ApplyBounds(bounds);
    }

    private void ApplyBounds(LargeWindowBounds bounds)
    {
        _applyingBounds = true;
        try
        {
            _window.Position = new Vector2(bounds.X, bounds.Y);
            _window.Size = new Vector2(bounds.Width, bounds.Height);
        }
        finally { _applyingBounds = false; }
    }

    private void OnWindowRectChanged()
    {
        if (_applyingBounds || !_boundsInitialized || !IsInsideTree()) return;
        _layoutDirty = true;
        _layoutSaveAt = Time.GetTicksMsec() + 500;
    }

    private void FlushLayout()
    {
        if (!_layoutDirty || !GodotObject.IsInstanceValid(_window)) return;
        _layoutDirty = false;
        try
        {
            Vector2 viewport = GetViewport().GetVisibleRect().Size;
            LargeWindowBounds bounds = LargeWindowGeometry.Clamp(viewport.X, viewport.Y,
                _window.Position.X, _window.Position.Y, _window.Size.X, _window.Size.Y);
            _layoutStore.Save(bounds, viewport.X, viewport.Y);
            Entry.Logger.Info($"Saved large timeline window bounds={bounds} viewport={viewport}.");
        }
        catch (Exception error)
        {
            Entry.Logger.Warn($"Could not save large timeline window layout: {error.Message}");
        }
    }

    private void NotifyClosed()
    {
        if (_closedNotified) return;
        _closedNotified = true;
        Closed?.Invoke();
    }

    private void OnNodeSelected(Node node)
    {
        if (_segmentTail.TryGetValue(node.Name.ToString(), out TimelineNodeSnapshot? tail))
        {
            SelectNode(tail.NodeId);
            FocusChanged?.Invoke(tail.NodeId);
        }
    }

    private void SelectNode(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out TimelineNodeSnapshot? node)) return;
        _selectedNodeId = nodeId;
        RefreshSelectionHighlight();
        _jumpButton.Text = HomuraText.JumpHere;
        _jumpButton.Disabled = node.Action == null || node.IsCurrent;
        _deleteButton.Disabled = node.Action == null;
        string action = node.Action == null ? HomuraText.Root : HomuraOverlay.ActionText(node.Action);
        string state = node.State == null ? HomuraText.None :
            $"T{node.State.Turn}\n{HomuraText.Hp} {node.State.PlayerHp}/{node.State.PlayerMaxHp}\n" +
            $"{HomuraText.Block} {node.State.PlayerBlock}\n{HomuraText.Energy} {node.State.Energy}\n{HomuraText.EnemyHp}\n" +
            string.Join('\n', node.State.Enemies.Select(enemy =>
                $"  {LocalizedModelNames.Monster(enemy.ModelId)}: {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" +{enemy.Block}" : "")
                + (string.IsNullOrWhiteSpace(LocalizedIntent.Format(enemy)) ? "" : $" · {HomuraText.Intent}: {LocalizedIntent.Format(enemy)}")));
        _details.Text = $"{action}\n\n{HomuraText.Visits(node.Visits)}\n{OutcomeLabel(node.Outcome)}\n\n{state}";
    }

    private void RequestJump()
    {
        if (_selectedNodeId == null || !_nodes.TryGetValue(_selectedNodeId, out TimelineNodeSnapshot? node)
            || node.Action == null) return;
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
            row.Pressed += () =>
            {
                SelectNode(nodeId);
                FocusChanged?.Invoke(nodeId);
            };
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
        TimelineOutcome.Victory => $"★ {HomuraText.OutcomeVictory}",
        TimelineOutcome.Defeat => $"☠ {HomuraText.OutcomeDefeat}",
        TimelineOutcome.Aborted => $"↺ {HomuraText.OutcomeAborted}",
        _ => "…",
    };

    private static string OutcomeLabel(TimelineOutcome outcome) => outcome switch
    {
        TimelineOutcome.Victory => HomuraText.OutcomeVictory,
        TimelineOutcome.Defeat => HomuraText.OutcomeDefeat,
        TimelineOutcome.Aborted => HomuraText.OutcomeAborted,
        _ => HomuraText.OutcomeOngoing,
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
