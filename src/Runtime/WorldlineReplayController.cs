using Godot;
using HomuraLog.Domain;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace HomuraLog.Runtime;

internal static class WorldlineReplayController
{
    private sealed record ReplayRequest(string EncounterKey, string NodeId, IReadOnlyList<TimelineAction> Path);
    private static ReplayRequest? _pending;
    private static bool _reloading;

    public static event Action<string>? StatusChanged;

    public static void Request(TimelineSession session, string nodeId)
    {
        if (_reloading) return;
        if (!session.TryGetPath(nodeId, out IReadOnlyList<TimelineAction> path) || path.Count == 0)
        {
            StatusChanged?.Invoke("Cannot find a replayable path for this node.");
            return;
        }
        _pending = new ReplayRequest(session.Snapshot.EncounterKey, nodeId, path);
        _reloading = true;
        session.Abort();
        StatusChanged?.Invoke($"Reloading combat entry · {path.Count} recorded steps");
        TaskHelper.RunSafely(ReloadRunAsync());
    }

    public static void OnCombatStarted(TimelineSession session)
    {
        ReplayRequest? request = _pending;
        if (request == null) return;
        if (!string.Equals(request.EncounterKey, session.Snapshot.EncounterKey, StringComparison.Ordinal))
        {
            Fail($"Reloaded a different encounter ({session.Snapshot.EncounterKey}).");
            return;
        }
        TaskHelper.RunSafely(ReplayAsync(session, request));
    }

    private static async Task ReloadRunAsync()
    {
        try
        {
            NGame game = NGame.Instance ?? throw new InvalidOperationException("Game root is unavailable.");
            // Stop the old run's FMOD event before asynchronous scene teardown, otherwise it can
            // overlap the newly loaded combat track during an SL.
            NRunMusicController.Instance?.StopMusic();
            await game.ReturnToMainMenu();
            // NMainMenu._Ready starts event:/music/menu_update after ReturnToMainMenu has created
            // the menu scene. Stop that global event here, before loading the run, or it remains
            // alive underneath the newly-created combat music.
            game.AudioManager.StopMusic();
            Entry.Logger.Info("Stopped main-menu music before loading the saved combat.");
            ReadSaveResult<SerializableRun> read = SaveManager.Instance.LoadRunSave();
            if (!read.Success || read.SaveData == null)
                throw new InvalidOperationException($"Unable to load the current run save: {read.Status}.");
            SerializableRun save = read.SaveData;
            RunState state = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(state, save);
            game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await game.LoadRun(state, save.PreFinishedRoom);
        }
        catch (Exception error)
        {
            Entry.Logger.Error($"Worldline reload failed safely: {error}");
            Fail($"Reload failed: {error.Message}");
        }
    }

    internal static async Task LoadCurrentRunForSmokeAsync()
    {
        NGame game = NGame.Instance ?? throw new InvalidOperationException("Game root is unavailable.");
        ReadSaveResult<SerializableRun> read = SaveManager.Instance.LoadRunSave();
        if (!read.Success || read.SaveData == null)
            throw new InvalidOperationException($"Unable to load the current run save: {read.Status}.");
        SerializableRun save = read.SaveData;
        RunState state = RunState.FromSerializable(save);
        await RunManager.Instance.SetUpSavedSingleplayer(state, save);
        game.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
        await game.LoadRun(state, save.PreFinishedRoom);
    }

    private static async Task ReplayAsync(TimelineSession session, ReplayRequest request)
    {
        using IDisposable replayCommits = session.SuppressReplayCommits();
        using IDisposable replaySpeed = new ReplaySpeedScope();
        try
        {
            TimelineAction[] executable = request.Path
                .Where(action => action.Kind != TimelineActionKind.CardChoice).ToArray();
            Queue<TimelineAction> choices = new(request.Path
                .Where(action => action.Kind == TimelineActionKind.CardChoice));
            using IDisposable selector = CardSelectCmd.PushSelector(new ReplayCardSelector(choices, session));
            for (int index = 0; index < executable.Length; index++)
            {
                TimelineAction action = executable[index];
                StatusChanged?.Invoke($"Replaying {index + 1}/{executable.Length}: {action.SourceId}");
                await WaitForPlayableState(session.Combat, action);
                await Execute(session.Combat, action);
            }
            if (choices.Count > 0)
                throw new InvalidOperationException($"{choices.Count} recorded card choice(s) were not requested by the game.");
            StatusChanged?.Invoke($"Reached worldline node {request.NodeId[..Math.Min(8, request.NodeId.Length)]}.");
            Entry.Logger.Info($"Worldline replay completed node={request.NodeId} actions={executable.Length}.");
            _pending = null;
            _reloading = false;
        }
        catch (Exception error)
        {
            Entry.Logger.Error($"Worldline replay stopped safely: {error}");
            Fail($"Replay stopped: {error.Message}");
        }
    }

