# v0.4.0 Implementation Notes — Input, Skill Ranges, Resource and Visual Pipeline

## Status and scope

This development branch contains a first implementation pass for:
- Hiding the action menu during move/attack/support/transport target selection.
- Distinguishing click from drag: left-drag pans the camera; right-drag orbits the camera.
- Cyan movement destinations and orange legal action-target tiles.
- Per-action `rangeConfigured`, `minRange`, `maxRange`, AP cost, damage type/multiplier, and ammo-consumption fields in `GadgetUnitProfiles.json`.
- Unit production money, labour, iron, aluminium, and AP cost fields populated from the latest user-provided CSV.
- A fully opaque abstract front-end backdrop so Main Menu, Level Select, Skin Select, Unit Codex and Skin Codex do not show the generated Test001 battlefield underneath.
- Player faction selection on the post-level skin selection screen, defaulting to red and saved with PlayerPrefs. Blue, green, yellow, purple and black palettes are also included. Team IDs remain team 0 = player and team 1 = opponent, so faction color changes do not swap combat logic.
- Faction identity is displayed through colored unit ground rings, city ownership tint and building base/banner markers. Faction definitions also expose future stat multipliers (attack, HP, defenses, movement and production cost), all neutral by default.
- Generated terrain visuals now add raised trees, hills, rocks, water ripples, road markings, city blocks and short terrain-code icons. These are visual-only and their colliders are disabled so existing tile raycasts/movement rules continue to use the original hex tile.
- Buildings now use different primitive silhouettes for HQ, city, village, barracks, factories, shipyard, airport and mines, with a separate faction-colored base and flag banner.
- Building labels are billboarded toward the camera with corrected front-side orientation and half-size display.
- Road tiles automatically draw connected road arms based on all six neighboring hexes, including 3-way, 4-way and 6-way junctions; the prototype map now includes a third diagonal road corridor.
- Camera tilt is constrained to a 30–60 degree pitch range, including older serialized Inspector values.
- Ready player units receive cyan pulsing hex outlines, the selected unit gets a gold outline, and the currently acting enemy gets an orange outline when visible. Damaged unit sprites flash red for about 0.48 seconds.
- Unit sprite base-height was lowered to 0.045 world units; the setting was renamed so older serialized scene values do not override the new offset.
- The five production building types now auto-generate production choices from `GadgetUnitProfiles.json` when the Inspector production list is empty. An occupied factory hides production choices; matching friendly units are automatically restored every 10 AP using the existing +10 HP / +1 ammo / +2 fuel rule.
- The `Arsenal` building is now displayed as `兵营` (the serialized enum value remains unchanged for compatibility).
- The local fixes for `tileInfoScrollPosition` and `victoryWhenAllHostileUnitsDefeated` are preserved in the updated `HexUnitDemo.cs`.

Important: this is a code update, not proof of a successful Unity build. The changes have not yet been compiled or Play-tested inside the user's Unity Editor. Test the steps below before treating this as a stable release.

## Faction and visual system

Player team ID remains 0 and opponent team ID remains 1. This is deliberate: changing faction colors must not change AI scheduling, capture rules, or win/loss checks. The current default is red for the player and blue for the opponent. The selected player faction ID is saved to `PlayerPrefs`; the selector lives on the screen reached after choosing a level. Faction definitions carry color plus neutral-by-default multipliers for attack, HP, bullet/explosion defense, movement and production cost. Player modifiers combine with the selected skin modifiers; opponent modifiers are configured independently through `opponentFactionId`. No faction has a special active/passive ability configured yet, and all default multipliers are 1.

The current terrain/building artwork is generated from simple Unity primitives as a readability pass, not final authored models or prefab assets. Terrain decorators disable their colliders; tile terrain IDs remain the source of truth for movement, defense and traversal. Terrain icons use short codes. Each building type has a distinct silhouette and independent faction-colored base/banner; taking over a building updates its own ownership markers. Unit ground rings and building labels follow movement/camera changes, and enemy rings are hidden when the enemy is not detected.

## Current range and production data

Schema version 7 is derived from the user's latest uploaded `单位信息(1).csv` and contains all 25 unit profiles. All ranges use hex distance and each non-empty action column is preserved in `actions[]` as `action_1`, `action_2`, or `action_3`.

Examples from that table:
- Sniper skill: 1–5.
- Self-propelled gun and anti-air: 1–4.
- Rocket launcher skill: 3–6.
- Fortress bombardment: 1–8; switching stance costs 100 AP.
- Building capture: 0–0; the action runs on the unit's own tile.
- Battleship skill: 1–6, even though the unit's default `attackRange` is 8.
- Each unit now has `productionMoneyCost`, `productionLabourRequired`, `productionIronRequired`, `productionAluminiumRequired`, and `productionAP`.

