using System.Text.Json.Serialization;

namespace HomuraLog.Domain;

public enum TimelineActionKind { PlayCard, UsePotion, EndTurn, CardChoice }
public enum TimelineOutcome { Ongoing, Victory, Defeat, Aborted }

public sealed record TimelineAction(
    TimelineActionKind Kind,
    int Turn,
    string SourceId,
    string InstanceId = "",
    uint? TargetId = null,
    int? Slot = null,
    IReadOnlyList<string>? Choices = null,
    bool Skipped = false,
    int? HandPosition = null)
{
    [JsonIgnore]
    public string Key => string.Join("|", Kind, Turn, Escape(SourceId), Escape(InstanceId),
        TargetId?.ToString() ?? "-", Slot?.ToString() ?? "-", Skipped ? "1" : "0",
        Choices is null ? "" : string.Join(",", Choices.Select(Escape)));

    private static string Escape(string value) => value.Replace("%", "%25").Replace("|", "%7C").Replace(",", "%2C");
}

public sealed record CreatureState(uint? CombatId, string ModelId, int Hp, int MaxHp, int Block, bool Alive,
    string Intent = "", IReadOnlyList<IntentState>? Intents = null);

public sealed record IntentState(
    string TitleKey,
    string LabelKey,
    IReadOnlyList<IntentVariable>? Variables = null);

public sealed record IntentVariable(string Name, string Value, string Kind);

public sealed record CombatStateSummary(
    int Turn,
    int PlayerHp,
    int PlayerMaxHp,
    int Energy,
    IReadOnlyList<CreatureState> Enemies,
    string RngDigest = "",
    int PlayerBlock = 0);

public sealed class TimelineNode
{
    public string NodeId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentNodeId { get; set; }
    public TimelineAction? Action { get; set; }
    public CombatStateSummary? State { get; set; }
    public int VisitCount { get; set; }
    public DateTimeOffset FirstVisitedAt { get; set; }
    public DateTimeOffset LastVisitedAt { get; set; }
    public TimelineOutcome Outcome { get; set; }
    public Dictionary<string, TimelineNode> Children { get; set; } = new(StringComparer.Ordinal);
}

public sealed class EncounterRecord
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public required string EncounterKey { get; set; }
    public required string RunId { get; set; }
    public required int Floor { get; set; }
    public required string EncounterId { get; set; }
    public required string EntryFingerprint { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastOpenedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public TimelineOutcome Outcome { get; set; }
    public TimelineNode Root { get; set; } = new() { VisitCount = 1, FirstVisitedAt = DateTimeOffset.UtcNow, LastVisitedAt = DateTimeOffset.UtcNow };
}

public sealed record TimelineSnapshot(
    string EncounterKey,
    string CurrentNodeId,
    IReadOnlyList<TimelineAction> Path,
    IReadOnlyList<TimelineBranchSnapshot> NextBranches,
    int TotalNodes,
    TimelineNodeSnapshot Root);

public sealed record TimelineBranchSnapshot(TimelineAction Action, int Visits, TimelineOutcome Outcome, CombatStateSummary? State);

public sealed record TimelineNodeSnapshot(
    string NodeId,
    TimelineAction? Action,
    int Visits,
    TimelineOutcome Outcome,
    CombatStateSummary? State,
    bool IsCurrent,
    bool IsOnCurrentPath,
    IReadOnlyList<TimelineNodeSnapshot> Children);
