using Godot;
using HomuraLog.Domain;
using HomuraLog.Persistence;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace HomuraLog.Runtime;

internal sealed class TimelineSession : IDisposable
{
    private readonly CombatState _combat;
    private readonly TimelineStore _store;
    private readonly TimelineTree _tree;
    private readonly Dictionary<GameAction, PendingAction> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GameAction, List<TimelineAction>> _choices = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    private sealed record PendingAction(GameAction Native, TimelineAction Action);

    private TimelineSession(CombatState combat, TimelineStore store, TimelineTree tree)
    {
        _combat = combat;
        _store = store;
        _tree = tree;
        RunManager.Instance.ActionQueueSet.ActionEnqueued += ObserveActionEnqueued;
        RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived += ObserveChoice;
    }

    public event Action<TimelineSnapshot>? Changed;
    public TimelineSnapshot Snapshot => _tree.Snapshot();
    public CombatState Combat => _combat;

    public bool TryGetPath(string nodeId, out IReadOnlyList<TimelineAction> path)
    {
        List<TimelineAction> actions = [];
        bool Find(TimelineNode node)
        {
            if (node.NodeId == nodeId) return true;
            foreach (TimelineNode child in node.Children.Values)
            {
                if (!Find(child)) continue;
                actions.Insert(0, child.Action!);
                return true;
            }
            return false;
        }
        bool found = Find(_tree.Record.Root);
        path = found ? actions : [];
        return found;
    }

    internal void ObserveReplayChoice(TimelineAction choice)
    {
        GameAction? owner = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (owner == null || !_pending.ContainsKey(owner))
            throw new InvalidOperationException("Replay choice has no tracked owning action.");
        if (!_choices.TryGetValue(owner, out List<TimelineAction>? list))
            _choices[owner] = list = [];
        list.Add(choice);
    }

    public static TimelineSession Start(CombatState combat)
    {
        string directory = Path.Combine(OS.GetUserDataDir(), "HomuraLog", "timelines-v1");
        TimelineStore store = new(directory);
        CombatIdentity identity = CombatIdentityBuilder.Capture(combat);
        EncounterRecord? record = store.Load(identity.EncounterKey);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (record == null)
        {
            record = new EncounterRecord
            {
                EncounterKey = identity.EncounterKey,
                RunId = identity.RunId,
                Floor = identity.Floor,
                EncounterId = identity.EncounterId,
                EntryFingerprint = identity.EntryFingerprint,
                StartedAt = now,
                LastOpenedAt = now,
                Root = new TimelineNode
                {
                    VisitCount = 1, FirstVisitedAt = now, LastVisitedAt = now,
                    State = CombatIdentityBuilder.State(combat),
                },
            };
        }
        else
        {
            record.LastOpenedAt = now;
            record.Root.VisitCount++;
            record.Root.LastVisitedAt = now;
            record.Outcome = TimelineOutcome.Ongoing;
        }
        var session = new TimelineSession(combat, store, new TimelineTree(record));
        session.Save();
        return session;
    }

    private void ObserveActionEnqueued(GameAction action)
    {
        try { OnActionEnqueued(action); }
        catch (Exception error) { Entry.Logger.Error($"Action observation failed safely: {error}"); }
    }

