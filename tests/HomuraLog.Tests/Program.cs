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

var deleteRecord = Record("delete-subtree");
var deleteTree = new TimelineTree(deleteRecord);
TimelineNode deleteParent = deleteTree.Append(strikeA, state, DateTimeOffset.UtcNow);
deleteTree.Append(new TimelineAction(TimelineActionKind.EndTurn, 1, "END_TURN"), state, DateTimeOffset.UtcNow);
Assert(deleteTree.Remove(deleteParent.NodeId), "Deleting a node must remove the action-keyed child entry.");
Assert(deleteRecord.Root.Children.Count == 0, "Deleting a node must remove its entire subtree.");
Assert(deleteTree.Current.NodeId == deleteRecord.Root.NodeId,
    "Deleting a node on the current path must move the timeline cursor to its surviving parent.");
Assert(!deleteTree.Remove(deleteRecord.Root.NodeId), "The timeline root must remain undeletable.");

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
TimelineSnapshot chainSnapshot = projectionCursor.Snapshot();
MiniTimelineProjection chainProjection = MiniTimelineProjector.Create(
    chainSnapshot, chainSnapshot.CurrentNodeId, 0, 0);
Assert(chainProjection.Root.Rows.Count == 3 && chainProjection.Root.Rows[^1].IsFocused,
    "Mini projection must retain exactly two parent actions above the focus.");
Assert(chainProjection.Root.Children.Count == 1
    && chainProjection.Root.Children[0].Rows.Count == 3,
    "A visible branch must contain its first action and at most two continuation actions.");
CompactTimelineLayoutResult singleBranchLayout = CompactTimelineLayout.Create(
    chainProjection.Root, _ => 160, _ => 160);
CompactTimelineItem singleFocus = singleBranchLayout.Items.Single(item => item.Row?.IsFocused == true);
CompactTimelineItem singleChild = singleBranchLayout.Items.First(item => item.Row?.IsBranchFirstStep == true);
Assert(Math.Abs((singleFocus.X + singleFocus.Width / 2) - (singleChild.X + singleChild.Width / 2)) < 0.1f,
    "A sole branch must remain centered beneath the focused node.");

var fanoutRecord = Record("projection-fanout");
TimelineAction[] fanoutActions = Enumerable.Range(0, 8)
    .Select(index => new TimelineAction(TimelineActionKind.PlayCard, 1, $"CARD_{index}", index.ToString()))
    .ToArray();
foreach (TimelineAction action in fanoutActions)
    new TimelineTree(fanoutRecord).Append(action, state, DateTimeOffset.UtcNow.AddSeconds(Array.IndexOf(fanoutActions, action)));
var fanoutCursor = new TimelineTree(fanoutRecord);
Assert(fanoutCursor.FollowExisting(fanoutActions[0]), "Current fanout branch should exist.");
TimelineSnapshot decisionSnapshot = new TimelineTree(fanoutRecord).Snapshot();
MiniTimelineProjection fanoutProjection = MiniTimelineProjector.Create(
    decisionSnapshot, decisionSnapshot.CurrentNodeId, 0, 0);
Assert(fanoutProjection.Root.Children.Count == 3 && fanoutProjection.BranchCount == 8,
    "Mini projection must expose a three-branch window without losing the total count.");
Assert(fanoutProjection.VisibleBranchNodeIds.SequenceEqual(
        fanoutRecord.Root.Children.Values.Take(3).Select(node => node.NodeId)),
    "Branch ordering must remain stable in first-explored order.");
MiniTimelineProjection shiftedProjection = MiniTimelineProjector.Create(
    decisionSnapshot, decisionSnapshot.CurrentNodeId, 3, 0);
Assert(shiftedProjection.BranchWindowStart == 1 && shiftedProjection.SelectedBranchIndex == 3,
    "Selecting branch four must slide the visible window from 1-3 to 2-4.");
Assert(shiftedProjection.Root.Children[^1].Rows[0].IsSelectedBranch,
    "The selected candidate must be marked independently from the focused node.");
