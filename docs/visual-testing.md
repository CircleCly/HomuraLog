# Visual smoke testing

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-visual-smoke.ps1` while Slay the Spire 2 is closed. The explicit process-only execution-policy override is needed on machines that disable local PowerShell scripts. The script builds and deploys the mod, launches the installed game with `--homuralog-visual-smoke`, loads the current run save through the existing smoke path, and waits for the mod to capture five full-viewport PNGs.

Screenshots are written under `%APPDATA%\SlayTheSpire2\HomuraLog\visual-smoke\<timestamp>`:

1. compact view focused on the player's current node;
2. compact view focused on a different explored branch;
3. large view opened with the same shared focus;
4. large view after Reset View returns focus to the current node;
5. compact view after the same reset, proving cross-view synchronization.

The capture uses Godot's rendered viewport after `FramePostDraw`. It does not depend on desktop focus, screen coordinates, Steam screenshots, or an external capture tool. It therefore continues to work when the game window is occluded.

## Review checklist

- The gold focus outline identifies the same exact node in screenshots 2 and 3.
- Reset View identifies the player node in screenshots 4 and 5.
- The orange current-position marker remains distinct when browsing another node.
- The first step of every visible compact branch is fully readable.
- Nodes, arrows, navigation controls, and branch counts do not overlap or clip.
- Chinese and English labels contain no raw enum values or model IDs.
- The large window remains within the viewport and leaves game content visible around it.
- Jump and delete actions are visibly enabled only when valid.

Run the suite once in Simplified Chinese and once in English. For resolution coverage, change the game's resolution before each run; screenshots preserve the actual rendered viewport size. Keep at least one save at a decision point with three or more explored branches for fan-out coverage.

This is a visual smoke suite, not an input automation suite. Manual checks are still required for pointer hit targets, drag/zoom feel, jump execution, and modal suppression over draw/discard/map screens.
