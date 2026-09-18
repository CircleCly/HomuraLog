using Godot;
using HomuraLog.Domain;
using HomuraLog.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.Ui.Shell.Theme;
using STS2RitsuLib.Ui.Windows;

namespace HomuraLog.UI;

internal sealed partial class HomuraOverlay : CanvasLayer
{
    private RitsuFloatingWindow? _panel;
    private VBoxContainer? _content;
    private Button? _toggle;
    private Label? _status;
    private TimelineMiniGraph? _miniGraph;
    private RitsuFloatingWindow? _miniInspector;
    private Label? _details;
    private Button? _miniJump;
    private Button? _fullGraph;
    private Button? _resetMini;
    private TimelineGraphWindow? _graphWindow;
    private readonly TimelineFocusState _focus = new();
    private TimelineSession? _session;
    private TimelineSnapshot? _snapshot;
    private string? _selectedMiniNodeId;
    private bool _collapsed;
    private double _badgeRefresh;
    private double _combatWatchdogRefresh;
    private bool _hiddenForPause;
    private bool _hiddenForCombatModal;
    private Vector2 _expandedSize = new(440, 360);
    private string _lastLanguage = "";

    public override void _Ready()
    {
        Layer = 90;
        _panel = new RitsuFloatingWindow(new RitsuFloatingWindowOptions
        {
            Title = HomuraText.Title,
            InitialSize = new Vector2(440, 360),
            MinimumSize = new Vector2(360, 260),
            MaximumSize = new Vector2(850, 800),
            FitInitialSizeToContent = false,
            Movable = true,
            Resizable = true,
            Closable = false,
            StartCentered = false,
            ConstrainToViewport = true,
        }) { Position = new Vector2(24, 180) };
        _panel.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Body);
        AddChild(_panel);
        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 4);
        _panel.SetContent(_content);

        HBoxContainer header = new();
        _content.AddChild(header);
        _status = CreateRitsuLabel();
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.AddChild(_status);
        _toggle = new Button { Text = HomuraText.Hide, FocusMode = Control.FocusModeEnum.None };
        _toggle.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Button);
        _toggle.Pressed += Toggle;
        header.AddChild(_toggle);
        _fullGraph = new Button { Text = HomuraText.FullGraph, FocusMode = Control.FocusModeEnum.None };
        _fullGraph.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Button);
        _fullGraph.Pressed += ShowFullGraph;
        header.AddChild(_fullGraph);
        _resetMini = new Button { Text = HomuraText.ResetView, FocusMode = Control.FocusModeEnum.None };
        _resetMini.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Button);
        _resetMini.Pressed += ResetSharedFocusFromMini;
        header.AddChild(_resetMini);

        _miniGraph = new TimelineMiniGraph
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _miniGraph.FocusChanged += nodeId => SetSharedFocus(nodeId, FocusSource.Mini);
        _miniGraph.NodeActivated += ShowNodeDetails;
        _miniGraph.MoreBranchesActivated += nodeId =>
        {
            SetSharedFocus(nodeId, FocusSource.Mini);
            ShowFullGraphAt(nodeId);
        };
        _content.AddChild(_miniGraph);

        RefreshLocalizedChrome();
        Visible = false;
        SetProcess(true);
        SetProcessInput(true);
        WorldlineReplayController.StatusChanged += OnReplayStatus;
    }

    public override void _ExitTree() => WorldlineReplayController.StatusChanged -= OnReplayStatus;

    public void Bind(TimelineSession session)
    {
        Unbind();
        _session = session;
        _session.Changed += OnChanged;
        SetDisabled(false);
        // CombatStarting can fire before this CanvasLayer has entered the scene tree
        // (notably after a language change/relaunch). Defer the first render so the
        // Ritsu window is created and made visible reliably.
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(this) && ReferenceEquals(_session, session))
                OnChanged(session.Snapshot);
        }).CallDeferred();
    }

    public void Unbind()
    {
        bool wasBound = _session != null;
        if (_session != null) _session.Changed -= OnChanged;
        _session = null;
        _snapshot = null;
        _focus.Clear();
        _selectedMiniNodeId = null;
        DestroyMiniInspector();
        CloseGraphWindow();
        Visible = false;
        ClearBadges();
        if (wasBound) Entry.Logger.Info("Combat overlay hidden; timeline data was finalized before unbinding.");
    }

    public void SetDisabled(bool disabled)
    {
        if (_status != null) _status.Text = HomuraText.Disabled;
        Visible = !disabled;
        if (!disabled && _session != null)
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(this)) OnChanged(_session.Snapshot);
            }).CallDeferred();
    }

    private void OnChanged(TimelineSnapshot snapshot)
    {
        string? previousFocus = _focus.FocusedNodeId;
        _snapshot = snapshot;
        _focus.UpdateSnapshot(snapshot);
        string focusedNodeId = _focus.FocusedNodeId ?? snapshot.CurrentNodeId;
        bool focusChanged = !string.Equals(previousFocus, focusedNodeId, StringComparison.Ordinal);
        Visible = true;
        Render();
        if (focusChanged) _miniGraph?.SetFocusedNode(focusedNodeId);
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
            _graphWindow.UpdateSnapshot(snapshot, focusedNodeId);
        else
            _graphWindow = null;
        RefreshCardBadges();
    }

    private void Render()
    {
        if (_snapshot == null || _status == null) return;
        _status.Text = HomuraText.Nodes(_snapshot.TotalNodes);
        _miniGraph?.SetSnapshot(_snapshot);
        if (_focus.FocusedNodeId != null) _miniGraph?.SetFocusedNode(_focus.FocusedNodeId, false);
        if (_miniInspector?.Visible == true && _selectedMiniNodeId != null)
        {
            if (FindNode(_snapshot.Root, _selectedMiniNodeId) != null) ShowNodeDetails(_selectedMiniNodeId);
            else _miniInspector.Visible = false;
        }
    }

    private void ShowNodeDetails(string nodeId)
    {
        if (_snapshot == null) return;
        TimelineNodeSnapshot? node = FindNode(_snapshot.Root, nodeId);
        if (node == null) return;
        EnsureMiniInspector();
        if (_details == null) return;
        _selectedMiniNodeId = node?.NodeId;
        if (_miniInspector != null) _miniInspector.Visible = true;
        if (_miniJump != null)
            _miniJump.Disabled = node?.Action == null || node.NodeId == _snapshot.CurrentNodeId;
        if (node == null || node.State == null)
        {
            _details.Text = HomuraText.Details + ": " + HomuraText.None;
            return;
        }
        if (node.State.PlayerBlock >= 0)
        {
            _details.Text = FormatRichDetails(node.State, node.IsCurrent) + $"\n{HomuraText.Result}: {ResultText(node.Outcome)}";
            return;
        }
        string enemies = string.Join(", ", node.State.Enemies.Select(enemy =>
            $"{LocalizedModelNames.Monster(enemy.ModelId)} {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" (+{enemy.Block})" : "")));
        _details.Text = $"{HomuraText.Details}: T{node.State.Turn} · {HomuraText.Hp} {node.State.PlayerHp}/{node.State.PlayerMaxHp} · " +
            $"{HomuraText.Energy} {node.State.Energy}\n{HomuraText.EnemyHp}: {enemies}\n{HomuraText.Result}: {ResultText(node.Outcome)}";
    }

    private void EnsureMiniInspector()
    {
        if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector)) return;
        RitsuFloatingWindow inspector = new(new RitsuFloatingWindowOptions
        {
            Title = HomuraText.Details,
            InitialSize = new Vector2(390, 225),
            MinimumSize = new Vector2(330, 170),
            MaximumSize = new Vector2(650, 600),
            FitInitialSizeToContent = false,
            Movable = true,
            Resizable = true,
            Closable = true,
            StartCentered = false,
            ConstrainToViewport = true,
        });
        inspector.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Body);
        VBoxContainer content = new();
        content.AddThemeConstantOverride("separation", 6);
        _details = CreateRitsuLabel();
        _details.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _details.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _details.CustomMinimumSize = new Vector2(300, 105);
        content.AddChild(_details);
        _miniJump = new Button
        {
            Text = HomuraText.JumpHere,
            Disabled = true,
            FocusMode = Control.FocusModeEnum.None,
        };
        _miniJump.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Button);
        _miniJump.Pressed += RequestMiniWorldlineJump;
        content.AddChild(_miniJump);
        inspector.SetContent(content);
        inspector.Position = _panel == null
            ? new Vector2(470, 180)
            : _panel.Position + new Vector2(_panel.Size.X + 10, 0);
        inspector.Closed += (_, _) =>
        {
            if (ReferenceEquals(_miniInspector, inspector))
            {
                _miniInspector = null;
                _details = null;
                _miniJump = null;
                _selectedMiniNodeId = null;
            }
            inspector.QueueFree();
        };
        _miniInspector = inspector;
        AddChild(inspector);
    }

    private void DestroyMiniInspector()
    {
        if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector))
            _miniInspector.QueueFree();
        _miniInspector = null;
        _details = null;
        _miniJump = null;
        _selectedMiniNodeId = null;
    }

    private static TimelineNodeSnapshot? FindNode(TimelineNodeSnapshot node, string nodeId)
    {
        if (node.NodeId == nodeId) return node;
        foreach (TimelineNodeSnapshot child in node.Children)
        {
            TimelineNodeSnapshot? found = FindNode(child, nodeId);
            if (found != null) return found;
        }
        return null;
    }

    private static Label CreateRitsuLabel()
    {
        Label label = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.Body);
        label.AddThemeColorOverride("font_color", RitsuShellTheme.Current.Text.LabelPrimary);
        return label;
    }

    internal static string ActionText(TimelineAction action) => action.Kind switch
    {
        TimelineActionKind.PlayCard => $"T{action.Turn} {LocalizedModelNames.Card(action.SourceId)}" +
            (action.HandPosition.HasValue ? $" ({HomuraText.HandPosition(action.HandPosition.Value)})" : "") +
            (action.TargetId.HasValue ? $" → #{action.TargetId}" : ""),
        TimelineActionKind.UsePotion => $"T{action.Turn} 🧪 {LocalizedModelNames.Potion(action.SourceId)}" + (action.TargetId.HasValue ? $" → #{action.TargetId}" : ""),
        TimelineActionKind.EndTurn => $"T{action.Turn} ⏭ {HomuraText.EndTurn}",
        TimelineActionKind.CardChoice => action.Skipped ? $"↳ {HomuraText.Skip}" : $"↳ {HomuraText.Choice} [{string.Join(", ", (action.Choices ?? []).Select(LocalizedModelNames.Choice))}]",
        _ => action.SourceId,
    };

    private static string FormatRichDetails(CombatStateSummary state, bool useLiveIntent = false)
    {
        string enemies = string.Join("\n", state.Enemies.Select(enemy =>
            $"  {LocalizedModelNames.Monster(enemy.ModelId)} {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" (+{enemy.Block})" : "")
            + (string.IsNullOrWhiteSpace(IntentForDisplay(enemy, useLiveIntent)) ? "" : $" · {HomuraText.Intent}: {IntentForDisplay(enemy, useLiveIntent)}")));
        return $"{HomuraText.Details}: T{state.Turn} · {HomuraText.Hp} {state.PlayerHp}/{state.PlayerMaxHp} · " +
            $"{HomuraText.Block} {state.PlayerBlock} · {HomuraText.Energy} {state.Energy}\n{HomuraText.EnemyHp}:\n{enemies}";
    }

    private static string IntentForDisplay(CreatureState recorded, bool useLiveIntent)
    {
        if (!useLiveIntent || !recorded.CombatId.HasValue)
            return LocalizedIntent.Format(recorded);
        try
        {
            var combat = CombatManager.Instance.DebugOnlyGetState();
            var enemy = combat?.Enemies.FirstOrDefault(candidate => candidate.CombatId == recorded.CombatId.Value);
            if (enemy?.Monster == null) return LocalizedIntent.Format(recorded);
            return string.Join(" + ", enemy.Monster.NextMove.Intents.Select(intent =>
                intent.GetIntentLabel(combat!.Allies, enemy).GetFormattedText().Trim()));
        }
        catch { return LocalizedIntent.Format(recorded); }
    }

    private void ShowFullGraph()
        => ShowFullGraphAt(null);

    private void ShowFullGraphAt(string? focusNodeId)
    {
        if (_snapshot == null) return;
        if (focusNodeId != null) SetSharedFocus(focusNodeId, FocusSource.External);
        string target = _focus.FocusedNodeId ?? _snapshot.CurrentNodeId;
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
        {
            _graphWindow.UpdateSnapshot(_snapshot, target);
            _graphWindow.Visible = true;
            TimelineGraphWindow graph = _graphWindow;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(graph) && graph.IsAvailable) graph.SetFocusedNode(target);
            }).CallDeferred();
            return;
        }
        _graphWindow = null;
        TimelineGraphWindow created = new(_snapshot, target) { Name = "HomuraTimelineGraph" };
        created.FocusChanged += nodeId => SetSharedFocus(nodeId, FocusSource.Large);
        created.ResetViewRequested += ResetSharedFocusFromLarge;
        created.JumpRequested += nodeId => SubmitWorldlineJump(nodeId, "large-view");
        created.DeleteRequested += DeleteWorldlineNode;
        created.Closed += () =>
        {
            if (ReferenceEquals(_graphWindow, created)) _graphWindow = null;
        };
        _graphWindow = created;
        AddChild(created);
    }

    private void SetSharedFocus(string nodeId, FocusSource source)
    {
        if (_snapshot == null || !_focus.TrySet(_snapshot, nodeId)) return;
        if (source != FocusSource.Mini) _miniGraph?.SetFocusedNode(nodeId);
        if (source != FocusSource.Large && _graphWindow != null
            && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
            _graphWindow.SetFocusedNode(nodeId);
        if (_miniInspector?.Visible == true) ShowNodeDetails(nodeId);
    }

    private void ResetSharedFocusFromMini()
    {
        if (_snapshot == null) return;
        string nodeId = _focus.Reset(_snapshot);
        _miniGraph?.ResetView(nodeId);
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
            _graphWindow.SetFocusedNode(nodeId);
    }

    private void ResetSharedFocusFromLarge()
    {
        if (_snapshot == null) return;
        string nodeId = _focus.Reset(_snapshot);
        _miniGraph?.SetFocusedNode(nodeId);
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
            _graphWindow.ResetView(nodeId);
    }

    private void DeleteWorldlineNode(string nodeId)
    {
        if (_session == null || _snapshot == null || nodeId == _snapshot.Root.NodeId) return;
        _session.DeleteNode(nodeId);
    }

    internal void RunSmokeCheck()
    {
        TaskHelper.RunSafely(RunVisualSmokeCheckAsync());
    }

    private async Task RunVisualSmokeCheckAsync()
    {
        await WaitForUiFrames(4);
        if (_snapshot == null) return;
        string outputDirectory = Path.Combine(OS.GetUserDataDir(), "HomuraLog", "visual-smoke",
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputDirectory);

        await WaitForUiFrames(3);
        await CaptureViewport(outputDirectory, "01-mini-current.png");

        TimelineNodeSnapshot? alternate = FlattenNodes(_snapshot.Root)
            .FirstOrDefault(node => !node.IsCurrent && node.Action != null && !node.IsOnCurrentPath)
            ?? FlattenNodes(_snapshot.Root).FirstOrDefault(node => !node.IsCurrent && node.Action != null);
        if (alternate != null)
        {
            SetSharedFocus(alternate.NodeId, FocusSource.External);
            await WaitForUiFrames(2);
            await CaptureViewport(outputDirectory, "02-mini-alternate-focus.png");
        }

        ShowFullGraph();
        await WaitForUiFrames(4);
        _graphWindow?.RunLargeWindowSmokeCheck();
        await CaptureViewport(outputDirectory, "03-large-shared-focus.png");

        ResetSharedFocusFromLarge();
        await WaitForUiFrames(2);
        await CaptureViewport(outputDirectory, "04-large-reset-current.png");

        CloseGraphWindow();
        await WaitForUiFrames(2);
        await CaptureViewport(outputDirectory, "05-mini-reset-current.png");
        Entry.Logger.Info($"Visual smoke check captured screenshots directory={outputDirectory}.");
    }

    private async Task WaitForUiFrames(int count)
    {
        for (int index = 0; index < count; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task CaptureViewport(string directory, string fileName)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = Path.Combine(directory, fileName);
        Error result = GetViewport().GetTexture().GetImage().SavePng(path);
        if (result == Error.Ok) Entry.Logger.Info($"Visual smoke screenshot saved path={path}.");
        else Entry.Logger.Warn($"Visual smoke screenshot failed path={path} error={result}.");
    }

    private static IEnumerable<TimelineNodeSnapshot> FlattenNodes(TimelineNodeSnapshot node)
    {
        yield return node;
        foreach (TimelineNodeSnapshot child in node.Children)
        foreach (TimelineNodeSnapshot descendant in FlattenNodes(child))
            yield return descendant;
    }

    private void CloseGraphWindow()
    {
        TimelineGraphWindow? graph = _graphWindow;
        _graphWindow = null;
        if (graph != null && GodotObject.IsInstanceValid(graph)) graph.QueueFree();
    }

    private void SubmitWorldlineJump(string nodeId, string source)
    {
        TimelineSession? session = _session;
        TimelineSnapshot? snapshot = _snapshot;
        if (session == null || snapshot == null) return;
        TimelineNodeSnapshot? node = FindNode(snapshot.Root, nodeId);
        if (node?.Action == null || node.IsCurrent) return;
        string mode = session.TryGetForwardPath(nodeId, out _) ? "forward" : "reload";
        Entry.Logger.Info($"Worldline jump submitted source={source} mode={mode} node={nodeId}.");
        CloseGraphWindow();
        DestroyMiniInspector();
        WorldlineReplayController.Request(session, nodeId);
    }

    private void RequestMiniWorldlineJump()
    {
        string? nodeId = _selectedMiniNodeId;
        if (nodeId == null) return;
        SubmitWorldlineJump(nodeId, "mini-inspector");
    }

    private void OnReplayStatus(string message)
    {
        if (_status != null) _status.Text = message;
    }

    private static string OutcomeText(TimelineOutcome outcome) => outcome switch
    {
        TimelineOutcome.Victory => " · ★",
        TimelineOutcome.Defeat => " · ☠",
        TimelineOutcome.Aborted => " · ↺",
        _ => "",
    };

    private static string ResultText(TimelineOutcome outcome) => outcome switch
    {
        TimelineOutcome.Victory => $"★ {HomuraText.OutcomeVictory}",
        TimelineOutcome.Defeat => $"☠ {HomuraText.OutcomeDefeat}",
        TimelineOutcome.Aborted => $"↺ {HomuraText.OutcomeAborted}",
        _ => "—",
    };

    private enum FocusSource { Mini, Large, External }

    public override void _Input(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.H, CtrlPressed: true }) Toggle();
    }

    public override void _Process(double delta)
    {
        if (!string.Equals(_lastLanguage, HomuraText.Language, StringComparison.Ordinal))
            RefreshLocalizedChrome();
        _combatWatchdogRefresh += delta;
        if (_combatWatchdogRefresh >= 0.25)
        {
            _combatWatchdogRefresh = 0;
            Entry.RecoverMissedCombatStart();
        }
        if (_session != null && (!CombatManager.Instance.IsInProgress
            || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _session.Combat)))
        {
            Visible = false;
            ClearBadges();
            return;
        }
        bool paused = RunManager.Instance.IsPaused;
        // Only display over the actual combat room. The game's active-screen context
        // covers pile/deck views, the map, inspect screens, capstones, and mod screens.
        bool combatModal = IsCombatModalOpen();
        if (paused != _hiddenForPause || combatModal != _hiddenForCombatModal)
        {
            _hiddenForPause = paused;
            _hiddenForCombatModal = combatModal;
            bool show = !paused && !combatModal;
            if (_panel != null) _panel.Visible = show;
            if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector))
                _miniInspector.Visible = show;
            if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
                _graphWindow.Visible = show;
            if (show && _selectedMiniNodeId != null && _miniInspector != null)
                ShowNodeDetails(_selectedMiniNodeId);
        }
        if (!Visible || _snapshot == null) return;
        _badgeRefresh += delta;
        if (_badgeRefresh < 0.2) return;
        _badgeRefresh = 0;
        RefreshCardBadges();
    }

    private void RefreshLocalizedChrome()
    {
        _lastLanguage = HomuraText.Language;
        if (_toggle != null) _toggle.Text = _collapsed ? HomuraText.Show : HomuraText.Hide;
        if (_fullGraph != null) _fullGraph.Text = HomuraText.CompactFullGraph;
        if (_resetMini != null) _resetMini.Text = HomuraText.CompactResetView;
        if (_miniJump != null) _miniJump.Text = HomuraText.JumpHere;
        _miniGraph?.RefreshLocalization();
        try
        {
            var field = typeof(RitsuFloatingWindow).GetField("_title",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(_panel) is Label title) title.Text = HomuraText.Title;
            if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector)
                && field?.GetValue(_miniInspector) is Label inspectorTitle)
                inspectorTitle.Text = HomuraText.Details;
        }
        catch (Exception error) { Entry.Logger.Warn($"Could not refresh localized window title: {error.Message}"); }
        CloseGraphWindow();
        if (_snapshot != null) Render();
    }

    private void Toggle()
    {
        _collapsed = !_collapsed;
        ApplyCompactState();
    }

    private void ApplyCompactState()
    {
        if (_content == null || _toggle == null || _panel == null || _status == null
            || _miniGraph == null || _fullGraph == null || _resetMini == null) return;
        _status.Visible = !_collapsed;
        _miniGraph.Visible = !_collapsed;
        if (_collapsed)
        {
            if (_panel.Size.X > 300 || _panel.Size.Y > 100) _expandedSize = _panel.Size;
            _panel.CustomMinimumSize = new Vector2(300, 54);
            _panel.Size = new Vector2(300, 54);
        }
        else
        {
            _panel.CustomMinimumSize = new Vector2(360, 260);
            _panel.Size = new Vector2(Math.Max(360, _expandedSize.X), Math.Max(260, _expandedSize.Y));
        }
        // The large graph remains reachable even in compact mode; otherwise a
        // compact HUD can trap the user in a view with no way to inspect the tree.
        _fullGraph.Visible = true;
        _resetMini.Visible = true;
        _toggle.Text = _collapsed ? HomuraText.Show : HomuraText.Hide;
    }

    private bool IsCombatModalOpen()
    {
        if (NPlayerHand.Instance == null || !NPlayerHand.Instance.IsVisibleInTree()) return true;
        return ActiveScreenContext.Instance.GetCurrentScreen() is not NCombatRoom;
    }

    private void RefreshCardBadges()
    {
        TimelineSnapshot? snapshot = _snapshot;
        TimelineSession? session = _session;
        NPlayerHand? hand = NPlayerHand.Instance;
        if (snapshot == null || session == null || hand == null || !CombatManager.Instance.IsInProgress
            || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), session.Combat)) return;
        Dictionary<string, int> explored = snapshot.NextBranches
            .Where(branch => branch.Action.Kind == TimelineActionKind.PlayCard)
            .GroupBy(branch => $"{branch.Action.SourceId}|{branch.Action.InstanceId}")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var holder in hand.ActiveHolders)
        {
            var cardNode = holder.CardNode;
            var model = cardNode?.Model;
            if (cardNode == null || model == null) continue;
            string instance;
            try { instance = NetCombatCard.FromModel(model).CombatCardIndex.ToString(); }
            catch (InvalidOperationException) { SetBadge(cardNode, "?", new Color(0.9f, 0.65f, 0.2f)); continue; }
            string key = $"{model.Id.Entry}|{instance}";
            if (explored.TryGetValue(key, out int count))
                SetBadge(cardNode, count > 1 ? $"✓{count}" : "✓", new Color(0.35f, 0.85f, 0.55f));
            else SetBadge(cardNode, "•", new Color(0.55f, 0.7f, 1f));
        }
        RefreshEndTurnBadge();
        RefreshPotionBadges();
    }

    private void RefreshEndTurnBadge()
    {
        if (_snapshot == null) return;
        bool explored = _snapshot.NextBranches.Any(branch => branch.Action.Kind == TimelineActionKind.EndTurn);
        foreach (Node node in GetTree().Root.FindChildren("*", nameof(NEndTurnButton), true, false))
            if (node is NEndTurnButton button)
                SetBadge(button, explored ? "✓" : "•", explored ? new Color(0.35f, 0.85f, 0.55f) : new Color(0.55f, 0.7f, 1f));
    }

    private void RefreshPotionBadges()
    {
        if (_snapshot == null || _session == null) return;
        var player = _session.Combat.Players.Count == 1 ? _session.Combat.Players[0] : null;
        if (player == null) return;
        foreach (Node node in GetTree().Root.FindChildren("*", nameof(NPotionHolder), true, false))
        {
            if (node is not NPotionHolder { Potion.Model: { } potion } holder) continue;
            int slot = player.GetPotionSlotIndex(potion);
            int count = _snapshot.NextBranches.Count(branch => branch.Action.Kind == TimelineActionKind.UsePotion
                && branch.Action.Slot == slot && branch.Action.SourceId == potion.Id.Entry);
            SetBadge(holder, count > 1 ? $"✓{count}" : count == 1 ? "✓" : "•",
                count > 0 ? new Color(0.35f, 0.85f, 0.55f) : new Color(0.55f, 0.7f, 1f));
        }
    }

    private static void SetBadge(Control card, string text, Color color)
    {
        Label? badge = card.GetNodeOrNull<Label>("HomuraLogBadge");
        if (badge == null)
        {
            badge = new Label
            {
                Name = "HomuraLogBadge", Position = new Vector2(14, 14),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            badge.AddThemeFontOverride("font", RitsuShellTheme.Current.Font.BodyBold);
            badge.AddThemeFontSizeOverride("font_size", 24);
            badge.AddThemeConstantOverride("outline_size", 5);
            card.AddChild(badge);
        }
        badge.Text = text;
        badge.Modulate = color;
        badge.Visible = true;
    }

    private static void ClearBadges()
    {
        if (NPlayerHand.Instance == null) return;
        foreach (var holder in NPlayerHand.Instance.ActiveHolders)
            holder.CardNode?.GetNodeOrNull<Label>("HomuraLogBadge")?.QueueFree();
        foreach (Node node in NPlayerHand.Instance.GetTree().Root.FindChildren("*", nameof(NEndTurnButton), true, false))
            (node as Control)?.GetNodeOrNull<Label>("HomuraLogBadge")?.QueueFree();
        foreach (Node node in NPlayerHand.Instance.GetTree().Root.FindChildren("*", nameof(NPotionHolder), true, false))
            (node as Control)?.GetNodeOrNull<Label>("HomuraLogBadge")?.QueueFree();
    }
}