Action metadata also includes `actionAPCost`, `damageMultiplier`, `damageType`, and `consumesAmmo`. The attack UI reads the per-action range/cost/damage/ammo fields. Actions whose text doesn't specify a range use 1–the unit's default `射程`, except building capture and stance switching, which use 0–0.

Limitation: the current movement enum has no distinct Amphibious class, so the CSV `两栖` row for `transport_ship` maps to the existing Naval enum (3). Movement rules need a dedicated amphibious class in a later gameplay pass.

Unit production prices are stored in JSON and shown in the unit codex. If the Inspector `productionProfiles` array is empty, the five factory types now generate production choices from the JSON fields. A non-empty Inspector array remains an intentional override.

## Unity files to copy/sync

The user's actual Unity project root is a separate folder: `D:\\GitHub\\zhanjishaonv3D_project`. The repository branch stores source files under `UnityScripts/`, so copy the current branch versions into the matching project locations:

- `UnityScripts/HexUnitDemo.cs` → `Assets/Scripts/HexUnitDemo.cs`
- `UnityScripts/HexBattlefieldDemo.cs` → `Assets/Scripts/HexBattlefieldDemo.cs`
- `UnityScripts/HexGameFlowController.cs` → `Assets/Scripts/HexGameFlowController.cs`
- `UnityScripts/GadgetUnitProfiles.json` → `Assets/Resources/GadgetUnitProfiles.json`

Do not place duplicate copies of the same C# classes under another folder in `Assets`; duplicate class definitions will cause compile errors. Keep `GadgetUnitProfiles.json` under `Resources` because the current loader uses `Resources.Load<TextAsset>("GadgetUnitProfiles")`.

## First Unity test pass

1. Wait for compilation and check that Console has no red errors.
2. Confirm building labels are readable and half-size, and the camera cannot tilt beyond the 30–60 degree range.
3. Select a ready player unit. Click Move: the large HUD/action panels should hide and reachable tiles should stay cyan. Click a reachable empty tile: the unit moves and the action menu returns.
4. Click an attack skill. The menu should hide; visible, legal enemy targets should turn orange. Escape or a plain right-click cancels target selection.
5. Test a `rockets` unit: distance 1–2 must be rejected; distance 3–6 is its current CSV range. The default 9×9 map may not start with rocket artillery, so add a temporary test spawn/profile.
6. Left-drag pans and right-drag rotates the view. A drag must not also select a tile/unit. Starting a drag over HUD panels must not move the camera.
7. On the post-level skin selection screen, verify red is selected by default. Switch to green or purple, start Test001, and confirm player unit rings, city ownership tint and owned-building banners follow the chosen color while enemy markers stay blue. Restart and make sure the selected faction persists.
8. Walk through the map and compare tree, hill, mountain, water, road, dryland and city silhouettes/icons. Confirm connected road seams and 3-way/4-way/6-way junctions; clicking and movement should still be registered by the underlying tile, not decoration.
9. Confirm all ready player units have a pulsing cyan hex outline, the selected unit is gold, the acting visible enemy is orange, and damaged unit sprites flash red briefly.
10. Test a friendly unit on each matching owned production building: it should automatically recover +10 HP, +1 ammo, +2 fuel every 10 AP, and the factory UI should not show production choices while occupied. Empty owned factory tiles should show unit production choices from the JSON with money/labour/mineral requirements.
11. Test ammunition: a skill with `consumesAmmo=true` must require and consume ammo; a skill with `consumesAmmo=false` must remain usable when a unit's ammo is empty.
12. Confirm the main menu and other front-end screens show an opaque abstract navy background, not the Test001 battlefield. Pause, result and in-battle settings should still show the battlefield.
13. Verify Pause, Resume, Surrender, restart, AP timing, experience, building capture and factory production.

When `productionProfiles` is left empty, factory options are now generated from the JSON profile data. A non-empty Inspector list is intentionally treated as an override.

If any error appears, fix the first Console error before testing gameplay rules.

## Suggested Unity resource layout

Use this as the intended organization as art assets grow. Keep runtime-loaded files in the exact current `Resources` paths unless their loader is changed.

