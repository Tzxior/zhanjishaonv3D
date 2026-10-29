# v0.4.0 Implementation Notes — Input, Skill Ranges, Resource and Visual Pipeline

## Status and scope

This development branch contains a first implementation pass for:
- Hiding the action menu during move/attack/support/transport target selection.
- Distinguishing click from drag: left-drag pans the camera; right-drag orbits the camera.
- Cyan movement destinations and orange legal action-target tiles.
- Per-action `rangeConfigured`, `minRange`, `maxRange`, AP cost, damage type/multiplier, and ammo-consumption fields in `GadgetUnitProfiles.json`.
- Unit production money, labour, iron, aluminium, and AP cost fields populated from the latest user-provided CSV.
- A fully opaque abstract front-end backdrop so Main Menu, Level Select, Skin Select, Unit Codex and Skin Codex do not show the generated Test001 battlefield underneath.
- The local fixes for `tileInfoScrollPosition` and `victoryWhenAllHostileUnitsDefeated` are preserved in the updated `HexUnitDemo.cs`.

Important: this is a code update, not proof of a successful Unity build. The changes have not yet been compiled or Play-tested inside the user's Unity Editor. Test the steps below before treating this as a stable release.

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

Unit production prices are now stored in JSON and shown in the unit codex. The factory's production queue still uses its separate Inspector `productionProfiles` list; it is not yet auto-populated from these JSON cost fields.

## Unity files to copy/sync

The user's actual Unity project root is a separate folder: `D:\\GitHub\\zhanjishaonv3D_project`. The repository branch stores source files under `UnityScripts/`, so copy the current branch versions into the matching project locations:

- `UnityScripts/HexUnitDemo.cs` → `Assets/Scripts/HexUnitDemo.cs`
- `UnityScripts/HexBattlefieldDemo.cs` → `Assets/Scripts/HexBattlefieldDemo.cs`
- `UnityScripts/HexGameFlowController.cs` → `Assets/Scripts/HexGameFlowController.cs`
- `UnityScripts/GadgetUnitProfiles.json` → `Assets/Resources/GadgetUnitProfiles.json`

Do not place duplicate copies of the same C# classes under another folder in `Assets`; duplicate class definitions will cause compile errors. Keep `GadgetUnitProfiles.json` under `Resources` because the current loader uses `Resources.Load<TextAsset>("GadgetUnitProfiles")`.

## First Unity test pass

1. Wait for compilation and check that Console has no red errors.
2. Select a ready blue unit. Click Move: the large HUD/action panels should hide and reachable tiles should stay cyan. Click a reachable empty tile: the unit moves and the action menu returns.
3. Click an attack skill. The menu should hide; visible, legal enemy targets should turn orange. Escape or a plain right-click cancels target selection.
4. Test a `rockets` unit: distance 1–2 must be rejected; distance 3–6 is its current CSV range. The default 9×9 map may not start with rocket artillery, so add a temporary test spawn/profile.
5. Left-drag pans and right-drag rotates the view. A drag must not also select a tile/unit. Starting a drag over HUD panels must not move the camera.
6. Test ammunition: a skill with `consumesAmmo=true` must require and consume ammo; a skill with `consumesAmmo=false` must remain usable when a unit's ammo is empty.
7. Confirm the main menu and other front-end screens show an opaque abstract navy background, not the Test001 battlefield. Pause, result and in-battle settings should still show the battlefield.
8. Verify Pause, Resume, Surrender, restart, AP timing, experience, building capture and factory production.

The factory queue still reads its own Inspector list; JSON production costs are not automatically wired into production until that integration step is implemented.

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

The current map is a flat hex mesh colored by terrain and buildings are colored cylinders with text. The next visual pass should prioritize **silhouette + surface pattern + icon + ownership mark**, not color alone.

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
- Arsenal: ammunition crates / warehouse.
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
