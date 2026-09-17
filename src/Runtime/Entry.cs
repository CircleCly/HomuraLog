using System.Reflection;
using Godot;
using HomuraLog.Domain;
using HomuraLog.UI;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using MegaCrit.Sts2.Core.Helpers;

namespace HomuraLog.Runtime;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "HomuraLog";
    internal static HomuraLogLogger Logger { get; private set; } = null!;
    internal static TimelineSession? Session { get; private set; }
    private static HomuraOverlay? _overlay;
    private static bool _multiplayerWarned;
    private static CombatState? _watchdogRejectedCombat;
    private static bool _watchdogRecovering;
    private static readonly bool SmokeCheck = System.Environment.GetCommandLineArgs()
        .Contains("--homuralog-smoke", StringComparer.OrdinalIgnoreCase);
    private static bool _smokeReplayRequested;

    public static void Initialize()
    {
        Logger = new HomuraLogLogger();
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, Assembly.GetExecutingAssembly());
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(evt =>
        {
            if (evt.CombatState is CombatState combat) BeginCombat(combat);
        });
        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(_ => EndCombat());
        EnsureOverlay();
        if (SmokeCheck) TaskHelper.RunSafely(RunSmokeCheckAsync());
        Logger.Info($"Initialized v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} game_target=0.111.0.");
    }

    private static void BeginCombat(CombatState combat)
    {
        if (Session != null && ReferenceEquals(Session.Combat, combat)) return;
        ResetSession(abort: true);
        if (!MultiplayerGuard.IsSinglePlayer(combat))
        {
            _watchdogRejectedCombat = combat;
            if (!_multiplayerWarned)
            {
                _multiplayerWarned = true;
                Logger.Warn("Multiplayer combat detected; timeline recording is disabled.");
            }
            _overlay?.SetDisabled(true);
            return;
        }
        _watchdogRejectedCombat = null;
        try
        {
            Session = TimelineSession.Start(combat);
            EnsureOverlay();
            _overlay!.Bind(Session);
            if (SmokeCheck) _overlay.RunSmokeCheck();
            WorldlineReplayController.OnCombatStarted(Session);
            if (SmokeCheck && !_smokeReplayRequested)
            {
                _smokeReplayRequested = true;
                TaskHelper.RunSafely(RunSmokeReplayAsync(Session));
            }
            Logger.Info($"Combat matched key={Session.Snapshot.EncounterKey} nodes={Session.Snapshot.TotalNodes}.");
        }
        catch (Exception error)
        {
            Logger.Error($"Combat recording disabled after initialization failure: {error}");
            ResetSession(abort: false);
        }
    }

    internal static void RecoverMissedCombatStart()
    {
        if (_watchdogRecovering || !CombatManager.Instance.IsInProgress) return;
        CombatState? combat;
        try { combat = CombatManager.Instance.DebugOnlyGetState(); }
        catch { return; }
        if (combat == null) return;
        if (ReferenceEquals(Session?.Combat, combat) || ReferenceEquals(_watchdogRejectedCombat, combat)) return;

        _watchdogRecovering = true;
        try
        {
            Logger.Warn("Detected an active combat without a bound session; recovering after a non-standard reload (for example Rewind).");
            BeginCombat(combat);
        }
        catch (Exception error)
        {
            Logger.Error($"Combat session watchdog failed safely: {error}");
        }
        finally { _watchdogRecovering = false; }
    }

    private static async Task RunSmokeCheckAsync()
    {
        if (NGame.Instance == null) return;
        await NGame.Instance.GameStartupComplete;
        if (!SaveManager.Instance.HasRunSave)
        {
            Logger.Warn("UI smoke check skipped: no current run save.");
            return;
        }
        await WorldlineReplayController.LoadCurrentRunForSmokeAsync();
    }

    private static async Task RunSmokeReplayAsync(TimelineSession session)
    {
        for (int i = 0; i < 4; i++)
            await NGame.Instance!.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
        TimelineNodeSnapshot? target = session.Snapshot.Root.Children
            .FirstOrDefault(node => node.Action?.Kind == TimelineActionKind.PlayCard);
        if (target == null)
        {
            Logger.Warn("Replay smoke check skipped: no root card branch.");
            return;
        }
        Logger.Info($"Replay smoke check targeting node={target.NodeId} action={target.Action!.SourceId}.");
        WorldlineReplayController.Request(session, target.NodeId);
    }

    private static void EndCombat()
    {
        if (Session != null)
        {
            bool victory = Session.Combat.Enemies.All(enemy => enemy.IsDead || enemy.CurrentHp <= 0);
            bool defeat = Session.Combat.Players.All(player => player.Creature.IsDead || player.Creature.CurrentHp <= 0);
            try
            {
                if (victory || defeat) Session.End(victory);
                else Session.Abort();
            }
            catch (Exception error) { Logger.Error($"Combat finalization failed: {error}"); }
        }
        ResetSession(abort: false);
    }

    private static void EnsureOverlay()
    {
        if (_overlay != null || NGame.Instance == null) return;
        _overlay = new HomuraOverlay { Name = "HomuraLogOverlay" };
        NGame.Instance.AddChild(_overlay);
    }

    private static void ResetSession(bool abort)
    {
        if (Session != null)
        {
            if (abort) Session.Abort();
            Session.Dispose();
            Session = null;
        }
        _overlay?.Unbind();
    }
}