```text
Assets/
  Scripts/
    Core/            # HexBattlefieldDemo, HexUnitDemo and shared rules
    Economy/         # HexBuildingEconomy and production code
    UI/              # menus, panels, result screens
  Resources/
    GadgetUnitProfiles.json
    GadgetSprites/
      MU_00000/frames/
        down_right/frame_01.png ... frame_04.png
        down_left/...
        up_left/...
        up_right/...
  Art/
    Units/
      PixelSprites/   # imported sprites not loaded by string path
      Portraits/      # codex, recruitment, result and profile views
      Icons/          # small tactical icons
    Terrain/
      Prefabs/        # forest, mountain, hill, road, water details
      Materials/
    Buildings/
      Prefabs/        # HQ, cities, village, factory, mine, shipyard, airport
      Materials/
    UI/
      Icons/
      Panels/
    Effects/
    Audio/
  Scenes/
    MainMenu.unity
    Battle_Test001.unity
  GameData/
    Levels/           # ScriptableObject level definitions
    Factions/
    Buildings/
```

### What belongs where

- **Pixel unit sprites:** the current loader uses `Resources.Load<Sprite>`, so the four directional animation folders currently must remain under `Assets/Resources/GadgetSprites/... `. Each frame name should be stable (for example `frame_01.png`) and imported with Filter Mode = Point for pixel art.
- **Portraits and codex illustrations:** put under `Assets/Art/Units/Portraits/` and reference them from serialized assets or a catalogue. Do not put every unused portrait into `Resources`; Resources assets are built into the player and can increase runtime memory/build footprint.
- **UI and effects:** store in `Assets/Art/UI/` and `Assets/Art/Effects/`; use direct inspector references or a prefab/catalogue.
- **Levels:** store scenes in `Assets/Scenes/`. Store board dimensions, terrain IDs, building placements, starting units, factions and victory conditions in a `LevelDefinition` ScriptableObject under `Assets/GameData/Levels/`. For the prototype, the Test001 scene may still create its map procedurally; migrate once its layout becomes stable.
- **Audio:** put music and sound effects under `Assets/Art/Audio/` (or an `Assets/Audio/` folder if preferred) and reference them through an audio configuration asset.

Do not introduce Addressables until there is a real need for remote content, large optional asset packs, or asynchronous loading. For the immediate prototype, use `Resources` only for the two existing string-loaded data families and use regular references for the rest.

## Visual distinguishability plan

A first code-generated readability pass is now implemented using simple Unity primitives rather than final authored models. Terrain uses distinct silhouettes/surface marks (trees, mounds, rocks, water ripples, road strips, dryland scrub, paved city footprint) plus short terrain-code icons. Buildings use different silhouettes and a separate faction-colored base/banner; labels billboard toward the camera. The next visual pass should replace/augment these generated shapes with authored Prefabs, proper surface materials, selection outlines and dedicated illustrated icons, retaining the rule **silhouette + surface pattern + icon + ownership mark**, not color alone.

### Terrain identifiers
- Grassland: a mostly flat green tile with a subtle grass/brush pattern.
- Forest: 2–3 unmistakable low-poly trees per tile.
- Hill: a raised earth mound, visibly taller than grassland.
- Mountain: grey rock peaks with a darker edge/shadow.
- Road / MountainRoad: narrow road strip crossing the hex; mountain road adds rock shoulders.
- Water: deeper blue surface and pale shoreline/foam; naval-passable tiles should be obvious.
- Dryland: sand/soil patch and sparse scrub.
- City: paved surface, street/roof silhouettes and a distinct urban footprint.

The collision and movement logic must continue to use terrain IDs, not visual object names.

### Building identifiers
Use reusable prefabs with a common root/selection anchor and a separate owner banner:
- Headquarters: largest silhouette, command tower/radar/flag.
- Large city: several dense blocks; small city: a compact cluster of buildings.
- Village: two or three small houses.
- Barracks (serialized enum `Arsenal`): ammunition crates / warehouse.
- Tank factory: industrial shed with crane or tank silhouette.
- Artillery factory: factory plus artillery-barrel silhouette.
- Shipyard: water edge, dock and ship silhouette.
- Airport: runway markings and aircraft silhouette.
- Iron mine: dark ore pile / mining rig.
- Aluminium mine: lighter metallic ore pile / mining rig.

Ownership should be shown with a small faction banner or ring, while building type should be identified by shape and icon. Avoid recoloring the whole tile as the only ownership cue. Keep target highlights (orange) and movement highlights (cyan) visually separate from terrain colors.

### Recommended implementation order
1. Add clean terrain/building prefabs and icons to `Assets/Art`.
2. Add inspector references for those prefabs to the map/building configuration; keep generated-primitive fallbacks for missing art.
3. Replace the cylinder-only building marker with a type-specific prefab plus a billboard icon/name/owner banner.
4. Make terrain decoration visual-only; retain `TileInfo.terrain` as the source of movement, defense and traversability rules.
5. Add selection outline/hover state, then refine lighting, tile borders and terrain textures.