    /// Temporarily accelerates the Godot loop and silences SFX during deterministic replay.
    /// Reflection keeps this optional across game builds; failure to find the audio method is harmless.
    private sealed class ReplaySpeedScope : IDisposable
    {
        private readonly float _oldScale;
        private readonly object? _audio;
        private readonly System.Reflection.MethodInfo? _setSfx;
        private readonly object? _oldSfx;

        public ReplaySpeedScope()
        {
            _oldScale = (float)Engine.TimeScale;
            Engine.TimeScale = 5.0f;
            try
            {
                NGame game = NGame.Instance!;
                _audio = game.AudioManager;
                _setSfx = _audio?.GetType().GetMethod("SetSfxVol", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (_setSfx != null)
                {
                    var prop = _audio!.GetType().GetProperty("SfxVol") ?? _audio.GetType().GetProperty("SfxVolume");
                    _oldSfx = prop?.GetValue(_audio);
                    _setSfx.Invoke(_audio, [0f]);
                }
            }
            catch (Exception error) { Entry.Logger.Warn($"Replay audio mute unavailable: {error.Message}"); }
            Entry.Logger.Info("Replay speed scope enabled (5x, SFX muted when supported).");
        }

        public void Dispose()
        {
            try { Engine.TimeScale = _oldScale; } catch { }
            try
            {
                if (_setSfx != null && _audio != null && _oldSfx is float volume)
                    _setSfx.Invoke(_audio, [volume]);
            }
            catch (Exception error) { Entry.Logger.Warn($"Replay audio restore failed: {error.Message}"); }
            Entry.Logger.Info("Replay speed scope restored.");
        }
    }

    private static async Task WaitForPlayableState(CombatState combat, TimelineAction action)
    {
        NGame game = NGame.Instance ?? throw new InvalidOperationException("Game root is unavailable.");
        for (int frame = 0; frame < 1800; frame++)
        {
            if (!CombatManager.Instance.IsInProgress || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat))
                throw new InvalidOperationException("Combat ended or changed during replay.");
            Player? player = LocalContext.GetMe(combat);
            if (player?.PlayerCombatState != null
                && player.PlayerCombatState.TurnNumber == action.Turn
                && player.PlayerCombatState.Phase.ToString() == "Play"
                && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null)
                return;
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        throw new TimeoutException($"Timed out waiting for turn {action.Turn}.");
    }

    private static async Task Execute(CombatState combat, TimelineAction action)
    {
        NGame game = NGame.Instance ?? throw new InvalidOperationException("Game root is unavailable.");
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("Local player is unavailable.");
        Creature? target = ResolveTarget(combat, player, action.TargetId);
        GameAction? captured = null;
        void Capture(GameAction candidate)
        {
            if (candidate.OwnerId != player.NetId) return;
            if (action.Kind == TimelineActionKind.PlayCard && candidate is PlayCardAction play
                && play.CardModelId.Entry == action.SourceId) captured = candidate;
            else if (action.Kind == TimelineActionKind.UsePotion && candidate is UsePotionAction potion
                     && potion.PotionIndex == (uint)action.Slot!.Value) captured = candidate;
            else if (action.Kind == TimelineActionKind.EndTurn && candidate is EndPlayerTurnAction) captured = candidate;
        }
        RunManager.Instance.ActionQueueSet.ActionEnqueued += Capture;
        try
        {
            switch (action.Kind)
            {
                case TimelineActionKind.PlayCard:
                {
                    CardModel card = FindCard(player, action);
                    if (!card.TryManualPlay(target))
                        throw new InvalidOperationException($"Card {action.SourceId} was rejected by the game.");
                    break;
                }
                case TimelineActionKind.UsePotion:
                {
                    if (!action.Slot.HasValue) throw new InvalidOperationException("Potion slot was not recorded.");
                    PotionModel potion = player.GetPotionAtSlotIndex(action.Slot.Value)
                        ?? throw new InvalidOperationException($"Potion slot {action.Slot.Value} is empty.");
                    if (potion.Id.Entry != action.SourceId)
                        throw new InvalidOperationException($"Potion mismatch: expected {action.SourceId}, found {potion.Id.Entry}.");
                    potion.EnqueueManualUse(target);
                    break;
                }
                case TimelineActionKind.EndTurn:
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new EndPlayerTurnAction(player, player.PlayerCombatState!.TurnNumber));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported replay action {action.Kind}.");
            }
            for (int frame = 0; captured == null && frame < 120; frame++)
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (captured == null) throw new InvalidOperationException($"The game did not enqueue {action.SourceId}.");
            await captured.CompletionTask;
            if (captured.Exception != null) throw new InvalidOperationException("The replayed action failed.", captured.Exception);
            await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        finally
        {
            RunManager.Instance.ActionQueueSet.ActionEnqueued -= Capture;
        }
    }