MiniTimelineProjection insideWindowProjection = MiniTimelineProjector.Create(
    decisionSnapshot, decisionSnapshot.CurrentNodeId, 2, 0);
Assert(insideWindowProjection.BranchWindowStart == 0,
    "Moving inside branches 1-3 must not slide the window.");
MiniTimelineProjection rightBoundaryProjection = MiniTimelineProjector.Create(
    decisionSnapshot, decisionSnapshot.CurrentNodeId, 99, 99);
Assert(rightBoundaryProjection.SelectedBranchIndex == 7
    && rightBoundaryProjection.BranchWindowStart == 5,
    "Branch selection and its three-item window must clamp at the right boundary.");

var twoBranchRecord = Record("projection-two-branches");
foreach (TimelineAction action in fanoutActions.Take(2))
    new TimelineTree(twoBranchRecord).Append(action, state, DateTimeOffset.UtcNow);
TimelineSnapshot twoBranchSnapshot = new TimelineTree(twoBranchRecord).Snapshot();
MiniTimelineProjection twoBranchProjection = MiniTimelineProjector.Create(
    twoBranchSnapshot, twoBranchSnapshot.CurrentNodeId, 0, 0);
CompactTimelineLayoutResult twoBranchLayout = CompactTimelineLayout.Create(
    twoBranchProjection.Root, _ => 160, _ => 160);
float focusCenter = twoBranchLayout.Items.Single(item => item.Row?.IsFocused == true).X + 80;
float[] branchCenters = twoBranchLayout.Items.Where(item => item.Row?.IsBranchFirstStep == true)
    .Select(item => item.X + 80).Order().ToArray();
Assert(branchCenters.Length == 2 && branchCenters[0] < focusCenter && branchCenters[1] > focusCenter,
    "Two branches must occupy opposite sides of the focused node.");
CompactTimelineItem[] twoBranchItems = twoBranchLayout.Items
    .Where(item => item.Row?.IsBranchFirstStep == true).ToArray();
Assert(twoBranchItems.Max(item => item.X + item.Width) - twoBranchItems.Min(item => item.X) >= 520,
    "Two branches should use the available horizontal space instead of clustering centrally.");

CompactTimelineLayoutResult compactFanout = CompactTimelineLayout.Create(
    fanoutProjection.Root, _ => 180, _ => 180);
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
    "A three-way preview must use exactly the left, center, and right fanout lanes.");
float[] threeBranchTops = compactItems.Where(item => item.Row?.IsBranchFirstStep == true)
    .Select(item => item.Y).ToArray();
Assert(threeBranchTops.Length == 3 && threeBranchTops.Distinct().Count() == 1,
    "All three immediate branches must start on the same horizontal level.");
float[] threeBranchCenters = compactItems.Where(item => item.Row?.IsBranchFirstStep == true)
    .Select(item => item.X + item.Width / 2).Order().ToArray();
float threeFocusCenter = compactItems.Single(item => item.Row?.IsFocused == true).X + 90;
Assert(threeBranchCenters[0] < threeFocusCenter
    && Math.Abs(threeBranchCenters[1] - threeFocusCenter) < 0.1f
    && threeBranchCenters[2] > threeFocusCenter,
    "Three branches must form a true left-center-right split around the focus.");
int projectedItems = FlattenMini(fanoutProjection.Root).Sum(segment => segment.Rows.Count
    + (segment.HiddenBranchCount > 0 ? 1 : 0));
Assert(compactItems.Length == projectedItems,
    "Compact layout must preserve every projected action, omission, and hidden-branch prompt.");
CompactTimelineLayoutResult variableHeightLayout = CompactTimelineLayout.Create(
    shiftedProjection.Root, _ => 160, _ => 160,
    row => row.Node?.Action == null ? CompactTimelineLayout.ItemHeight : 72);
Assert(variableHeightLayout.Items.Where(item => item.Row?.Node?.Action != null)
        .All(item => item.Height == 72),
    "Compact layout must retain the expanded height of fully wrapped immediate actions.");
