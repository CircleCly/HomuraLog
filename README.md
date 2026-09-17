# HomuraLog

HomuraLog records the exact combat decisions you have already explored while save-loading in Slay the Spire 2.

The initial release targets the `public-beta` game build `0.111.0`, .NET 9 and STS2-RitsuLib. It is single-player only and never modifies the native run save.

## Build

1. Install the .NET 9 SDK, Slay the Spire 2 public beta and STS2-RitsuLib.
2. Copy `local.props.example` to `local.props` if your Steam library is not `E:/SteamLibrary`.
3. Run `dotnet build -c Release`. The DLL and manifest are copied to `mods/HomuraLog` by default.

In combat, use `Ctrl+H` to toggle the RitsuLib-styled timeline panel. Records are stored under the game's user-data directory in `HomuraLog/timelines-v1`. The panel and graph close automatically when combat ends.

## What is recorded

- Successfully completed card plays, including the concrete combat-card instance and target.
- Successfully used potions, their slot and target.
- End-turn actions and native card-choice results made while a player action is paused.
- A compact state summary after each decision. Reloading the same room starts at the existing tree root.

Cards, potions and the end-turn control receive a blue unexplored dot or green explored checkmark. The draggable side panel presents an expandable branch tree: the active path is highlighted, unrelated branches are collapsed by default, and selecting a node shows its HP, energy and enemy-state summary. Multiplayer combats are intentionally ignored.

The fullscreen graph lays decisions out from top to bottom with directional arrows, current-path highlighting, zoom, pan, minimap, node details and a true viewport-filling mode. Consecutive decisions with no branch are compacted into one tall visual segment, while every action remains an individually clickable replay target. Select an action row and confirm **Jump to this timeline** to reload the room-entry save and replay the recorded card, potion, end-turn and card-choice actions. Replay stops safely on the first mismatch instead of forcing an invalid game action.

Card and potion labels are resolved from their stable model IDs at render time, so existing timeline files immediately follow the game's current language without migration.