    private static CardModel FindCard(Player player, TimelineAction action)
    {
        IReadOnlyList<CardModel> hand = player.PlayerCombatState!.Hand.Cards;
        if (uint.TryParse(action.InstanceId, out uint combatIndex))
        {
            CardModel? exact = hand.FirstOrDefault(card =>
            {
                try { return NetCombatCard.FromModel(card).CombatCardIndex == combatIndex; }
                catch { return false; }
            });
            if (exact != null && exact.Id.Entry == action.SourceId) return exact;
        }
        return hand.FirstOrDefault(card => card.Id.Entry == action.SourceId)
            ?? throw new InvalidOperationException($"Card {action.SourceId} is not in hand.");
    }

    private static Creature? ResolveTarget(CombatState combat, Player player, uint? targetId)
    {
        if (!targetId.HasValue) return null;
        IEnumerable<Creature> creatures = combat.Enemies.Cast<Creature>().Append(player.Creature);
        return creatures.FirstOrDefault(creature => creature.CombatId == targetId.Value)
            ?? throw new InvalidOperationException($"Target #{targetId.Value} is unavailable.");
    }

    private static void Fail(string message)
    {
        StatusChanged?.Invoke(message);
        _pending = null;
        _reloading = false;
    }

    private sealed class ReplayCardSelector(Queue<TimelineAction> choices, TimelineSession session) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            if (!choices.TryDequeue(out TimelineAction? choice))
                throw new InvalidOperationException("The game requested an unrecorded card choice.");
            List<CardModel> available = options.ToList();
            List<CardModel> selected = [];
            foreach (string token in choice.Choices ?? [])
            {
                string[] parts = token.Split("::", StringSplitOptions.None);
                string id = parts[0].Split('#')[0];
                int upgrade = parts.Length > 2 && parts[2].StartsWith('u')
                    && int.TryParse(parts[2].AsSpan(1), out int parsed) ? parsed : 0;
                CardModel? card = FindExact(parts, available, selected);
                card ??= available.FirstOrDefault(candidate =>
                    candidate.Id.Entry == id && candidate.CurrentUpgradeLevel == upgrade && !selected.Contains(candidate));
                if (card == null)
                    throw new InvalidOperationException($"Recorded choice {id}+{upgrade} is unavailable.");
                selected.Add(card);
            }
            if (selected.Count < minSelect || selected.Count > maxSelect)
                throw new InvalidOperationException($"Recorded choice count {selected.Count} is outside {minSelect}..{maxSelect}.");
            session.ObserveReplayChoice(choice);
            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        private static CardModel? FindExact(string[] parts, List<CardModel> available, List<CardModel> selected)
        {
            if (parts.Length < 2) return null;
            string[] identity = parts[1].Split(':', 2);
            if (identity.Length != 2 || !uint.TryParse(identity[1], out uint index)) return null;
            return identity[0] switch
            {
                "combat" => available.FirstOrDefault(card => !selected.Contains(card) && CombatIndex(card) == index),
                "deck" => available.FirstOrDefault(card => !selected.Contains(card)
                    && card.Pile?.Cards.ToList().IndexOf(card) == (int)index),
                _ => null,
            };
        }

        private static uint? CombatIndex(CardModel card)
        {
            try { return NetCombatCard.FromModel(card).CombatCardIndex; }
            catch { return null; }
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new NotSupportedException("Combat worldline replay does not select card rewards.");
    }
}