    private void OnActionEnqueued(GameAction action)
    {
        if (_disposed || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _combat) || !action.Id.HasValue)
            return;
        TimelineAction? timelineAction = ToTimelineAction(action);
        if (timelineAction == null) return;
        _pending[action] = new PendingAction(action, timelineAction);
        action.BeforeCancelled += ObserveActionCancelled;
        action.AfterFinished += ObserveActionFinished;
    }

    private TimelineAction? ToTimelineAction(GameAction action)
    {
        var player = LocalContext.GetMe(_combat);
        if (player == null || action.OwnerId != player.NetId) return null;
        int turn = player.PlayerCombatState?.TurnNumber ?? _combat.RoundNumber;
        switch (action)
        {
            case PlayCardAction play:
                CardModel? card = play.NetCombatCard.ToCardModelOrNull();
                string instance = play.NetCombatCard.CombatCardIndex.ToString();
                string source = card?.Id.Entry ?? play.CardModelId.Entry;
                int handIndex = card == null ? -1 : NPlayerHand.Instance?.ActiveHolders
                    .Select(holder => holder.CardNode?.Model).ToList()
                    .FindIndex(candidate => ReferenceEquals(candidate, card)) ?? -1;
                if (handIndex < 0 && card != null && player.PlayerCombatState != null)
                    handIndex = player.PlayerCombatState.Hand.Cards.ToList()
                        .FindIndex(candidate => ReferenceEquals(candidate, card));
                int? handPosition = handIndex >= 0 ? handIndex + 1 : null;
                return new TimelineAction(TimelineActionKind.PlayCard, turn, source, instance,
                    play.TargetId, HandPosition: handPosition);
            case UsePotionAction potion when potion.WasEnqueuedInCombat:
                string potionId = potion.Player.GetPotionAtSlotIndex((int)potion.PotionIndex)?.Id.Entry ?? "UNKNOWN_POTION";
                return new TimelineAction(TimelineActionKind.UsePotion, turn, potionId,
                    potion.PotionIndex.ToString(), potion.TargetId, (int)potion.PotionIndex);
            case EndPlayerTurnAction:
                return new TimelineAction(TimelineActionKind.EndTurn, turn, "END_TURN");
            default:
                return null;
        }
    }

    private void ObserveChoice(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        try { OnChoice(player, choiceId, result); }
        catch (Exception error) { Entry.Logger.Error($"Choice observation failed safely: {error}"); }
    }

    private void OnChoice(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        if (_disposed || LocalContext.GetMe(_combat)?.NetId != player.NetId) return;
        GameAction? owner = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (owner == null || !_pending.TryGetValue(owner, out PendingAction? pending))
            owner = _pending.Keys.SingleOrDefault(action => action.State == GameActionState.GatheringPlayerChoice);
        if (owner == null || !_pending.TryGetValue(owner, out pending))
        {
            Entry.Logger.Warn($"Untracked choice id={choiceId} type={result.type}; no owning player action.");
            return;
        }
        List<string> selected = DecodeChoice(player, result);
        TimelineAction choice = new(TimelineActionKind.CardChoice, pending.Action.Turn,
            $"{pending.Action.SourceId}:CHOICE", Choices: selected, Skipped: selected.Count == 0);
        if (!_choices.TryGetValue(owner, out List<TimelineAction>? list))
            _choices[owner] = list = [];
        list.Add(choice);
        Entry.Logger.Info($"Observed choice id={choiceId} type={result.type} selected={selected.Count} [{string.Join(",", selected)}].");
    }

    private static List<string> DecodeChoice(Player player, NetPlayerChoiceResult result)
    {
        static string Token(CardModel card, string instance) =>
            $"{card.Id.Entry}::{instance}::u{card.CurrentUpgradeLevel}";

        return result.type switch
        {
            PlayerChoiceType.CanonicalCard => (result.canonicalCards ?? [])
                .Select((card, index) => Token(card, $"canonical:{index}"))
                .ToList(),
            PlayerChoiceType.CombatCard => (result.combatCards ?? [])
                .Select(card => card.ToCardModelOrNull() is { } model
                    ? Token(model, $"combat:{card.CombatCardIndex}")
                    : $"UNKNOWN_CARD::combat:{card.CombatCardIndex}::u0")
                .ToList(),
            PlayerChoiceType.DeckCard => (result.deckCards ?? [])
                .Select(card => Token(card.ToCardModel(player), $"deck:{card.DeckIndex}"))
                .ToList(),
            PlayerChoiceType.MutableCard => (result.mutableCards ?? [])
                .Select((card, index) =>
                    $"{card.Id?.Entry ?? "UNKNOWN_CARD"}::mutable:{index}::u{card.CurrentUpgradeLevel}")
                .ToList(),
            PlayerChoiceType.Index => (result.indexes ?? [])
                .Select(index => $"INDEX::{index}::u0")
                .ToList(),
            PlayerChoiceType.Player => result.playerId.HasValue
                ? [$"PLAYER::{result.playerId.Value}::u0"]
                : [],
            _ => [],
        };
    }

    private void ObserveActionCancelled(GameAction action)
    {
        try { OnActionCancelled(action); }
        catch (Exception error) { Entry.Logger.Error($"Action cancellation observation failed safely: {error}"); }
    }

    private void OnActionCancelled(GameAction action)
    {
        Detach(action);
        _pending.Remove(action);
        _choices.Remove(action);
    }

    private void ObserveActionFinished(GameAction action)
    {
        try { OnActionFinished(action); }
        catch (Exception error) { Entry.Logger.Error($"Completed action observation failed safely: {error}"); }
    }

    private void OnActionFinished(GameAction action)
    {
        if (!_pending.Remove(action, out PendingAction? pending)) return;
        Detach(action);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CombatStateSummary state = CombatIdentityBuilder.State(_combat);
        _tree.Append(pending.Action, state, now);
        if (_choices.Remove(action, out List<TimelineAction>? choices))
            foreach (TimelineAction choice in choices) _tree.Append(choice, state, now);
        Save();
        NotifyChanged();
        Entry.Logger.Info($"Committed {pending.Action.Kind} {pending.Action.SourceId} turn={pending.Action.Turn} node={_tree.Current.NodeId}.");
    }

    public void End(bool victory)
    {
        _tree.MarkCurrent(victory ? TimelineOutcome.Victory : TimelineOutcome.Defeat, DateTimeOffset.UtcNow);
        _store.SaveSummaryAndPrune(_tree.Record);
        NotifyChanged();
    }

    public void Abort()
    {
        if (_tree.Current != _tree.Record.Root)
            _tree.MarkCurrent(TimelineOutcome.Aborted, DateTimeOffset.UtcNow);
        Save();
    }

    private void Save()
    {
        try { _store.Save(_tree.Record); }
        catch (Exception error) { Entry.Logger.Error($"Timeline save failed safely: {error}"); }
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(_tree.Snapshot()); }
        catch (Exception error) { Entry.Logger.Error($"Timeline UI notification failed safely: {error}"); }
    }

    private void Detach(GameAction action)
    {
        action.BeforeCancelled -= ObserveActionCancelled;
        action.AfterFinished -= ObserveActionFinished;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RunManager.Instance.ActionQueueSet.ActionEnqueued -= ObserveActionEnqueued;
        RunManager.Instance.PlayerChoiceSynchronizer.PlayerChoiceReceived -= ObserveChoice;
        foreach (PendingAction item in _pending.Values)
        {
            item.Native.BeforeCancelled -= ObserveActionCancelled;
            item.Native.AfterFinished -= ObserveActionFinished;
        }
        _pending.Clear();
        _choices.Clear();
    }
}
