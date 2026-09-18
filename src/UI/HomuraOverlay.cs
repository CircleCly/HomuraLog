using Godot;
using HomuraLog.Domain;
using HomuraLog.Runtime;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
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
    private bool _miniSuppressedForLarge;
    private bool _miniVisibleBeforeLarge;
    private bool _inspectorVisibleBeforeLarge;
    private Vector2 _expandedSize = new(440, 360);
    private string _lastLanguage = "";
    private bool _smokeSuppressJump;
    private string? _smokeLastJumpNodeId;
    private readonly List<string> _visualSmokeFailures = [];

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
        WorldlineReplayController.BusyChanged += RefreshActionAvailability;
    }

    public override void _ExitTree()
    {
        WorldlineReplayController.StatusChanged -= OnReplayStatus;
        WorldlineReplayController.BusyChanged -= RefreshActionAvailability;
    }

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
        _selectedMiniNodeId = node.NodeId;
        if (_miniInspector != null) _miniInspector.Visible = true;
        if (_miniJump != null)
            ApplyButtonAvailability(_miniJump,
                TimelineActionAvailability.Evaluate(_snapshot, node, WorldlineReplayController.IsBusy));
        string action = node.Action == null ? HomuraText.Root : ActionText(node.Action);
        string context = $"{action}\n{HomuraText.Visits(node.Visits)}";
        if (node.State == null)
        {
            _details.Text = $"{context}\n{HomuraText.Details}: {HomuraText.None}";
            return;
        }
        if (node.State.PlayerBlock >= 0)
        {
            _details.Text = $"{context}\n\n{FormatRichDetails(node.State, node.IsCurrent)}" +
                $"\n{HomuraText.Result}: {ResultText(node.Outcome)}";
            return;
        }
        string enemies = string.Join(", ", node.State.Enemies.Select(enemy =>
            $"{LocalizedModelNames.Monster(enemy.ModelId)} {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" (+{enemy.Block})" : "")));
        _details.Text = $"{context}\n\n{HomuraText.Details}: T{node.State.Turn} · {HomuraText.Hp} {node.State.PlayerHp}/{node.State.PlayerMaxHp} · " +
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
        {
            string header = $"  {LocalizedModelNames.Monster(enemy.ModelId)} {enemy.Hp}/{enemy.MaxHp}"
                + (enemy.Block > 0 ? $" (+{enemy.Block})" : "");
            IReadOnlyList<string> intents = IntentsForDisplay(enemy, useLiveIntent);
            return intents.Count == 0 ? header : header + "\n"
                + string.Join('\n', intents.Select(intent => $"    {HomuraText.Intent}: {intent}"));
        }));
        return $"{HomuraText.Details}: T{state.Turn} · {HomuraText.Hp} {state.PlayerHp}/{state.PlayerMaxHp} · " +
            $"{HomuraText.Block} {state.PlayerBlock} · {HomuraText.Energy} {state.Energy}\n{HomuraText.EnemyHp}:\n{enemies}";
    }

    private static IReadOnlyList<string> IntentsForDisplay(CreatureState recorded, bool useLiveIntent)
    {
        if (!useLiveIntent || !recorded.CombatId.HasValue)
            return LocalizedIntent.FormatLines(recorded);
        try
        {
            var combat = CombatManager.Instance.DebugOnlyGetState();
            var enemy = combat?.Enemies.FirstOrDefault(candidate => candidate.CombatId == recorded.CombatId.Value);
            if (enemy?.Monster == null) return LocalizedIntent.FormatLines(recorded);
            return enemy.Monster.NextMove.Intents.Select(intent =>
                RichText.ToPlainText(intent.GetIntentLabel(combat!.Allies, enemy).GetFormattedText()).Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        }
        catch { return LocalizedIntent.FormatLines(recorded); }
    }

    private void ShowFullGraph()
        => ShowFullGraphAt(null);

    private void ShowFullGraphAt(string? focusNodeId)
    {
        if (_snapshot == null) return;
        SuppressMiniForLarge();
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
            RestoreMiniAfterLarge();
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
        _visualSmokeFailures.Clear();
        await WaitForUiFrames(4);
        if (_snapshot == null) return;
        await WaitForStableCombat();
        Vector2I requestedSize = RequestedVisualSmokeSize();
        if (requestedSize.X > 0)
        {
            Window window = GetWindow();
            window.Mode = Window.ModeEnum.Windowed;
            await WaitForUiFrames(2);
            window.Size = requestedSize;
            await WaitForUiFrames(4);
            Entry.Logger.Info($"Visual smoke requested live window size={requestedSize} actual={window.Size} viewport={GetViewport().GetVisibleRect().Size}.");
        }
        string outputDirectory = Path.Combine(OS.GetUserDataDir(), "HomuraLog", "visual-smoke",
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputDirectory);

        await WaitForUiFrames(3);
        RecordVisualSmokeResult((_miniGraph?.SmokeZoom ?? 0) >= 0.86f,
            "mini-readable-auto-zoom");
        RecordVisualSmokeResult((_miniGraph?.SmokeNavigatorRect.Size.X ?? 0) <= 112f
            && (_miniGraph?.SmokeNavigatorRect.Position.X ?? 0) > 0,
            "mini-navigation-local-safe-area");
        RecordVisualSmokeResult(_miniGraph?.SmokeNavigationButtonCount == 4,
            "mini-navigation-always-four-buttons");
        await CaptureViewport(outputDirectory, "01-mini-current.png");

        TimelineNodeSnapshot? alternate = FlattenNodes(_snapshot.Root)
            .FirstOrDefault(node => !node.IsCurrent && node.Action != null && !node.IsOnCurrentPath)
            ?? FlattenNodes(_snapshot.Root).FirstOrDefault(node => !node.IsCurrent && node.Action != null);
        if (alternate != null)
        {
            SetSharedFocus(alternate.NodeId, FocusSource.External);
            await WaitForUiFrames(2);
            await CaptureViewport(outputDirectory, "02-mini-alternate-focus.png");
            ShowNodeDetails(alternate.NodeId);
            await WaitForUiFrames(2);
            await CaptureViewport(outputDirectory, "03-mini-node-details.png");
            DestroyMiniInspector();
        }
        else RecordVisualSmokeResult(false, "alternate-node-fixture");

        TimelineNodeSnapshot? fanout = FlattenNodes(_snapshot.Root)
            .Where(node => node.Children.Count > 1)
            .OrderByDescending(node => node.Children.Count)
            .ThenByDescending(node => node.LastVisitedAt)
            .FirstOrDefault();
        if (fanout != null)
        {
            SetSharedFocus(fanout.NodeId, FocusSource.External);
            await WaitForUiFrames(2);
            await CaptureViewport(outputDirectory, "04-mini-largest-fanout.png");
            if (fanout.Children.Count > MiniTimelineProjector.VisibleBranchCount)
            {
                _miniGraph?.SelectBranchForSmoke(fanout.Children.Count - 1);
                await WaitForUiFrames(2);
                await CaptureViewport(outputDirectory, "05-mini-fanout-last-window.png");
            }
        }
        else RecordVisualSmokeResult(false, "branching-node-fixture");

        ShowFullGraph();
        await WaitForUiFrames(4);
        _graphWindow?.RunLargeWindowSmokeCheck();
        RecordVisualSmokeResult(_panel?.Visible == false, "large-hides-mini");
        RecordVisualSmokeResult(_graphWindow?.SmokeBuiltInToolbarHidden == true,
            "large-built-in-toolbar-hidden");
        bool sharedFocusOpened = _graphWindow?.SmokeSelectedNodeId == _focus.FocusedNodeId
            && _miniGraph?.SmokeFocusedNodeId == _focus.FocusedNodeId;
        RecordVisualSmokeResult(sharedFocusOpened, "shared-focus-open-large");
        await CaptureViewport(outputDirectory, "06-large-shared-focus.png");
        await RunLargePointerSmokeChecks(outputDirectory, alternate);

        ResetSharedFocusFromLarge();
        await WaitForUiFrames(2);
        bool largeResetWorked = _focus.FocusedNodeId == _snapshot.CurrentNodeId
            && _graphWindow?.SmokeSelectedNodeId == _snapshot.CurrentNodeId
            && _miniGraph?.SmokeFocusedNodeId == _snapshot.CurrentNodeId
            && Math.Abs((_graphWindow?.SmokeZoom ?? 0f) - 1f) < 0.001f;
        RecordVisualSmokeResult(largeResetWorked, "large-reset-view-state");
        await CaptureViewport(outputDirectory, "07-large-reset-current.png");

        CloseGraphWindow();
        await WaitForUiFrames(2);
        RecordVisualSmokeResult(_panel?.Visible == true, "large-close-restores-mini");
        bool miniResetWorked = _focus.FocusedNodeId == _snapshot.CurrentNodeId
            && _miniGraph?.SmokeFocusedNodeId == _snapshot.CurrentNodeId;
        RecordVisualSmokeResult(miniResetWorked, "mini-reset-view-state");
        await CaptureViewport(outputDirectory, "08-mini-reset-current.png");
        await RunMiniPointerSmokeChecks(outputDirectory, fanout);
        await CaptureNativeScreenSuppression(outputDirectory, "mega_view_draw_pile",
            "NCardPileScreen", "09-native-draw-pile.png");
        await CaptureNativeScreenSuppression(outputDirectory, "mega_view_discard_pile",
            "NCardPileScreen", "10-native-discard-pile.png", false);
        await CaptureNativeScreenSuppression(outputDirectory, "mega_view_map",
            "NMapScreen", "11-native-map.png");
        await CaptureNativeScreenSuppression(outputDirectory, "mega_pause_and_back",
            "NCapstoneSubmenuStack", "12-native-pause.png");
        if (DestructiveVisualSmokeEnabled()) await RunDestructiveSmokeChecks(outputDirectory);
        int screenshotCount = Directory.GetFiles(outputDirectory, "*.png").Length;
        RecordVisualSmokeResult(screenshotCount >= 15, $"screenshot-count-{screenshotCount}");
        Entry.Logger.Info($"Visual smoke check captured screenshots directory={outputDirectory}.");
        if (_visualSmokeFailures.Count == 0)
            Entry.Logger.Info($"Visual smoke check passed directory={outputDirectory}.");
        else
            Entry.Logger.Error($"Visual smoke check failed checks={string.Join(',', _visualSmokeFailures)} directory={outputDirectory}.");
    }

    private void RecordVisualSmokeResult(bool passed, string check)
    {
        if (!passed) _visualSmokeFailures.Add(check);
    }

    private static bool DestructiveVisualSmokeEnabled() => System.Environment.GetCommandLineArgs()
        .Contains("--homuralog-destructive-smoke", StringComparer.OrdinalIgnoreCase);

    private async Task RunDestructiveSmokeChecks(string directory)
    {
        if (_snapshot == null || _miniGraph == null) return;
        TimelineNodeSnapshot? current = FindNode(_snapshot.Root, _snapshot.CurrentNodeId);
        TimelineNodeSnapshot? forwardTarget = current?.Children.FirstOrDefault(child => child.Action != null);
        if (forwardTarget == null)
        {
            RecordVisualSmokeResult(false, "destructive-forward-fixture");
            return;
        }

        SetSharedFocus(forwardTarget.NodeId, FocusSource.External);
        ShowNodeDetails(forwardTarget.NodeId);
        await WaitForUiFrames(3);
        if (_miniJump == null || _miniJump.Disabled)
        {
            RecordVisualSmokeResult(false, "destructive-forward-button-enabled");
            return;
        }
        await ClickAt(_miniJump.GetGlobalRect().GetCenter());
        bool arrived = await WaitForCurrentNode(forwardTarget.NodeId, 900);
        RecordVisualSmokeResult(arrived, "destructive-forward-arrived");
        Entry.Logger.Info($"Visual smoke destructive forward arrived={arrived} target={forwardTarget.NodeId}.");
        if (arrived) await WaitForStableCombat();
        await CaptureViewport(directory, "17-destructive-forward-arrived.png");
        if (!arrived || _snapshot == null) return;

        int forwardSteps = 1;
        while (CurrentDiscardCount() == 0 && forwardSteps < 8 && _snapshot != null)
        {
            TimelineNodeSnapshot? cursor = FindNode(_snapshot.Root, _snapshot.CurrentNodeId);
            TimelineNodeSnapshot? next = cursor?.Children.FirstOrDefault(child => child.Action != null);
            if (next == null) break;
            SetSharedFocus(next.NodeId, FocusSource.External);
            ShowNodeDetails(next.NodeId);
            await WaitForUiFrames(3);
            if (_miniJump == null || _miniJump.Disabled) break;
            await ClickAt(_miniJump.GetGlobalRect().GetCenter());
            if (!await WaitForCurrentNode(next.NodeId, 900)) break;
            await WaitForStableCombat();
            forwardSteps++;
        }
        int discardCount = CurrentDiscardCount();
        RecordVisualSmokeResult(discardCount > 0, "destructive-discard-fixture");
        Entry.Logger.Info($"Visual smoke destructive discard fixture cards={discardCount} forwardSteps={forwardSteps}.");
        if (discardCount > 0)
            await CaptureNativeScreenSuppression(directory, "mega_view_discard_pile",
                "NCardPileScreen", "19-destructive-discard-pile.png");
        if (_snapshot == null) return;

        TimelineNodeSnapshot? deleteTarget = FlattenNodes(_snapshot.Root)
            .Where(node => node.Action != null && !node.IsOnCurrentPath)
            .OrderBy(node => node.Children.Count)
            .ThenByDescending(node => node.LastVisitedAt)
            .FirstOrDefault();
        if (deleteTarget == null)
        {
            RecordVisualSmokeResult(false, "destructive-delete-fixture");
            return;
        }
        string deleteNodeId = deleteTarget.NodeId;
        ShowFullGraphAt(deleteNodeId);
        await WaitForUiFrames(5);
        if (_graphWindow == null || !_graphWindow.PrepareNodeRowPointerTest(deleteNodeId))
        {
            RecordVisualSmokeResult(false, "destructive-delete-row");
            return;
        }
        await WaitForUiFrames(3);
        await ClickAt(_graphWindow.SmokeNodeRowCenter(deleteNodeId));
        await ClickAt(_graphWindow.SmokeDeleteButtonCenter());
        await WaitForUiFrames(5);
        bool deleted = _snapshot != null && FindNode(_snapshot.Root, deleteNodeId) == null;
        RecordVisualSmokeResult(deleted, "destructive-delete-removed");
        bool deleteFocusRecovered = deleted && _snapshot != null
            && _focus.FocusedNodeId == _snapshot.CurrentNodeId
            && _miniGraph?.SmokeFocusedNodeId == _snapshot.CurrentNodeId
            && _graphWindow?.SmokeSelectedNodeId == _snapshot.CurrentNodeId;
        RecordVisualSmokeResult(deleteFocusRecovered, "destructive-delete-focus-fallback");
        Entry.Logger.Info($"Visual smoke destructive delete removed={deleted} target={deleteNodeId}.");
        await CaptureViewport(directory, "18-destructive-delete-removed.png");
    }

    private async Task<bool> WaitForCurrentNode(string nodeId, int maximumFrames)
    {
        for (int frame = 0; frame < maximumFrames; frame++)
        {
            if (_snapshot?.CurrentNodeId == nodeId) return true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private int CurrentDiscardCount()
    {
        if (_session == null) return 0;
        try
        {
            var player = LocalContext.GetMe(_session.Combat);
            return player?.PlayerCombatState?.DiscardPile.Cards.Count ?? 0;
        }
        catch { return 0; }
    }

    private async Task RunMiniPointerSmokeChecks(string directory, TimelineNodeSnapshot? fanout)
    {
        if (_miniGraph == null || fanout == null || fanout.Children.Count < 2) return;
        SetSharedFocus(fanout.NodeId, FocusSource.External);
        await WaitForUiFrames(3);

        int branchBefore = _miniGraph.SmokeSelectedBranchIndex;
        await ClickAt(_miniGraph.SmokeNavigationCenter("Right"));
        bool rightWorked = _miniGraph.SmokeSelectedBranchIndex == branchBefore + 1;
        RecordVisualSmokeResult(rightWorked, "mini-right-pointer");
        Entry.Logger.Info($"Visual smoke pointer mini-right worked={rightWorked} " +
            $"before={branchBefore} after={_miniGraph.SmokeSelectedBranchIndex}.");
        await CaptureViewport(directory, "13-mini-pointer-right.png");

        string expectedChild = fanout.Children[_miniGraph.SmokeSelectedBranchIndex].NodeId;
        await ClickAt(_miniGraph.SmokeNavigationCenter("Down"));
        bool downWorked = string.Equals(_focus.FocusedNodeId, expectedChild, StringComparison.Ordinal);
        RecordVisualSmokeResult(downWorked, "mini-down-pointer");
        Entry.Logger.Info($"Visual smoke pointer mini-down worked={downWorked} " +
            $"expected={expectedChild} actual={_focus.FocusedNodeId}.");
        await CaptureViewport(directory, "14-mini-pointer-down.png");

        _smokeSuppressJump = true;
        _smokeLastJumpNodeId = null;
        if (_miniJump != null && !_miniJump.Disabled)
            await ClickAt(_miniJump.GetGlobalRect().GetCenter());
        bool jumpWorked = string.Equals(_smokeLastJumpNodeId, expectedChild, StringComparison.Ordinal);
        RecordVisualSmokeResult(jumpWorked, "mini-jump-pointer");
        _smokeSuppressJump = false;
        Entry.Logger.Info($"Visual smoke pointer mini-jump-request worked={jumpWorked} " +
            $"expected={expectedChild} actual={_smokeLastJumpNodeId}.");

        float zoomBefore = _miniGraph.SmokeZoom;
        Vector2 canvasPoint = _miniGraph.SmokeCanvasPoint();
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = canvasPoint,
            GlobalPosition = canvasPoint,
            ButtonIndex = MouseButton.WheelUp,
            Pressed = true
        }, true);
        await WaitForUiFrames(2);
        bool zoomWorked = _miniGraph.SmokeZoom > zoomBefore;

        Vector2 panBefore = _miniGraph.SmokePan;
        Vector2 dragEnd = canvasPoint + new Vector2(44, 30);
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = canvasPoint,
            GlobalPosition = canvasPoint,
            ButtonIndex = MouseButton.Left,
            Pressed = true
        }, true);
        await WaitForUiFrames(1);
        GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = dragEnd,
            GlobalPosition = dragEnd,
            Relative = dragEnd - canvasPoint,
            ButtonMask = MouseButtonMask.Left
        }, true);
        await WaitForUiFrames(1);
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragEnd,
            GlobalPosition = dragEnd,
            ButtonIndex = MouseButton.Left,
            Pressed = false
        }, true);
        await WaitForUiFrames(2);
        bool dragWorked = _miniGraph.SmokePan.DistanceTo(panBefore) >= 6f;
        RecordVisualSmokeResult(zoomWorked, "mini-wheel-zoom");
        RecordVisualSmokeResult(dragWorked, "mini-canvas-drag");
        Entry.Logger.Info($"Visual smoke pointer mini-canvas zoomWorked={zoomWorked} " +
            $"zoomBefore={zoomBefore:0.00} zoomAfter={_miniGraph.SmokeZoom:0.00} " +
            $"dragWorked={dragWorked} panDelta={_miniGraph.SmokePan - panBefore}.");
        await CaptureViewport(directory, "15-mini-pointer-zoom-drag.png");
        ResetSharedFocusFromMini();
        await WaitForUiFrames(2);
    }

    private async Task RunLargePointerSmokeChecks(string directory, TimelineNodeSnapshot? alternate)
    {
        if (_graphWindow == null || alternate == null || !GodotObject.IsInstanceValid(_graphWindow)) return;
        string beforeFocus = _focus.FocusedNodeId ?? "";
        if (!_graphWindow.PrepareNodeRowPointerTest(alternate.NodeId)) return;
        await WaitForUiFrames(3);
        Vector2 rowPoint = _graphWindow.SmokeNodeRowCenter(alternate.NodeId);
        await ClickAt(rowPoint);
        bool rowWorked = string.Equals(_focus.FocusedNodeId, alternate.NodeId, StringComparison.Ordinal);
        RecordVisualSmokeResult(rowWorked, "large-node-row-pointer");
        RecordVisualSmokeResult(_miniGraph?.SmokeFocusedNodeId == alternate.NodeId,
            "large-node-row-synced-to-mini");
        Entry.Logger.Info($"Visual smoke pointer large-row worked={rowWorked} before={beforeFocus} " +
            $"expected={alternate.NodeId} actual={_focus.FocusedNodeId} point={rowPoint}.");

        _graphWindow.SuppressRequestsForSmoke(true);
        await ClickAt(_graphWindow.SmokeJumpButtonCenter());
        bool jumpWorked = _graphWindow.SmokeLastRequest == $"jump:{alternate.NodeId}";
        _graphWindow.SuppressRequestsForSmoke(true);
        await ClickAt(_graphWindow.SmokeDeleteButtonCenter());
        bool deleteWorked = _graphWindow.SmokeLastRequest == $"delete:{alternate.NodeId}";
        RecordVisualSmokeResult(jumpWorked, "large-jump-pointer");
        RecordVisualSmokeResult(deleteWorked, "large-delete-pointer");
        _graphWindow.SuppressRequestsForSmoke(false);
        Entry.Logger.Info($"Visual smoke pointer large-actions jumpWorked={jumpWorked} " +
            $"deleteWorked={deleteWorked} node={alternate.NodeId}.");

        Vector2 canvasPoint = _graphWindow.SmokeCanvasPoint();
        float zoomBefore = _graphWindow.SmokeZoom;
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = canvasPoint,
            GlobalPosition = canvasPoint,
            ButtonIndex = MouseButton.WheelUp,
            Pressed = true
        }, true);
        await WaitForUiFrames(2);
        bool zoomWorked = _graphWindow.SmokeZoom > zoomBefore;

        Vector2 scrollBefore = _graphWindow.SmokeScrollOffset;
        Vector2 dragEnd = canvasPoint + new Vector2(46, 28);
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = canvasPoint,
            GlobalPosition = canvasPoint,
            ButtonIndex = MouseButton.Left,
            Pressed = true
        }, true);
        await WaitForUiFrames(1);
        GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = dragEnd,
            GlobalPosition = dragEnd,
            Relative = dragEnd - canvasPoint,
            ButtonMask = MouseButtonMask.Left
        }, true);
        await WaitForUiFrames(1);
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragEnd,
            GlobalPosition = dragEnd,
            ButtonIndex = MouseButton.Left,
            Pressed = false
        }, true);
        await WaitForUiFrames(2);
        bool dragWorked = _graphWindow.SmokeScrollOffset.DistanceTo(scrollBefore) >= 6f;
        RecordVisualSmokeResult(zoomWorked, "large-wheel-zoom");
        RecordVisualSmokeResult(dragWorked, "large-canvas-drag");
        Entry.Logger.Info($"Visual smoke pointer large-canvas zoomWorked={zoomWorked} " +
            $"zoomBefore={zoomBefore:0.00} zoomAfter={_graphWindow.SmokeZoom:0.00} " +
            $"dragWorked={dragWorked} scrollDelta={_graphWindow.SmokeScrollOffset - scrollBefore}.");
        await CaptureViewport(directory, "16-large-pointer-row-zoom-drag.png");
    }

    private async Task ClickAt(Vector2 position)
    {
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = position,
            GlobalPosition = position,
            ButtonIndex = MouseButton.Left,
            Pressed = true
        }, true);
        await WaitForUiFrames(1);
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = position,
            GlobalPosition = position,
            ButtonIndex = MouseButton.Left,
            Pressed = false
        }, true);
        await WaitForUiFrames(2);
    }

    private async Task CaptureNativeScreenSuppression(
        string directory, string action, string expectedScreen, string fileName, bool required = true)
    {
        if (!InputMap.HasAction(action))
        {
            Entry.Logger.Warn($"Visual smoke native-screen check skipped; input action is unavailable action={action}.");
            if (required) RecordVisualSmokeResult(false, $"native-action-{action}");
            return;
        }
        InputEventKey? binding = InputMap.ActionGetEvents(action).OfType<InputEventKey>().FirstOrDefault()
            ?? DefaultVisualSmokeBinding(action);
        if (binding == null) return;
        Input.ParseInputEvent(CopyKeyEvent(binding, true));
        await WaitForUiFrames(1);
        Input.ParseInputEvent(CopyKeyEvent(binding, false));
        bool reachedExpectedScreen = await WaitForScreen(expectedScreen, 180);
        // Screen context changes before the native transition animation has settled.
        await WaitForUiFrames(60);
        string screen = CurrentScreenName();
        bool paused = RunManager.Instance.IsPaused;
        Entry.Logger.Info($"Visual smoke native-screen action={action} expected={expectedScreen} " +
            $"binding={binding.AsText()} reached={reachedExpectedScreen} screen={screen} " +
            $"paused={paused} panelVisible={_panel?.Visible}.");
        if (!reachedExpectedScreen)
        {
            Entry.Logger.Warn($"Visual smoke native-screen check unavailable; the action did not open its screen " +
                $"(the pile may be empty) action={action}.");
            if (required) RecordVisualSmokeResult(false, $"native-screen-{action}");
            return;
        }
        RecordVisualSmokeResult(_panel?.Visible == false, $"native-overlay-hidden-{action}");
        await CaptureViewport(directory, fileName);

        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
        await WaitForUiFrames(1);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
        await WaitForScreen("NCombatRoom", 180);
        await WaitForUiFrames(30);
    }

    private static InputEventKey CopyKeyEvent(InputEventKey source, bool pressed) => new()
    {
        Keycode = source.Keycode,
        PhysicalKeycode = source.PhysicalKeycode,
        KeyLabel = source.KeyLabel,
        Unicode = source.Unicode,
        Location = source.Location,
        CtrlPressed = source.CtrlPressed,
        AltPressed = source.AltPressed,
        ShiftPressed = source.ShiftPressed,
        MetaPressed = source.MetaPressed,
        Pressed = pressed
    };

    private static InputEventKey? DefaultVisualSmokeBinding(string action)
    {
        Key key = action switch
        {
            "mega_view_draw_pile" => Key.A,
            "mega_view_discard_pile" => Key.S,
            "mega_view_map" => Key.M,
            "mega_pause_and_back" => Key.Escape,
            _ => Key.None
        };
        if (key == Key.None)
        {
            Entry.Logger.Warn($"Visual smoke native-screen check skipped; no fallback binding action={action}.");
            return null;
        }
        return new InputEventKey { Keycode = key, PhysicalKeycode = key };
    }

    private async Task<bool> WaitForScreen(string expectedScreen, int maximumFrames)
    {
        for (int frame = 0; frame < maximumFrames; frame++)
        {
            if (string.Equals(CurrentScreenName(), expectedScreen, StringComparison.Ordinal)) return true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return false;
    }

    private static string CurrentScreenName()
    {
        try { return ActiveScreenContext.Instance.GetCurrentScreen()?.GetType().Name ?? "null"; }
        catch { return "unavailable"; }
    }

    private static Vector2I RequestedVisualSmokeSize()
    {
        const string prefix = "--homuralog-visual-size=";
        string? value = System.Environment.GetCommandLineArgs()
            .FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value == null) return Vector2I.Zero;
        string[] parts = value[prefix.Length..].Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out int width)
            && int.TryParse(parts[1], out int height) && width >= 640 && height >= 360
            ? new Vector2I(width, height) : Vector2I.Zero;
    }

    private async Task WaitForUiFrames(int count)
    {
        for (int index = 0; index < count; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task WaitForStableCombat()
    {
        for (int frame = 0; frame < 1800; frame++)
        {
            if (_session != null && CombatManager.Instance.IsInProgress
                && ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _session.Combat))
            {
                var player = LocalContext.GetMe(_session.Combat);
                if (player?.PlayerCombatState?.Phase.ToString() == "Play"
                    && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null)
                {
                    // Combat phase changes before the opening banner and hand fan finish animating.
                    await WaitForUiFrames(60);
                    return;
                }
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Entry.Logger.Warn("Visual smoke check timed out waiting for stable player input; capturing the current frame.");
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

    private void SuppressMiniForLarge()
    {
        if (_miniSuppressedForLarge) return;
        _miniSuppressedForLarge = true;
        _miniVisibleBeforeLarge = _panel?.Visible == true;
        _inspectorVisibleBeforeLarge = _miniInspector?.Visible == true;
        if (_panel != null) _panel.Visible = false;
        if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector))
            _miniInspector.Visible = false;
    }

    private void RestoreMiniAfterLarge()
    {
        if (!_miniSuppressedForLarge) return;
        _miniSuppressedForLarge = false;
        bool canShow = _session != null && CombatManager.Instance.IsInProgress
            && !_hiddenForPause && !_hiddenForCombatModal;
        if (_panel != null) _panel.Visible = canShow && _miniVisibleBeforeLarge;
        if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector))
            _miniInspector.Visible = canShow && _inspectorVisibleBeforeLarge;
    }

    private void SubmitWorldlineJump(string nodeId, string source)
    {
        TimelineSession? session = _session;
        TimelineSnapshot? snapshot = _snapshot;
        if (session == null || snapshot == null) return;
        TimelineNodeSnapshot? node = FindNode(snapshot.Root, nodeId);
        if (node?.Action == null || node.IsCurrent) return;
        if (_smokeSuppressJump)
        {
            _smokeLastJumpNodeId = nodeId;
            return;
        }
        string mode = session.TryGetForwardPath(nodeId, out _) ? "forward" : "reload";
        Entry.Logger.Info($"Worldline jump submitted source={source} mode={mode} node={nodeId}.");
        _miniSuppressedForLarge = false;
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
        RefreshActionAvailability();
    }

    private void RefreshActionAvailability()
    {
        if (_snapshot != null && _selectedMiniNodeId != null && _miniJump != null)
            ApplyButtonAvailability(_miniJump, TimelineActionAvailability.Evaluate(_snapshot,
                FindNode(_snapshot.Root, _selectedMiniNodeId), WorldlineReplayController.IsBusy));
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow) && _graphWindow.IsAvailable)
            _graphWindow.RefreshActionAvailability();
    }

    private static void ApplyButtonAvailability(Button button, TimelineActionAvailability availability)
    {
        button.Disabled = !availability.JumpEnabled;
        button.TooltipText = availability.JumpReason;
        button.AddThemeColorOverride("font_disabled_color", new Color("9facb9"));
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
        _ => HomuraText.OutcomeOngoing,
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
            if (_panel != null) _panel.Visible = show && !_miniSuppressedForLarge;
            if (_miniInspector != null && GodotObject.IsInstanceValid(_miniInspector))
                _miniInspector.Visible = show && !_miniSuppressedForLarge;
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
