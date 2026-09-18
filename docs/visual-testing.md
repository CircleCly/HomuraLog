# Visual smoke testing

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-visual-smoke.ps1` while Slay the Spire 2 is closed. The explicit process-only execution-policy override is needed on machines that disable local PowerShell scripts. The script builds and deploys the mod, launches the installed game with `--homuralog-visual-smoke`, loads the current run save through the existing smoke path, and waits for the mod to capture up to twelve full-viewport PNGs.

For a real English-localization pass, run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-visual-language.ps1 -Language eng`. This wrapper selects the most recently used Steam `settings.save`, copies it byte-for-byte to a unique temporary backup, changes only its language property, runs the screenshot suite, stops the game, restores the original bytes in `finally`, and verifies the restored SHA-256 hash. It refuses to run while the game is already open.

For a real window-size pass, run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-visual-resolution.ps1 -Width 1600 -Height 900`. The visual hook changes the live Godot window after game startup because both the game's persisted fullscreen setting and ordinary command-line `--resolution` can be overridden. The wrapper still backs up and restores `settings.save` byte-for-byte in case the game observes the temporary resize.

Screenshots are written under `%APPDATA%\SlayTheSpire2\HomuraLog\visual-smoke\<timestamp>`:

1. compact view focused on the player's current node;
2. compact view focused on a different explored branch;
3. the compact node-details window for that explored branch, including its jump action;
4. compact view focused on the explored node with the most immediate branches, when one exists;
5. the compact branch window shifted to include and select the last branch, when more than three exist;
6. large view opened with the same shared focus;
7. large view after Reset View returns focus to the current node;
8. compact view after the same reset, proving cross-view synchronization.
9. the native draw-pile screen, which must not contain HomuraLog windows;
10. the native discard-pile screen, which must not contain HomuraLog windows;
11. the native map screen, which must not contain HomuraLog windows;
12. the native pause screen, which must not contain HomuraLog windows.

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
- The compact details window remains readable and its jump target matches the focused node.

Run the suite once in Simplified Chinese and once in English. For resolution coverage, change the game's resolution in its settings before each run; the game's fullscreen settings override command-line `--resolution`, so command-line size arguments are not valid evidence. Screenshots preserve the actual rendered viewport size. Keep at least one save at a decision point with three or more explored branches for fan-out coverage.

This is a visual smoke suite with keyboard-driven native-screen coverage, not full pointer automation. Manual checks are still required for pointer hit targets, drag/zoom feel, and jump/delete execution.
