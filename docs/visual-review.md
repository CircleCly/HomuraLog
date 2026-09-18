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

## Usability improvements verified

- Compact automatic framing now preserves a readable minimum zoom at 1280×720 and lets overflow remain pannable.
- Edge fades and a localized drag cue identify compact content outside the viewport.
- The compact navigator has an opaque safe area; panned graph content is masked and cannot be clicked through it.
- Focus uses a cyan-white double outline and diamond marker, while the actual player position retains its orange fill and arrow marker. Both states remain visible when they coincide.
- Opening the large graph temporarily hides the compact window; closing it restores the prior compact visibility state.
- Jump and delete buttons share availability rules and localized explanations for their disabled states.
- The built-in GraphEdit toolbar is hidden and replaced by localized zoom, minimap, and Reset View controls.
- Recorded intents render one structured intent per line instead of joining unrelated intent variables with plus signs.
- The large inspector uses a more solid backing so text remains legible over the combat scene.

## Remaining external limitation

- Other mods can still draw high-layer transient hints across HomuraLog windows. HomuraLog cannot safely reorder or hide third-party CanvasLayers; stronger local backplates reduce, but cannot eliminate, that visual noise.

## Hardware verification limit

- This machine exposes one physical 2560×1440 display plus virtual display adapters without usable native dimensions. A second physical display/Windows UI-scale configuration cannot be verified here; the 1600×900 and 1280×720 passes resize the live Godot viewport and must not be represented as that hardware test.
