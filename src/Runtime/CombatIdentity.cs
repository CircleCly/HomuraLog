using System.Security.Cryptography;
using System.Text;
using HomuraLog.Domain;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace HomuraLog.Runtime;

internal sealed record CombatIdentity(string EncounterKey, string RunId, int Floor, string EncounterId, string EntryFingerprint);

internal static class CombatIdentityBuilder
{
    public static CombatIdentity Capture(CombatState state)
    {
        string seed = state.RunState.Rng.StringSeed;
        string runId = Hash($"{RunManager.Instance._startTime}:{seed}");
        int floor = state.RunState.TotalFloor;
        string encounter = state.Encounter?.Id.Entry ?? "UNKNOWN";
        var player = LocalContext.GetMe(state) ?? state.Players.Single();
        string enemies = string.Join(";", state.Enemies.Select(enemy =>
            $"{enemy.Monster?.Id.Entry ?? "UNKNOWN"}:{enemy.CurrentHp}:{enemy.MaxHp}:{enemy.CombatId}"));
        string deck = string.Join(";", player.Deck.Cards.Select(card => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}"));
        string entry = Hash($"{runId}|{floor}|{encounter}|{player.Creature.CurrentHp}|{player.Creature.MaxHp}|{deck}|{enemies}");
        return new CombatIdentity($"{runId}:{floor}:{encounter}:{entry}", runId, floor, encounter, entry);
    }

    public static CombatStateSummary State(CombatState state)
    {
        var player = LocalContext.GetMe(state) ?? state.Players.Single();
        var pcs = player.PlayerCombatState;
        CreatureState[] enemies = state.Enemies.Select(enemy =>
        {
            var intents = IntentStates(state, enemy);
            return new CreatureState(enemy.CombatId, enemy.Monster?.Id.Entry ?? "UNKNOWN", enemy.CurrentHp,
                enemy.MaxHp, enemy.Block, !enemy.IsDead, IntentText(state, enemy), intents);
        }).ToArray();
        string rng = Hash(state.RunState.Rng.StringSeed + ":" + state.RoundNumber + ":" +
            string.Join(',', enemies.Select(x => $"{x.CombatId}:{x.Hp}:{x.Block}")));
        return new CombatStateSummary(pcs?.TurnNumber ?? state.RoundNumber, player.Creature.CurrentHp,
            player.Creature.MaxHp, pcs?.Energy ?? 0, enemies, rng, player.Creature.Block);
    }

    private static string IntentText(CombatState state, MegaCrit.Sts2.Core.Entities.Creatures.Creature enemy)
    {
        try
        {
            return string.Join(" + ", enemy.Monster?.NextMove.Intents.Select(intent =>
            {
                string label = intent.GetIntentLabel(state.Allies, enemy).GetFormattedText().Trim();
                string title = intent.GetHoverTip(state.Allies, enemy).Title?.Trim() ?? "";
                return string.IsNullOrEmpty(label) ? title
                    : string.IsNullOrEmpty(title) ? label : $"{title} {label}";
            }) ?? []);
        }
        catch (Exception error)
        {
            Entry.Logger.Warn($"Could not capture intent for {enemy.Monster?.Id.Entry}: {error.Message}");
            return "?";
        }
    }

    private static IReadOnlyList<IntentState> IntentStates(
        CombatState state, MegaCrit.Sts2.Core.Entities.Creatures.Creature enemy)
    {
        try
        {
            return enemy.Monster?.NextMove.Intents.Select(intent =>
            {
                var label = intent.GetIntentLabel(state.Allies, enemy);
                IntentVariable[] variables = label.Variables.Select(pair => new IntentVariable(
                    pair.Key,
                    Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    pair.Value switch { bool => "bool", decimal or float or double => "decimal", sbyte or byte or short or ushort or int or uint or long or ulong => "integer", _ => "string" }))
                    .ToArray();
                return new IntentState(intent.IntentTitle.LocEntryKey, label.LocEntryKey, variables);
            }).ToArray() ?? [];
        }
        catch (Exception error)
        {
            Entry.Logger.Warn($"Could not capture stable intent for {enemy.Monster?.Id.Entry}: {error.Message}");
            return [];
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
}

internal static class MultiplayerGuard
{
    public static bool IsSinglePlayer(CombatState state) =>
        state.Players.Count == 1 && RunManager.Instance.NetService.Type == NetGameType.Singleplayer;
}
