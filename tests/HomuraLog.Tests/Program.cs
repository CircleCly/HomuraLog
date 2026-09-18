using HomuraLog.Domain;
using HomuraLog.Persistence;

static void Assert(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static EncounterRecord Record(string key = "run:1:jaw_worm") => new()
{
    EncounterKey = key, RunId = "run", Floor = 1, EncounterId = "JAW_WORM", EntryFingerprint = "entry",
    StartedAt = DateTimeOffset.UtcNow, LastOpenedAt = DateTimeOffset.UtcNow,
};

var state = new CombatStateSummary(1, 70, 80, 2, []);
var record = Record();
var tree = new TimelineTree(record);
var strikeA = new TimelineAction(TimelineActionKind.PlayCard, 1, "STRIKE", "3", 8);
var strikeB = new TimelineAction(TimelineActionKind.PlayCard, 1, "STRIKE", "4", 8);
var positionedStrike = strikeA with { HandPosition = 3 };
Assert(positionedStrike.HandPosition == 3, "Played-card hand position should be retained.");
Assert(positionedStrike.Key == strikeA.Key, "Display-only hand position must not split an existing instance branch.");
tree.Append(strikeA, state, DateTimeOffset.UtcNow);
tree.Append(new TimelineAction(TimelineActionKind.EndTurn, 1, "END_TURN"), state, DateTimeOffset.UtcNow);

var secondAttempt = new TimelineTree(record);
secondAttempt.Append(strikeB, state, DateTimeOffset.UtcNow);
Assert(record.Root.Children.Count == 2, "Duplicate card instances must create distinct strict-prefix branches.");

var thirdAttempt = new TimelineTree(record);
thirdAttempt.Append(strikeA, state, DateTimeOffset.UtcNow);
Assert(record.Root.Children[strikeA.Key].VisitCount == 2, "Revisiting an action must increment its visit count.");

TimelineAction choice = new(TimelineActionKind.CardChoice, 1, "DISCOVERY", Choices: ["BASH#0", "DEFEND#1"]);
Assert(choice.Key != (choice with { Choices = ["DEFEND#1", "BASH#0"] }).Key, "Choice order is part of a worldline.");

string directory = Path.Combine(Path.GetTempPath(), "HomuraLogTests", Guid.NewGuid().ToString("N"));
var store = new TimelineStore(directory);
store.Save(record);
EncounterRecord loaded = store.Load(record.EncounterKey) ?? throw new InvalidOperationException("Round-trip load failed.");
Assert(loaded.Root.Children.Count == 2, "Persistence must retain branches.");

var deepRecord = Record("deep-combat");
var deepTree = new TimelineTree(deepRecord);
for (int index = 0; index < 180; index++)
    deepTree.Append(new TimelineAction(TimelineActionKind.PlayCard, index / 10 + 1, "FUEL", index.ToString()), state, DateTimeOffset.UtcNow);
store.Save(deepRecord);
EncounterRecord deepLoaded = store.Load(deepRecord.EncounterKey) ?? throw new InvalidOperationException("Deep timeline load failed.");
TimelineNode cursor = deepLoaded.Root;
for (int index = 0; index < 180; index++) cursor = cursor.Children.Values.Single();
Assert(cursor.Action?.SourceId == "FUEL", "Long combats must survive JSON round-trip beyond the default depth limit.");

TimelineAction chosenInstanceA = new(TimelineActionKind.CardChoice, 1, "SOURCE:CHOICE",
    Choices: ["STRIKE_RED::combat:7::u0"]);
TimelineAction chosenInstanceB = new(TimelineActionKind.CardChoice, 1, "SOURCE:CHOICE",
    Choices: ["STRIKE_RED::combat:8::u0"]);
TimelineAction chosenUpgrade = new(TimelineActionKind.CardChoice, 1, "SOURCE:CHOICE",
    Choices: ["STRIKE_RED::combat:7::u1"]);
Assert(chosenInstanceA.Key != chosenInstanceB.Key, "Different selected card instances must fork.");
Assert(chosenInstanceA.Key != chosenUpgrade.Key, "Different selected upgrade levels must fork.");

var projectionRecord = Record("projection-chain");
var projectionWriter = new TimelineTree(projectionRecord);
var projectionActions = Enumerable.Range(0, 10)
    .Select(index => new TimelineAction(TimelineActionKind.PlayCard, 1, "CARD", index.ToString()))
    .ToArray();
foreach (TimelineAction action in projectionActions)
    projectionWriter.Append(action, state, DateTimeOffset.UtcNow);
var projectionCursor = new TimelineTree(projectionRecord);
foreach (TimelineAction action in projectionActions.Take(5))
    Assert(projectionCursor.FollowExisting(action), "Projection fixture path should exist.");
MiniTimelineSegment chainProjection = MiniTimelineProjector.Create(projectionCursor.Snapshot());
Assert(chainProjection.Rows.Count(row => row.Node != null) == 6,
    "Current mini segment must retain three previous, current, and two following actions.");
Assert(chainProjection.Rows.Any(row => row.IsOmission && row.OmittedCount == 2),
    "Current mini segment must report omitted earlier actions.");
Assert(chainProjection.Rows.Any(row => row.IsOmission && row.OmittedCount == 3),
    "Current mini segment must report omitted later actions.");

var fanoutRecord = Record("projection-fanout");
TimelineAction[] fanoutActions = Enumerable.Range(0, 8)
    .Select(index => new TimelineAction(TimelineActionKind.PlayCard, 1, $"CARD_{index}", index.ToString()))
    .ToArray();
foreach (TimelineAction action in fanoutActions)
    new TimelineTree(fanoutRecord).Append(action, state, DateTimeOffset.UtcNow.AddSeconds(Array.IndexOf(fanoutActions, action)));
var fanoutCursor = new TimelineTree(fanoutRecord);
Assert(fanoutCursor.FollowExisting(fanoutActions[0]), "Current fanout branch should exist.");
MiniTimelineSegment fanoutProjection = MiniTimelineProjector.Create(fanoutCursor.Snapshot());
Assert(fanoutProjection.Children.Count == 6 && fanoutProjection.HiddenBranchCount == 2,
    "Mini projection must cap a decision at six branches and report the remainder.");
Assert(fanoutProjection.Children.Any(child => child.Rows.Any(row => row.Node?.IsCurrent == true)),
    "The current branch must survive branch capping regardless of recency.");
Assert(fanoutProjection.Children.Any(child => child.Rows.Any(row => row.Node?.Action?.SourceId == "CARD_7")),
    "Non-current mini branches must be selected by most recent visit.");

CompactTimelineLayoutResult compactFanout = CompactTimelineLayout.Create(
    fanoutProjection, _ => 180, _ => 180);
Assert(compactFanout.CurrentItemId != null, "Compact layout must identify the current action.");
Assert(compactFanout.Edges.Count > 0, "Compact layout must generate arrows after placing nodes.");
CompactTimelineItem[] compactItems = compactFanout.Items.ToArray();
for (int left = 0; left < compactItems.Length; left++)
for (int right = left + 1; right < compactItems.Length; right++)
{
    CompactTimelineItem a = compactItems[left], b = compactItems[right];
    bool overlaps = a.X < b.X + b.Width && a.X + a.Width > b.X
        && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    Assert(!overlaps, $"Compact items must not overlap: {a.Id} and {b.Id}.");
}
int fanoutLanes = compactItems.Where(item => !item.IsMoreBranches)
    .Select(item => MathF.Round(item.X)).Distinct().Count();
Assert(fanoutLanes <= 3,
    "A six-way decision must use the center, left, and right lanes instead of six leaf columns.");
int projectedItems = FlattenMini(fanoutProjection).Sum(segment => segment.Rows.Count
    + (segment.HiddenBranchCount > 0 ? 1 : 0));
Assert(compactItems.Length == projectedItems,
    "Compact layout must preserve every projected action, omission, and hidden-branch prompt.");

LargeWindowBounds hdWindow = LargeWindowGeometry.Default(1920, 1080);
Assert(hdWindow.Width == 1536 && hdWindow.Height == 810
    && hdWindow.X == 192 && hdWindow.Y == 135,
    "The large timeline window must default to 80% by 75% and remain centered at 1920x1080.");
LargeWindowBounds qhdWindow = LargeWindowGeometry.Default(2560, 1440);
Assert(qhdWindow.Width == 2048 && qhdWindow.Height == 1080,
    "The large timeline window must scale responsively at 2560x1440.");
LargeWindowBounds smallWindow = LargeWindowGeometry.Default(800, 600);
Assert(smallWindow.X >= LargeWindowGeometry.Margin && smallWindow.Y >= LargeWindowGeometry.Margin
    && smallWindow.X + smallWindow.Width <= 800 - LargeWindowGeometry.Margin
    && smallWindow.Y + smallWindow.Height <= 600 - LargeWindowGeometry.Margin,
    "Small viewports must retain the large window safety margin.");
LargeWindowBounds clampedWindow = LargeWindowGeometry.Clamp(1920, 1080, -400, 900, 1700, 900);
Assert(clampedWindow.X >= LargeWindowGeometry.Margin && clampedWindow.Y >= LargeWindowGeometry.Margin
    && clampedWindow.X + clampedWindow.Width <= 1920 - LargeWindowGeometry.Margin
    && clampedWindow.Y + clampedWindow.Height <= 1080 - LargeWindowGeometry.Margin,
    "Viewport changes must clamp a manually adjusted large window back on screen.");

var forwardRecord = Record("forward-path");
var forwardWriter = new TimelineTree(forwardRecord);
TimelineAction forwardA = new(TimelineActionKind.PlayCard, 1, "A", "1");
TimelineAction forwardB = new(TimelineActionKind.PlayCard, 1, "B", "2");
TimelineAction forwardChoice = new(TimelineActionKind.CardChoice, 1, "B:CHOICE", Choices: ["C::combat:3::u0"]);
TimelineNode nodeA = forwardWriter.Append(forwardA, state, DateTimeOffset.UtcNow);
TimelineNode nodeB = forwardWriter.Append(forwardB, state, DateTimeOffset.UtcNow);
TimelineNode choiceNode = forwardWriter.Append(forwardChoice, state, DateTimeOffset.UtcNow);
var siblingWriter = new TimelineTree(forwardRecord);
TimelineAction siblingAction = new(TimelineActionKind.PlayCard, 1, "SIBLING", "4");
TimelineNode siblingNode = siblingWriter.Append(siblingAction, state, DateTimeOffset.UtcNow);
var forwardCursor = new TimelineTree(forwardRecord);
Assert(forwardCursor.FollowExisting(forwardA), "Forward fixture current node should exist.");
Assert(forwardCursor.TryGetForwardPath(nodeB.NodeId, out IReadOnlyList<TimelineAction> direct)
    && direct.SequenceEqual([forwardB]), "Direct child must produce a one-action forward path.");
Assert(forwardCursor.TryGetForwardPath(choiceNode.NodeId, out IReadOnlyList<TimelineAction> deep)
    && deep.SequenceEqual([forwardB, forwardChoice]), "Descendant path must preserve action and choice order.");
Assert(!forwardCursor.TryGetForwardPath(nodeA.NodeId, out _), "The current node is not a forward target.");
Assert(!forwardCursor.TryGetForwardPath(forwardRecord.Root.NodeId, out _), "An ancestor is not a forward target.");
Assert(!forwardCursor.TryGetForwardPath(siblingNode.NodeId, out _), "A sibling is not a forward target.");
Assert(!forwardCursor.TryGetForwardPath("missing", out _), "A missing node is not a forward target.");
Console.WriteLine("HomuraLog core checks passed.");

static IEnumerable<MiniTimelineSegment> FlattenMini(MiniTimelineSegment root)
{
    yield return root;
    foreach (MiniTimelineSegment child in root.Children)
    foreach (MiniTimelineSegment descendant in FlattenMini(child)) yield return descendant;
}
