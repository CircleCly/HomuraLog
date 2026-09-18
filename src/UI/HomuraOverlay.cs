using Godot;
using HomuraLog.Domain;
using HomuraLog.Runtime;
using MegaCrit.Sts2.Core.Combat;
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
        _resetMini.Pressed += () => _miniGraph?.ResetToCurrent();
        header.AddChild(_resetMini);

        _miniGraph = new TimelineMiniGraph
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _miniGraph.NodeActivated += ShowNodeDetails;
        _miniGraph.MoreBranchesActivated += ShowFullGraphAt;
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
        _selectedMiniNodeId = null;
        DestroyMiniInspector();
        _graphWindow?.QueueFree();
        _graphWindow = null;
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
        _snapshot = snapshot;
        Visible = true;
        Render();
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow))
            _graphWindow.UpdateSnapshot(snapshot);
        else
            _graphWindow = null;
        RefreshCardBadges();
    }

    private void Render()
    {
        if (_snapshot == null || _status == null) return;
        _status.Text = HomuraText.Nodes(_snapshot.TotalNodes);
        _miniGraph?.SetSnapshot(_snapshot);
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
            $"{enemy.ModelId} {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" (+{enemy.Block})" : "")));
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
            $"  {enemy.ModelId} {enemy.Hp}/{enemy.MaxHp}" + (enemy.Block > 0 ? $" (+{enemy.Block})" : "")
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
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow))
        {
            _graphWindow.UpdateSnapshot(_snapshot);
            _graphWindow.Visible = true;
            if (focusNodeId != null)
            {
                string target = focusNodeId;
                Callable.From(() => _graphWindow?.FocusNode(target)).CallDeferred();
            }
            return;
        }
        _graphWindow = new TimelineGraphWindow(_snapshot) { Name = "HomuraTimelineGraph" };
        _graphWindow.JumpRequested += RequestWorldlineJump;
        _graphWindow.DeleteRequested += DeleteWorldlineNode;
        AddChild(_graphWindow);
        if (focusNodeId != null)
            Callable.From(() => _graphWindow?.FocusNode(focusNodeId)).CallDeferred();
    }

    private void DeleteWorldlineNode(string nodeId)
    {
        if (_session == null || _snapshot == null || nodeId == _snapshot.Root.NodeId) return;
        _session.DeleteNode(nodeId);
    }

    internal void RunSmokeCheck()
    {
        ShowFullGraph();
        if (_graphWindow != null)
            Callable.From(_graphWindow.RunFullscreenSmokeCheck).CallDeferred();
        Entry.Logger.Info("UI smoke check created the Ritsu main window, compact graph, and fullscreen timeline graph.");
    }

    private void RequestWorldlineJump(string nodeId)
    {
        if (_session == null) return;
        _graphWindow?.QueueFree();
        _graphWindow = null;
        DestroyMiniInspector();
        WorldlineReplayController.Request(_session, nodeId);
    }

    private void RequestMiniWorldlineJump()
    {
        if (_selectedMiniNodeId == null || _snapshot == null
            || _selectedMiniNodeId == _snapshot.CurrentNodeId) return;
        RequestWorldlineJump(_selectedMiniNodeId);
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
            if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow))
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
            if (field?.GetValue(_miniInspector) is Label inspectorTitle) inspectorTitle.Text = HomuraText.Details;
        }
        catch (Exception error) { Entry.Logger.Warn($"Could not refresh localized window title: {error.Message}"); }
        if (_graphWindow != null && GodotObject.IsInstanceValid(_graphWindow))
        {
            _graphWindow.QueueFree();
            _graphWindow = null;
        }
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
        // The fullscreen graph remains reachable even in compact mode; otherwise a
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