CompactTimelineItem[] variableItems = variableHeightLayout.Items.ToArray();
for (int left = 0; left < variableItems.Length; left++)
for (int right = left + 1; right < variableItems.Length; right++)
{
    CompactTimelineItem a = variableItems[left], b = variableItems[right];
    bool overlaps = a.X < b.X + b.Width && a.X + a.Width > b.X
        && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    Assert(!overlaps, $"Variable-height compact items must not overlap: {a.Id} and {b.Id}.");
}

LargeWindowBounds hdWindow = LargeWindowGeometry.Default(1920, 1080);
Assert(Math.Abs(hdWindow.Width - 1163.25f) < 0.1f && Math.Abs(hdWindow.Height - 578f) < 0.1f
    && hdWindow.X == 24 && Math.Abs(hdWindow.Y - 166.5f) < 0.1f,
    "The large timeline window must use the approved logical viewport preset captured on the 1440p display.");
LargeWindowBounds qhdWindow = LargeWindowGeometry.Default(2560, 1440);
Assert(Math.Abs(qhdWindow.Width / 2560 - LargeWindowGeometry.WidthRatio) < 0.001f
    && Math.Abs(qhdWindow.Height / 1440 - LargeWindowGeometry.HeightRatio) < 0.001f,
    "The large timeline window must scale responsively at 2560x1440.");
Assert(!LargeWindowGeometry.IsReasonableSavedLayout(
        new LargeWindowBounds(378, 24, 1163.25f, 1032), 1920, 1080),
    "An initialization-corrupted near-fullscreen layout must be rejected.");
Assert(LargeWindowGeometry.IsReasonableSavedLayout(hdWindow, 1920, 1080),
    "The approved large-window preset must remain restorable.");
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
string layoutPath = Path.Combine(directory, "ui-layout-v1.json");
var layoutStore = new UiLayoutStore(layoutPath);
layoutStore.Save(new LargeWindowBounds(140, 95, 1180, 690), 1920, 1080);
SavedLargeWindowLayout savedLayout = layoutStore.Load()
    ?? throw new InvalidOperationException("Saved UI layout must round-trip.");
Assert(savedLayout.X == 140 && savedLayout.Y == 95
    && savedLayout.Width == 1180 && savedLayout.Height == 690
    && savedLayout.ViewportWidth == 1920 && savedLayout.ViewportHeight == 1080,
    "The manually adjusted large-window position and size must persist exactly.");
LargeWindowBounds scaledLayout = savedLayout.Resolve(2560, 1440);
Assert(Math.Abs(scaledLayout.X - 140f / 1920f * 2560f) < 0.01f
    && Math.Abs(scaledLayout.Y - 95f / 1080f * 1440f) < 0.01f
    && Math.Abs(scaledLayout.Width - 1180f / 1920f * 2560f) < 0.01f
    && Math.Abs(scaledLayout.Height - 690f / 1080f * 1440f) < 0.01f,
    "A saved layout must preserve its visual proportions across resolutions.");
string legacyLayoutPath = Path.Combine(directory, "legacy-ui-layout.json");
File.WriteAllText(legacyLayoutPath,
    """{"schemaVersion":1,"x":192,"y":135,"width":1536,"height":810,"viewportWidth":1920,"viewportHeight":1080,"updatedAt":"2026-09-17T00:00:00Z"}""");
SavedLargeWindowLayout migratedLayout = new UiLayoutStore(legacyLayoutPath).Load()
    ?? throw new InvalidOperationException("Version 1 UI layout must migrate.");
LargeWindowBounds migratedAtQhd = migratedLayout.Resolve(2560, 1440);
Assert(migratedLayout.SchemaVersion == SavedLargeWindowLayout.CurrentSchemaVersion
    && Math.Abs(migratedAtQhd.X - 256) < 0.01f && Math.Abs(migratedAtQhd.Y - 180) < 0.01f
    && Math.Abs(migratedAtQhd.Width - 2048) < 0.01f && Math.Abs(migratedAtQhd.Height - 1080) < 0.01f,
    "Version 1 pixel layouts must migrate to proportional coordinates.");

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
