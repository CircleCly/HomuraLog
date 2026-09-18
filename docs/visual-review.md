# Visual review findings

Evidence is stored under `%APPDATA%\SlayTheSpire2\HomuraLog\visual-smoke`. The 20260917–20260918 runs cover Simplified Chinese and English at 2560×1440, plus live 1600×900 and 1280×720 captures.

## Confirmed functional

- Compact and large views retain the same exact focused node.
- Reset View returns both views to the player's current node.
- A four-branch decision renders as a three-column fan-out and shifts from branches 1–3 to 2–4 without reordering.
- The selected branch, focused node, and actual player node retain separate visual states.
- Compact details identify the action, hand position, visit count, combat state, localized enemy name, result, and jump action.
- Chinese and English chrome, card names, enemy names, and ongoing outcome labels refresh with the game language.
- All tested windows remain inside 2560×1440, 1600×900, and 1280×720 captures.
- HomuraLog windows are absent from the native draw-pile, discard-pile, map, and pause screens. The smoke harness confirms the expected native screen type and waits for its transition to settle before capturing.
- Hiding is scoped to HomuraLog: unrelated mod overlays remain visible on the tested native screens.
- Injected Godot pointer events successfully hit compact branch arrows, compact wheel zoom/drag, a large-view node row, and large-view wheel zoom/drag; each test also verifies the resulting focus, branch, zoom, or pan state.
- Compact and large jump buttons and the large delete button deliver the exact selected node ID through real pointer hits. The smoke harness suppresses the final mutation; tree deletion and replay path selection remain separately covered by core tests.
- The backed-up destructive pass executed a real forward replay from the compact jump button, reached the exact child node, deleted a real off-path subtree through the large-view button, then restored the user's timeline and native run saves.
- Localized enemy intent is converted to plain text before entering ordinary labels, so Godot tags such as `[font_size]` no longer leak into node details.

## Usability issues found

- At 1280×720, compact branch text is technically readable but near the comfortable size limit.
- When content continues below the compact viewport, there is no edge fade, scrollbar, or persistent cue that the canvas can be dragged.
- Focus gold and current-position orange are close in hue; without prior explanation, their different meanings are not self-evident.
- Opening the large view leaves the compact window visible, duplicating information and consuming additional screen space.
- Disabled jump/delete buttons do not explain why the action is unavailable.
- The large graph exposes several unlabeled built-in GraphEdit toolbar icons.
- Recorded intent variables can wrap poorly (`Strategic + Strategic` followed by `5` on a separate line) and remain less understandable than the game's native intent presentation.
- Other mods can draw transient hints across HomuraLog windows. HomuraLog stays functional, but the combined screen can be visually noisy.
- Zoomed or dragged compact graph content can move underneath the fixed branch navigator; the controls remain functional, but the overlap makes both layers harder to read.

## Hardware verification limit

- This machine exposes one physical 2560×1440 display plus virtual display adapters without usable native dimensions. A second physical display/Windows UI-scale configuration cannot be verified here; the 1600×900 and 1280×720 passes resize the live Godot viewport and must not be represented as that hardware test.
