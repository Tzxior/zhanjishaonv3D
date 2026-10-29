using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Prototype release: v0.3.0 — battle flow, persistent skins, buildings and production hooks.
public enum HexAttackType { Bullet, Explosion }

/// <summary>
/// Unit selection, terrain-cost movement, optional movement followed by a required non-movement action,
/// discrete AP scheduling, simple attacks, city capture, and basic enemy AI.
/// Simulation AP never advances with wall-clock time. WaitForSecondsRealtime is used only for presentation.
/// </summary>
public class HexUnitDemo : MonoBehaviour
{
    public float CurrentBattleAP => battleTimeAP;
    public event Action<string> SkinChestDefeated;
    [Header("Starting Positions")]
    public Vector2Int[] blueStartingPositions = { new Vector2Int(1, 1), new Vector2Int(2, 3) };
    public Vector2Int[] redStartingPositions = { new Vector2Int(7, 6), new Vector2Int(7, 7) };

    [System.Serializable] public class UnitBattleProfile
    {
        public string id = "infantry";
        public string unitName = "步兵";
        // Data category (five-way UI/design grouping) is separate from unitClass (movement/terrain rules).
        public string unitCategory = "infantry";
        public HexUnitClass unitClass = HexUnitClass.Infantry;
        [Min(0)] public int ammoCapacity = 0;
        [Min(0)] public int fuelCapacity = 0;
        [TextArea(2, 4)] public string description = "";
        // Stable identifiers for unit abilities; individual effects are implemented in later gameplay steps.
        public string[] specialAbilities = new string[0];
        [Min(-1)] public int reconRange = -1;
        [Range(-1, 100)] public int concealment = -1;
        public bool canDetectSpy = false;
        [Min(-1)] public int resourceCapacity = -1;
        public string[] dataGaps = new string[0];
        public UnitActionProfile[] actions = new UnitActionProfile[0];
        [Min(1)] public int maxHP=100;
        [Min(0)] public int attackPower=20;
        public HexAttackType attackType=HexAttackType.Bullet;
        [Min(0)] public int bulletDefense=10;
        [Min(0)] public int explosionDefense=8;
        [Min(1)] public int movementPower=6;
        [Min(1)] public int movementAPLimit=100;
        [Min(0)] public int attackAP=100;
        [Min(0)] public int captureAP=50;
        [Min(0)] public int restAP=0;
        [Min(1)] public int attackRange=1;
        public bool canCapture=false;
    }
    [System.Serializable] public class UnitActionProfile
    {
        public string id = "";
        public string description = "";
        public bool rangeConfigured;
        [Min(0)] public int minRange;
        [Min(0)] public int maxRange;
    }

    [Header("Individual Unit Profiles (array order matches starting positions)")]
    [Tooltip("每个部署单位独立配置；单位详细资料优先从 GadgetUnitProfiles.json 读取。")]
    public UnitBattleProfile[] blueUnitProfiles =
    {
        new UnitBattleProfile { unitName="步兵", unitClass=HexUnitClass.Infantry, maxHP=100, attackPower=20, attackType=HexAttackType.Bullet, bulletDefense=10, explosionDefense=8, movementPower=6, movementAPLimit=100, attackAP=100, captureAP=50, restAP=0, attackRange=1 },
        new UnitBattleProfile { unitName="重型地面单位", unitClass=HexUnitClass.HeavyGround, maxHP=140, attackPower=30, attackType=HexAttackType.Explosion, bulletDefense=20, explosionDefense=15, movementPower=4, movementAPLimit=100, attackAP=120, captureAP=60, restAP=0, attackRange=1 }
    };
    public UnitBattleProfile[] redUnitProfiles =
    {
        new UnitBattleProfile { id="infantry", unitName="敌方步兵", unitClass=HexUnitClass.Infantry, maxHP=100, attackPower=20, attackType=HexAttackType.Bullet, bulletDefense=10, explosionDefense=8, movementPower=6, movementAPLimit=100, attackAP=100, captureAP=50, restAP=0, attackRange=1 },
        new UnitBattleProfile { id="heavy_infantry", unitName="敌方重装步兵", unitClass=HexUnitClass.HeavyGround, maxHP=140, attackPower=30, attackType=HexAttackType.Explosion, bulletDefense=20, explosionDefense=15, movementPower=4, movementAPLimit=100, attackAP=120, captureAP=60, restAP=0, attackRange=1 }
    };

    [Header("Unit Data File")]
    [Tooltip("放在 Assets/Resources/GadgetUnitProfiles.json；不存在时使用 Inspector 内的备用配置。")]
    public string unitDatabaseResourcePath = "GadgetUnitProfiles";
    public string[] blueStartingProfileIds = { "infantry", "heavy_infantry" };
    public string[] redStartingProfileIds = { "infantry", "heavy_infantry" };

    [Header("Sprite Resources")]
    [Tooltip("Resources path ending with the down_right folder.")]
    public string blueFramesResourcePath = "GadgetSprites/MU_00000/frames/down_right";
    [Tooltip("Empty uses a temporary red placeholder sprite.")]
    public string redFramesResourcePath = "";

    [Header("Animation & Appearance")]
    [Min(1f)] public float pixelsPerUnit = 48f;
    [Min(0.1f)] public float animationFramesPerSecond = 6f;
    [Min(0.1f)] public float iconScale = 1.6f;
    [Min(0f)] public float liftAboveTile = 0.12f;
    [Min(1f)] public float selectedScaleMultiplier = 1.2f;
    [Tooltip("Enemy action display delay only. It never changes simulated AP.")]
    [Min(0f)] public float enemyActionDisplayDelay = 0.25f;

    [Header("Enemy AI")]
    [Min(1)] public int blockedEnemyWaitAP = 25;

    [Header("Simulation UI")]
    public bool showBattleHUD = true;
    public bool victoryWhenAllHostileUnitsDefeated = true;

    private static readonly string[] Directions = { "down_right", "down_left", "up_left", "up_right" };
    private static readonly Vector2Int[] Neighbors =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0),
        new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, -1), new Vector2Int(-1, 1)
    };

    private class UnitInfo
    {
        public int q, r, team;
        public HexUnitClass unitClass;
        public Transform visual;
        public SpriteRenderer renderer;
        public Dictionary<string, Sprite[]> directionalFrames;
        public string currentDirection = "down_right";
        public Vector3 baseScale;
        public float animationOffset;
        public int lastFrame = -1;
        public float nextReadyTime;
        public float zeroCostActedAtTime = -1f;

        // Movement is optional and does not finish this activation.
        public bool hasMovedThisActivation;
        public int pendingMoveAP;

        public string unitId = "infantry";
        public string unitName;
        public string unitCategory = "infantry";
        public int ammoCapacity, fuelCapacity;
        public string description;
        public string[] specialAbilities;
        public string[] dataGaps;
        public UnitActionProfile[] actions;
        public int currentAmmo, currentFuel;
        public UnitInfo carrier;
        public readonly List<UnitInfo> cargo = new List<UnitInfo>();
        public bool fortressGarrisoned;
        public int reconRange=-1, concealment=-1, resourceCapacity=-1;
        public bool canDetectSpy;
        public HexAttackType attackType;
        public int hp=100, maxHp=100, attack=20, baseAttackPower=20;
        public int experience, rank;
        public int bulletDefense=10, explosionDefense=8;
        public int movementPower=6, movementAPLimit=100;
        public int attackAP=100, captureAP=50, restAP=0, attackRange=1;
        public bool canCapture;
        public bool isSkinChest;
        public string rewardSkinId;
    }

    private class PathNode
    {
        public int cost;
        public int movementPoints;
        public int steps;
        public Vector2Int previous;
    }

    [System.Serializable]
    private class UnitProfileDatabase
    {
        public UnitBattleProfile[] units;
    }

    private readonly Dictionary<string, UnitBattleProfile> profilesById =
        new Dictionary<string, UnitBattleProfile>();

    private readonly List<UnitInfo> units = new List<UnitInfo>();
    private readonly HashSet<Vector2Int> reachableTiles = new HashSet<Vector2Int>();
    private readonly Dictionary<Vector2Int, PathNode> currentPaths = new Dictionary<Vector2Int, PathNode>();

    private UnitInfo selectedUnit;
    private Camera mapCamera;
    private Dictionary<string, Sprite[]> blueAnimations;
    private Dictionary<string, Sprite[]> redAnimations;
    private HexBuildingEconomy buildingEconomy;
    private HexGameFlowController gameFlowController;
    private bool gameplayPaused = true;
    private bool battleEnded;
    private float skinAttackMultiplier = 1f;
    private float skinHPMultiplier = 1f;
    private float skinBulletDefenseMultiplier = 1f;
    private float skinExplosionDefenseMultiplier = 1f;
    private float skinMovementMultiplier = 1f;
    private float skinProductionCostMultiplier = 1f;
    private HexBattlefieldDemo battlefield;
    private float battleTimeAP;
    private bool simulationRunning;
    private bool moveMode = true;
    private bool attackMode;
    private bool supportMode;
    private string supportAction = "";
    private string selectedAttackName = "攻击";
    private float selectedAttackMultiplier = 1f;
    private HexAttackType selectedAttackType = HexAttackType.Bullet;
    private int selectedAttackCost = 100;
    private bool selectedAttackNavalOnly, selectedAttackAirOnly, selectedAttackTrueDamage;
    private bool transportMode;
    private bool hasSelectedTile;
    private int selectedTileQ;
    private int selectedTileR;
    private string statusMessage = "选择我方单位开始行动。";
    private Vector2 actionMenuScreenPosition = new Vector2(420f, 100f);
    private Vector2 actionMenuScrollPosition;
    private Vector2 unitInfoScrollPosition;
    private Vector2 tileInfoScrollPosition;
    private bool actionMenuHiddenForTargetSelection;
    private int selectedAttackMinRange = 1;
    private int selectedAttackMaxRange = 1;
    private bool leftPointerPressed, leftPointerStartedOverHud, leftPointerDragged;
    private Vector2 leftPointerStart, lastLeftPointerPosition;
    private bool rightPointerPressed, rightPointerStartedOverHud, rightPointerDragged;
    private Vector2 rightPointerStart, lastRightPointerPosition;

    private void Start()
    {
        battlefield = GetComponent<HexBattlefieldDemo>();
        if (battlefield == null)
        {
            Debug.LogError("HexUnitDemo 必须与 HexBattlefieldDemo 挂在同一个 GameObject 上。");
            enabled = false;
            return;
        }

        mapCamera = Camera.main;
        blueAnimations = LoadDirectionalFrames(blueFramesResourcePath, "我方",
            new Color32(70, 155, 255, 255), new Color32(235, 245, 255, 255));
        redAnimations = LoadDirectionalFrames(redFramesResourcePath, "敌方",
            new Color32(235, 75, 70, 255), new Color32(255, 225, 190, 255));

        LoadUnitProfileDatabase();
        SpawnStartingUnits(blueStartingPositions, blueUnitProfiles, blueStartingProfileIds, 0, blueAnimations);
        SpawnStartingUnits(redStartingPositions, redUnitProfiles, redStartingProfileIds, 1, redAnimations);
        InitializeUniqueActionNodes();

        buildingEconomy = GetComponent<HexBuildingEconomy>();
        if (buildingEconomy == null) buildingEconomy = gameObject.AddComponent<HexBuildingEconomy>();
        buildingEconomy.InitializeForBattlefield(battlefield, this);

        gameFlowController = GetComponent<HexGameFlowController>();
        if (gameFlowController == null) gameFlowController = gameObject.AddComponent<HexGameFlowController>();
        statusMessage = "请从主菜单选择关卡。";
    }

    public void SetGameplayPaused(bool paused)
    {
        if (battleEnded && !paused) return;
        gameplayPaused = paused;
        if (!paused && !simulationRunning && !HasReadyBlueUnit())
            StartCoroutine(RunSimulationUntilPlayerCanAct());
    }

    public void ConfigureSkinModifiers(float attack, float hp, float bulletDefense,
        float explosionDefense, float movement, float productionCost)
    {
        skinAttackMultiplier = Mathf.Max(0.01f, attack);
        skinHPMultiplier = Mathf.Max(0.01f, hp);
        skinBulletDefenseMultiplier = Mathf.Max(0.01f, bulletDefense);
        skinExplosionDefenseMultiplier = Mathf.Max(0.01f, explosionDefense);
        skinMovementMultiplier = Mathf.Max(0.01f, movement);
        skinProductionCostMultiplier = Mathf.Max(0.01f, productionCost);
    }

    public float GetSelectedSkinProductionCostMultiplier() => skinProductionCostMultiplier;

    private void ApplyActiveSkin(UnitInfo unit)
    {
        if (unit == null || unit.team != 0) return;
        unit.maxHp = Mathf.Max(1, Mathf.RoundToInt(unit.maxHp * skinHPMultiplier));
        unit.hp = unit.maxHp;
        unit.baseAttackPower = Mathf.Max(0, Mathf.RoundToInt(unit.baseAttackPower * skinAttackMultiplier));
        unit.attack = unit.baseAttackPower;
        unit.bulletDefense = Mathf.Max(0, Mathf.RoundToInt(unit.bulletDefense * skinBulletDefenseMultiplier));
        unit.explosionDefense = Mathf.Max(0, Mathf.RoundToInt(unit.explosionDefense * skinExplosionDefenseMultiplier));
        unit.movementPower = Mathf.Max(1, Mathf.RoundToInt(unit.movementPower * skinMovementMultiplier));
    }

    public void ResetForNewMatch()
    {
        StopAllCoroutines();
        simulationRunning = false;
        battleEnded = false;
        gameplayPaused = true;
        foreach (UnitInfo unit in units)
            if (unit != null && unit.visual != null) Destroy(unit.visual.gameObject);
        units.Clear();
        selectedUnit = null;
        hasSelectedTile = false;
        moveMode = attackMode = supportMode = transportMode = false;
        supportAction = "";
        battleTimeAP = 0f;
        statusMessage = "新一局已初始化。";
        if (battlefield != null) battlefield.SetMovementHighlights(null);
        if (buildingEconomy != null) buildingEconomy.ResetForNewBattle(this);
        SpawnStartingUnits(blueStartingPositions, blueUnitProfiles, blueStartingProfileIds, 0, blueAnimations);
        SpawnStartingUnits(redStartingPositions, redUnitProfiles, redStartingProfileIds, 1, redAnimations);
        InitializeUniqueActionNodes();
    }

    public bool HasUnitProfile(string unitId)
    {
        return !string.IsNullOrWhiteSpace(unitId) && profilesById.ContainsKey(unitId);
    }

    public string[] GetUnitCodexEntries()
    {
        var entries = new List<string>();
        var ids = new List<string>(profilesById.Keys);
        ids.Sort(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            UnitBattleProfile p = profilesById[id];
            if (p == null) continue;
            entries.Add($"{p.unitName} [{p.id}]\n分类：{CategoryName(p.unitCategory)}\n生命 {p.maxHP}　攻击 {p.attackPower}　子弹防御 {p.bulletDefense}　爆炸防御 {p.explosionDefense}\n移动力 {p.movementPower}　范围 {p.attackRange}　攻击 AP {p.attackAP}　弹药 {p.ammoCapacity}　燃料 {p.fuelCapacity}\n{p.description}");
        }
        return entries.ToArray();
    }

    public bool TrySpawnProducedUnit(string unitId, int team, int originQ, int originR)
    {
        if (!HasUnitProfile(unitId)) return false;
        UnitBattleProfile profile = profilesById[unitId];
        var candidates = new List<Vector2Int> { new Vector2Int(originQ, originR) };
        foreach (Vector2Int offset in Neighbors)
            candidates.Add(new Vector2Int(originQ + offset.x, originR + offset.y));
        foreach (Vector2Int coordinate in candidates)
        {
            if (!battlefield.IsInsideMap(coordinate.x, coordinate.y) ||
                FindUnitAt(coordinate.x, coordinate.y) != null ||
                !battlefield.IsTileWalkable(coordinate.x, coordinate.y, profile.unitClass))
                continue;
            Dictionary<string, Sprite[]> animation = team == 0 ? blueAnimations : redAnimations;
            SpawnStartingUnits(new[] { coordinate }, null, new[] { unitId }, team, animation);
            UnitInfo produced = FindUnitAt(coordinate.x, coordinate.y);
            if (produced == null) return false;
            ReserveNextActionNode(produced, battleTimeAP + 1f);
            return true;
        }
        return false;
    }

    public void SpawnSkinRewardChest(Vector2Int coordinate, string skinId, string displayName, int maxHP)
    {
        if (!battlefield.IsInsideMap(coordinate.x, coordinate.y) ||
            FindUnitAt(coordinate.x, coordinate.y) != null)
        {
            Debug.LogWarning($"Skin chest at {coordinate} overlaps a unit or is outside the map.");
            return;
        }

        Sprite sprite = CreatePixelUnitSprite(new Color32(226, 174, 53, 255), new Color32(255, 233, 122, 255));
        var frames = new Dictionary<string, Sprite[]>();
        foreach (string direction in Directions) frames[direction] = new[] { sprite, sprite, sprite, sprite };
        GameObject icon = new GameObject("SkinRewardChest");
        icon.transform.SetParent(transform, false);
        icon.transform.localPosition = HexToLocalPosition(coordinate.x, coordinate.y);
        icon.transform.localScale = Vector3.one * iconScale;
        SpriteRenderer renderer = icon.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = 15;
        units.Add(new UnitInfo
        {
            q = coordinate.x, r = coordinate.y, team = 1,
            unitClass = HexUnitClass.Infantry, visual = icon.transform, renderer = renderer,
            directionalFrames = frames, currentDirection = "down_right",
            baseScale = Vector3.one * iconScale, unitId = "skin_chest",
            unitName = string.IsNullOrWhiteSpace(displayName) ? "皮肤宝箱" : displayName,
            unitCategory = "special", hp = Mathf.Max(1, maxHP), maxHp = Mathf.Max(1, maxHP),
            attack = 0, baseAttackPower = 0, attackAP = 0, movementPower = 1,
            movementAPLimit = 1, attackRange = 1, canCapture = false,
            reconRange = 0, concealment = 0, isSkinChest = true, rewardSkinId = skinId,
            nextReadyTime = float.PositiveInfinity, ammoCapacity = 0, fuelCapacity = 0, resourceCapacity = 0
        });
    }

    public void ApplyBuildingRestorationAt(int q, int r, int ownerTeam, HexBuildingType type)
    {
        foreach (UnitInfo unit in units)
        {
            if (unit == null || unit.carrier != null || unit.q != q || unit.r != r || unit.team != ownerTeam)
                continue;
            bool ground = unit.unitClass == HexUnitClass.Infantry || unit.unitClass == HexUnitClass.HeavyGround;
            bool factoryMatch =
                (type == HexBuildingType.Arsenal && unit.unitCategory == "infantry") ||
                (type == HexBuildingType.TankFactory && unit.unitCategory == "ground_vehicle") ||
                (type == HexBuildingType.ArtilleryFactory && unit.unitCategory == "artillery") ||
                (type == HexBuildingType.Shipyard && unit.unitCategory == "naval") ||
                (type == HexBuildingType.Airport && unit.unitCategory == "air");

            if (type == HexBuildingType.Headquarters && ground)
            {
                unit.hp = Mathf.Min(unit.maxHp, unit.hp + 10);
                if (unit.ammoCapacity > 0) unit.currentAmmo = Mathf.Min(unit.ammoCapacity, unit.currentAmmo + 1);
                if (unit.fuelCapacity > 0) unit.currentFuel = Mathf.Min(unit.fuelCapacity, unit.currentFuel + 2);
            }
            else if ((type == HexBuildingType.LargeCity || type == HexBuildingType.SmallCity) && ground)
            {
                if (unit.ammoCapacity > 0) unit.currentAmmo = Mathf.Min(unit.ammoCapacity, unit.currentAmmo + 1);
                if (unit.fuelCapacity > 0) unit.currentFuel = Mathf.Min(unit.fuelCapacity, unit.currentFuel + 2);
            }
            else if (type == HexBuildingType.Village && ground)
            {
                if (unit.fuelCapacity > 0) unit.currentFuel = Mathf.Min(unit.fuelCapacity, unit.currentFuel + 1);
            }
            else if (factoryMatch)
            {
                unit.hp = Mathf.Min(unit.maxHp, unit.hp + 10);
                if (unit.ammoCapacity > 0) unit.currentAmmo = Mathf.Min(unit.ammoCapacity, unit.currentAmmo + 1);
                if (unit.fuelCapacity > 0) unit.currentFuel = Mathf.Min(unit.fuelCapacity, unit.currentFuel + 2);
            }
        }
    }

    public void OnHeadquartersCaptured(int capturingTeam, int previousOwner)
    {
        if (battleEnded || previousOwner == capturingTeam) return;
        if (previousOwner == 0) EndBattle(false, "我方大本营被敌方占领。");
        else if (capturingTeam == 0) EndBattle(true, "成功占领敌方大本营。");
    }

    public void RequestSurrender()
    {
        EndBattle(false, "玩家选择投降。");
    }

    private void EndBattle(bool won, string reason)
    {
        if (battleEnded) return;
        battleEnded = true;
        gameplayPaused = true;
        StopAllCoroutines();
        simulationRunning = false;
        selectedUnit = null;
        hasSelectedTile = false;
        moveMode = attackMode = supportMode = transportMode = false;
        supportAction = "";
        if (battlefield != null) battlefield.SetMovementHighlights(null);
        if (gameFlowController == null) gameFlowController = GetComponent<HexGameFlowController>();
        if (gameFlowController != null) gameFlowController.ShowResult(won, reason);
    }

    private void CheckEliminationResult()
    {
        if (battleEnded) return;
        bool playerAlive = false;
        bool hostileAlive = false;
        foreach (UnitInfo unit in units)
        {
            if (unit == null || unit.carrier != null || unit.hp <= 0) continue;
            // Reward chests count as hostile units while elimination victory is enabled,
            // so a match cannot end before the player has a chance to collect the reward.
            if (unit.team == 0) playerAlive = true;
            else hostileAlive = true;
        }
        if (!playerAlive) EndBattle(false, "我方所有作战单位均已被击破。");
        else if (victoryWhenAllHostileUnitsDefeated && !hostileAlive)
            EndBattle(true, "所有敌方作战单位均已被击破。");
    }

    private void LoadUnitProfileDatabase()
    {
        profilesById.Clear();
        TextAsset json = Resources.Load<TextAsset>(unitDatabaseResourcePath);
        if (json == null)
        {
            Debug.LogWarning("未找到 Resources/" + unitDatabaseResourcePath + ".json；当前使用 Inspector 里的备用单位配置。");
            return;
        }

        UnitProfileDatabase database = JsonUtility.FromJson<UnitProfileDatabase>(json.text);
        if (database == null || database.units == null)
        {
            Debug.LogError("单位数据 JSON 格式无效：需要顶层 units 数组。");
            return;
        }

        foreach (UnitBattleProfile profile in database.units)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.id))
                continue;
            string id = profile.id.Trim();
            if (profilesById.ContainsKey(id))
                Debug.LogWarning("单位数据中出现重复 id：" + id);
            profilesById[id] = profile;
        }

        Debug.Log("已加载独立单位配置：" + profilesById.Count + " 种。");
    }

    private UnitBattleProfile ResolveProfile(string profileId, UnitBattleProfile[] fallbackProfiles, int index)
    {
        if (!string.IsNullOrWhiteSpace(profileId) && profilesById.TryGetValue(profileId.Trim(), out UnitBattleProfile fromFile))
            return fromFile;
        if (fallbackProfiles != null && index < fallbackProfiles.Length && fallbackProfiles[index] != null)
            return fallbackProfiles[index];
        return new UnitBattleProfile();
    }

    private Dictionary<string, Sprite[]> LoadDirectionalFrames(string path, string label, Color32 body, Color32 accent)
    {
        var result = new Dictionary<string, Sprite[]>();
        string clean = string.IsNullOrWhiteSpace(path) ? "" : path.Trim().Replace("\\", "/").Trim('/');
        string parent = "";
        Sprite[] fallback = null;
        if (!string.IsNullOrEmpty(clean))
        {
            int slash = clean.LastIndexOf('/');
            if (slash >= 0) parent = clean.Substring(0, slash);
            fallback = TryLoadFourFrames(clean);
        }
        if (fallback == null)
        {
            Sprite placeholder = CreatePixelUnitSprite(body, accent);
            fallback = new[] { placeholder, placeholder, placeholder, placeholder };
            Debug.LogWarning(label + "动画没有完整加载，暂时使用占位图。");
        }
        foreach (string direction in Directions)
        {
            Sprite[] frames = direction == "down_right" || string.IsNullOrEmpty(clean)
                ? fallback : TryLoadFourFrames(parent + "/" + direction);
            if (frames == null)
            {
                frames = fallback;
                Debug.LogWarning(label + "缺少 " + direction + " 动画，使用 down_right 代替。");
            }
            result[direction] = frames;
        }
        return result;
    }

    private Sprite[] TryLoadFourFrames(string folder)
    {
        var frames = new Sprite[4];
        for (int i = 0; i < 4; i++)
        {
            Sprite source = Resources.Load<Sprite>(folder + "/frame_" + (i + 1).ToString("00"));
            if (source == null) return null;
            source.texture.filterMode = FilterMode.Point;
            source.texture.wrapMode = TextureWrapMode.Clamp;
            frames[i] = Sprite.Create(source.texture, source.rect, new Vector2(0.5f, 0.02f), pixelsPerUnit);
            frames[i].name = folder.Replace("/", "_") + "_frame_" + (i + 1).ToString("00");
        }
        return frames;
    }

    private void Update()
    {
        AnimateUnits();
        UpdateUnitVisibility();

        // Escape cancels target selection only; the game-flow controller keeps ownership of the pause-menu shortcut.
        if (!gameplayPaused && !battleEnded && actionMenuHiddenForTargetSelection && WasEscapePressed())
        {
            CancelTargetSelection();
            return;
        }

        HandlePointerGestures(!gameplayPaused && !battleEnded && !simulationRunning);
    }

    private void HandlePointerGestures(bool allowMapClicks)
    {
        Vector2 pointer = GetPointerPosition();
        if (WasLeftClickPressed())
        {
            leftPointerPressed = true;
            leftPointerStart = pointer;
            lastLeftPointerPosition = pointer;
            leftPointerDragged = false;
            leftPointerStartedOverHud = PointerOverHud();
        }
        if (leftPointerPressed && IsLeftButtonHeld())
        {
            if (!leftPointerStartedOverHud && (pointer - leftPointerStart).sqrMagnitude >= 36f)
                leftPointerDragged = true;
            if (leftPointerDragged && battlefield != null)
                battlefield.PanCameraByPixels(pointer - lastLeftPointerPosition);
            lastLeftPointerPosition = pointer;
        }
        if (WasLeftClickReleased())
        {
            if (leftPointerPressed && !leftPointerDragged && !leftPointerStartedOverHud &&
                allowMapClicks && !PointerOverHud())
                HandleMapClick();
            leftPointerPressed = leftPointerDragged = leftPointerStartedOverHud = false;
        }

        if (WasRightClickPressed())
        {
            rightPointerPressed = true;
            rightPointerStart = pointer;
            lastRightPointerPosition = pointer;
            rightPointerDragged = false;
            rightPointerStartedOverHud = PointerOverHud();
        }
        if (rightPointerPressed && IsRightButtonHeld())
        {
            if (!rightPointerStartedOverHud && (pointer - rightPointerStart).sqrMagnitude >= 36f)
                rightPointerDragged = true;
            if (rightPointerDragged && battlefield != null)
                battlefield.RotateCameraByPixels(pointer - lastRightPointerPosition);
            lastRightPointerPosition = pointer;
        }
        if (WasRightClickReleased())
        {
            if (rightPointerPressed && !rightPointerDragged && !rightPointerStartedOverHud &&
                allowMapClicks && !PointerOverHud())
            {
                if (actionMenuHiddenForTargetSelection) CancelTargetSelection();
                else DeselectAll();
            }
            rightPointerPressed = rightPointerDragged = rightPointerStartedOverHud = false;
        }
    }

    private bool WasEscapePressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Escape);
#endif
    }

    private void UpdateUnitVisibility()
    {
        // Player-side visibility: a red unit is revealed only when a blue observer
        // has enough reconnaissance to beat its concealment and it is within recon range.
        foreach(UnitInfo target in units)
        {
            if(target.carrier != null)
            { if(target.renderer != null) target.renderer.enabled = false; continue; }
            if (target.isSkinChest)
            { if (target.renderer != null) target.renderer.enabled = true; continue; }
            if(target.renderer == null || target.team == 0) continue;
            bool detected = false;
            foreach(UnitInfo observer in units)
            {
                if(observer.team != 0 || observer.reconRange < 0 || target.concealment < 0) continue;
                if(observer.reconRange < target.concealment) continue;
                int distance = HexDistance(new Vector2Int(observer.q, observer.r), new Vector2Int(target.q, target.r));
                if(distance <= observer.reconRange) { detected = true; break; }
            }
            target.renderer.enabled = detected;
            if(!detected && selectedUnit == target)
            {
                selectedUnit = null;
                attackMode = false;
                moveMode = false;
                ClearHighlights();
                statusMessage = "敌方单位已脱离我方侦察范围。";
            }
        }
    }

    private void LateUpdate()
    {
        if (mapCamera == null) mapCamera = Camera.main;
        if (mapCamera == null) return;
        foreach (UnitInfo unit in units)
        {
            if (unit.visual == null) continue;
            Vector3 toCamera = mapCamera.transform.position - unit.visual.position;
            if (toCamera.sqrMagnitude > 0.001f)
                unit.visual.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
        }
    }

    private void AnimateUnits()
    {
        if (animationFramesPerSecond <= 0f) return;
        foreach (UnitInfo unit in units)
        {
            if (unit.renderer == null ||
                !unit.directionalFrames.TryGetValue(unit.currentDirection, out Sprite[] frames) ||
                frames == null || frames.Length == 0) continue;
            int frame = Mathf.FloorToInt((Time.time + unit.animationOffset) * animationFramesPerSecond) % frames.Length;
            if (frame != unit.lastFrame) { unit.renderer.sprite = frames[frame]; unit.lastFrame = frame; }
        }
    }

    private bool WasLeftClickPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(0);
#endif
    }
    private bool IsLeftButtonHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
        return Input.GetMouseButton(0);
#endif
    }
    private bool WasLeftClickReleased()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
#else
        return Input.GetMouseButtonUp(0);
#endif
    }
    private bool WasRightClickPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(1);
#endif
    }
    private bool IsRightButtonHeld()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
        return Input.GetMouseButton(1);
#endif
    }
    private bool WasRightClickReleased()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.rightButton.wasReleasedThisFrame;
#else
        return Input.GetMouseButtonUp(1);
#endif
    }

    private Vector2 GetPointerPosition()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
        return Input.mousePosition;
#endif
    }

    private Vector2 GetPointerGuiPosition()
    {
        Vector2 p = GetPointerPosition();
        return new Vector2(p.x, Screen.height - p.y);
    }

    private Rect GetResourcePanelRect()
    {
        return new Rect(12f, 12f, Mathf.Min(300f, Screen.width - 24f), Mathf.Min(188f, Screen.height - 24f));
    }

    private Rect GetUnitInfoPanelRect()
    {
        float width = Mathf.Min(370f, Screen.width - 24f);
        float height = Mathf.Min(430f, Screen.height * 0.52f);
        return new Rect(Screen.width - width - 12f, 12f, width, height);
    }

    private Rect GetTileInfoPanelRect()
    {
        float width = Mathf.Min(370f, Screen.width - 24f);
        float height = Mathf.Min(340f, Screen.height * 0.62f);
        return new Rect(Screen.width - width - 12f, Screen.height - height - 12f, width, height);
    }

    private Rect GetAPTimelinePanelRect()
    {
        float width = Mathf.Min(680f, Screen.width - 24f);
        float height = Mathf.Min(160f, Screen.height - 24f);
        return new Rect(12f, Screen.height - height - 12f, width, height);
    }

    private bool IsActionMenuVisible()
    {
        return selectedUnit != null && selectedUnit.team == 0 && !simulationRunning &&
               showBattleHUD && !actionMenuHiddenForTargetSelection;
    }

    private Rect GetActionMenuRect()
    {
        Vector2 anchor = actionMenuScreenPosition;
        float width = Mathf.Min(320f, Screen.width - 24f);
        float height = Mathf.Min(520f, Screen.height - 24f);
        // Keep the menu beside, rather than directly underneath, the cursor.
        float x = anchor.x + 18f;
        if (x + width > Screen.width - 12f)
            x = anchor.x - width - 18f;
        float y = anchor.y - 18f;
        if (y + height > Screen.height - 12f)
            y = anchor.y - height + 18f;
        x = Mathf.Clamp(x, 12f, Mathf.Max(12f, Screen.width - width - 12f));
        y = Mathf.Clamp(y, 12f, Mathf.Max(12f, Screen.height - height - 12f));
        return new Rect(x, y, width, height);
    }

    private Rect GetTargetSelectionPromptRect()
    {
        float width = Mathf.Min(560f, Screen.width - 24f);
        return new Rect((Screen.width - width) * 0.5f, 12f, width, 48f);
    }

    private bool PointerOverHud()
    {
        Vector2 p = GetPointerGuiPosition();
        // During target picking all large panels are hidden, so they cannot block targets underneath them.
        if (actionMenuHiddenForTargetSelection)
            return GetTargetSelectionPromptRect().Contains(p);
        if (GetResourcePanelRect().Contains(p) || GetAPTimelinePanelRect().Contains(p))
            return true;
        if (selectedUnit != null && GetUnitInfoPanelRect().Contains(p))
            return true;
        if ((selectedUnit != null || hasSelectedTile) && GetTileInfoPanelRect().Contains(p))
            return true;
        return IsActionMenuVisible() && GetActionMenuRect().Contains(p);
    }

    private void HandleMapClick()
    {
        if(mapCamera==null)mapCamera=Camera.main;if(mapCamera==null)return;
        Ray ray=mapCamera.ScreenPointToRay(GetPointerPosition());
        if(!Physics.Raycast(ray,out RaycastHit hit,1000f))return;
        if(!TryReadHexCoordinates(hit.collider.gameObject.name,out int q,out int r))return;
        UnitInfo clicked=FindUnitAt(q,r);
        if(clicked!=null && clicked.team==1 && clicked.renderer!=null && !clicked.renderer.enabled)
            clicked=null;
        if(selectedUnit!=null && selectedUnit.team==0 && transportMode)
        {
            if(clicked!=null && clicked.team==0 && clicked!=selectedUnit) { TryLoadCargo(clicked); return; }
            if(clicked==null) { TryUnloadCargo(q,r); return; }
        }
        if(selectedUnit!=null && selectedUnit.team==0 && !string.IsNullOrEmpty(supportAction) && clicked!=null && clicked.team==0 && clicked!=selectedUnit)
        { ExecuteSupportAction(clicked); return; }
        if(attackMode&&selectedUnit!=null&&clicked!=null&&clicked.team==1){ExecutePlayerAttack(clicked);return;}
        if(clicked!=null)
        {
            if(selectedUnit!=null&&selectedUnit.team==0&&selectedUnit.hasMovedThisActivation&&clicked!=selectedUnit)
            {statusMessage="已移动的单位必须先执行攻击、占领或休整，再切换单位。";return;}
            hasSelectedTile=false;SelectUnit(clicked);return;
        }
        hasSelectedTile=true;selectedTileQ=q;selectedTileR=r;
        if(selectedUnit!=null&&selectedUnit.team==0)
        {
            if(attackMode){statusMessage="攻击模式：请点击攻击范围内的敌军。";return;}
            Vector2Int target=new Vector2Int(q,r);
            if(moveMode&&!selectedUnit.hasMovedThisActivation&&reachableTiles.Contains(target))
            {
                List<Vector2Int> path=ReconstructPath(target);if(path.Count==0)return;
                int routeAP=currentPaths[target].cost,routeMove=currentPaths[target].movementPoints;
                MoveUnitAlongPath(selectedUnit,path);
                selectedUnit.pendingMoveAP+=routeAP;selectedUnit.hasMovedThisActivation=true;
                moveMode=false;attackMode=false;actionMenuHiddenForTargetSelection=false;ClearHighlights();
                statusMessage=$"移动完成：消耗 {routeMove} 移动力、暂记 {routeAP} AP；移动不结束行动，请继续选择非移动行动。";return;
            }
            statusMessage="已选中地块。移动是可选行动；本次行动仍需以攻击、占领或休整结束。";return;
        }
        statusMessage="已选中地块，可查看地形类型、移动力消耗与地块加成。";
    }

    private void DeselectAll()
    {
        if (selectedUnit != null && selectedUnit.visual != null)
            selectedUnit.visual.localScale = selectedUnit.baseScale;
        selectedUnit = null;
        hasSelectedTile = false;
        moveMode = attackMode = supportMode = transportMode = false;
        supportAction = "";
        actionMenuHiddenForTargetSelection = false;
        actionMenuScrollPosition = Vector2.zero;
        ClearHighlights();
        statusMessage = "已取消选择单位和地块。";
    }

    private void CancelTargetSelection()
    {
        attackMode = supportMode = transportMode = false;
        supportAction = "";
        actionMenuHiddenForTargetSelection = false;
        ClearHighlights();
        if (selectedUnit != null && selectedUnit.team == 0 && IsUnitReady(selectedUnit) &&
            !selectedUnit.hasMovedThisActivation)
        {
            moveMode = true;
            RefreshMovementHighlights();
        }
        else moveMode = false;
        statusMessage = "已取消当前行动的目标选择；单位仍保持选中。";
    }

    private void SpawnStartingUnits(Vector2Int[] positions, UnitBattleProfile[] fallbackProfiles, string[] profileIds, int team, Dictionary<string, Sprite[]> animations)
    {
        if(positions==null)return;
        for(int i=0;i<positions.Length;i++)
        {
            int q=positions[i].x,r=positions[i].y;
            if(!battlefield.IsInsideMap(q,r)){Debug.LogWarning($"单位坐标 ({q},{r}) 超出地图。");continue;}
            if(FindUnitAt(q,r)!=null){Debug.LogWarning($"地块 ({q},{r}) 已有单位。");continue;}
            string profileId=profileIds!=null&&i<profileIds.Length?profileIds[i]:"";
            UnitBattleProfile p=ResolveProfile(profileId,fallbackProfiles,i);
            GameObject icon=new GameObject(team==0?"BlueUnitIcon":"RedUnitIcon");
            icon.transform.SetParent(transform,false);icon.transform.localPosition=HexToLocalPosition(q,r);icon.transform.localScale=Vector3.one*iconScale;
            SpriteRenderer sr=icon.AddComponent<SpriteRenderer>();
            var u=new UnitInfo{
                q=q,r=r,team=team,unitClass=p.unitClass,visual=icon.transform,renderer=sr,directionalFrames=animations,
                currentDirection="down_right",baseScale=Vector3.one*iconScale,animationOffset=(q*3+r*7+team*5)*0.037f,
                nextReadyTime=0f,zeroCostActedAtTime=-1f,
                unitId=string.IsNullOrWhiteSpace(p.id)?"infantry":p.id,
                unitName=string.IsNullOrWhiteSpace(p.unitName)?"未命名单位":p.unitName,
                unitCategory=string.IsNullOrWhiteSpace(p.unitCategory)?"infantry":p.unitCategory,
                ammoCapacity=p.ammoCapacity,fuelCapacity=p.fuelCapacity,resourceCapacity=p.resourceCapacity,
                currentAmmo=Mathf.Max(0,p.ammoCapacity),currentFuel=Mathf.Max(0,p.fuelCapacity),
                reconRange=p.reconRange,concealment=p.concealment,canDetectSpy=p.canDetectSpy,
                description=p.description,specialAbilities=p.specialAbilities,dataGaps=p.dataGaps,actions=p.actions,
                hp=Mathf.Max(1,p.maxHP),maxHp=Mathf.Max(1,p.maxHP),
                baseAttackPower=Mathf.Max(0,p.attackPower),attack=Mathf.Max(0,p.attackPower),experience=0,rank=0,attackType=p.attackType,
                bulletDefense=Mathf.Max(0,p.bulletDefense),explosionDefense=Mathf.Max(0,p.explosionDefense),
                movementPower=Mathf.Max(1,p.movementPower),movementAPLimit=Mathf.Max(1,p.movementAPLimit),
                attackAP=Mathf.Max(0,p.attackAP),captureAP=Mathf.Max(0,p.captureAP),restAP=Mathf.Max(0,p.restAP),attackRange=Mathf.Max(1,p.attackRange),canCapture=p.canCapture};
            ApplyActiveSkin(u);
            sr.sprite=animations["down_right"][0];sr.sortingOrder=10;units.Add(u);
        }
    }

    private Vector3 HexToLocalPosition(int q, int r)
    {
        float size = battlefield.hexSize;
        return new Vector3(1.5f * size * q,
            battlefield.tileThickness * 0.5f + liftAboveTile,
            Mathf.Sqrt(3f) * size * (r + q * 0.5f));
    }

    private void SelectUnit(UnitInfo unit)
    {
        if (selectedUnit != null && selectedUnit.visual != null)
            selectedUnit.visual.localScale = selectedUnit.baseScale;
        selectedUnit = unit;
        actionMenuScreenPosition = GetPointerGuiPosition();
        actionMenuScrollPosition = Vector2.zero;
        actionMenuHiddenForTargetSelection = false;
        supportMode = false;
        supportAction = "";
        transportMode = false;
        if (unit.visual != null)
            unit.visual.localScale = unit.baseScale * selectedScaleMultiplier;

        moveMode = unit.team == 0 && IsUnitReady(unit) && !unit.hasMovedThisActivation && !simulationRunning;
        attackMode = false;
        ClearHighlights();
        RefreshMovementHighlights();

        if (unit.team != 0)
            statusMessage = "敌方单位属性（只读）。我方不能直接控制敌方行动。";
        else if (!IsUnitReady(unit))
            statusMessage = $"该单位恢复中，还需 {Mathf.CeilToInt(unit.nextReadyTime - battleTimeAP)} AP。";
        else if (unit.hasMovedThisActivation)
            statusMessage = "该单位已移动；请执行攻击、占领或休整来结束本次行动。";
        else
            statusMessage = "我方操作阶段：移动可选；非移动行动负责结束本次行动。";
    }

    private void ClearHighlights()
    {
        reachableTiles.Clear();
        currentPaths.Clear();
        if (battlefield != null)
        {
            battlefield.SetMovementHighlights(null);
            battlefield.SetActionHighlights(null);
        }
    }

    private void RefreshMovementHighlights()
    {
        reachableTiles.Clear();
        currentPaths.Clear();

        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit) ||
            selectedUnit.hasMovedThisActivation || simulationRunning || !moveMode || attackMode)
        {
            battlefield.SetMovementHighlights(null);
            return;
        }

        Vector2Int start = new Vector2Int(selectedUnit.q, selectedUnit.r);
        currentPaths[start]=new PathNode{cost=0,movementPoints=0,steps=0,previous=start};
        var open=new List<Vector2Int>{start};
        int apBudget=selectedUnit.movementAPLimit;

        while (open.Count > 0)
        {
            int bestIndex = 0;
            for (int i = 1; i < open.Count; i++)
                if (currentPaths[open[i]].cost < currentPaths[open[bestIndex]].cost) bestIndex = i;
            Vector2Int current = open[bestIndex];
            open.RemoveAt(bestIndex);
            PathNode node = currentPaths[current];

            foreach (Vector2Int delta in Neighbors)
            {
                Vector2Int next = current + delta;
                if (!battlefield.IsInsideMap(next.x, next.y) ||
                    !battlefield.IsTileWalkable(next.x, next.y, selectedUnit.unitClass)) continue;
                UnitInfo occupant = FindUnitAt(next.x, next.y);
                if (occupant != null && occupant != selectedUnit) continue;

                int cost=node.cost+battlefield.GetMovementCostAP(next.x,next.y,selectedUnit.unitClass);
                int movementPoints=node.movementPoints+battlefield.GetMovementPointCost(next.x,next.y,selectedUnit.unitClass);
                int steps=node.steps+1;
                if(cost>apBudget||movementPoints>selectedUnit.movementPower)continue;
                if(currentPaths.TryGetValue(next,out PathNode old)&&old.cost<=cost&&old.movementPoints<=movementPoints)continue;
                currentPaths[next]=new PathNode{cost=cost,movementPoints=movementPoints,steps=steps,previous=current};
                if (!open.Contains(next)) open.Add(next);
            }
        }

        foreach (KeyValuePair<Vector2Int, PathNode> pair in currentPaths)
            if (pair.Key != start) reachableTiles.Add(pair.Key);
        battlefield.SetMovementHighlights(reachableTiles);
    }

    private List<Vector2Int> ReconstructPath(Vector2Int destination)
    {
        var path = new List<Vector2Int>();
        if (!currentPaths.ContainsKey(destination)) return path;
        Vector2Int cursor = destination;
        Vector2Int start = new Vector2Int(selectedUnit.q, selectedUnit.r);
        int guard = 0;
        while (cursor != start && guard++ < battlefield.columns * battlefield.rows + 1)
        {
            path.Add(cursor);
            if (!currentPaths.TryGetValue(cursor, out PathNode node)) return new List<Vector2Int>();
            cursor = node.previous;
        }
        if (cursor != start) return new List<Vector2Int>();
        path.Reverse();
        return path;
    }

    // Every live unit receives a unique integer AP node, regardless of faction.
    // Initial ties are resolved round-robin by faction ID so one team is not given
    // all of the first action slots simply because its units were spawned first.
    private void InitializeUniqueActionNodes()
    {
        var activeUnits = new List<UnitInfo>();
        var teamIds = new List<int>();
        foreach (UnitInfo unit in units)
        {
            if (unit == null || unit.carrier != null || unit.isSkinChest) continue;
            activeUnits.Add(unit);
            if (!teamIds.Contains(unit.team)) teamIds.Add(unit.team);
        }
        teamIds.Sort();

        var assigned = new HashSet<UnitInfo>();
        int nextNode = 0;
        bool assignedAny;
        do
        {
            assignedAny = false;
            foreach (int teamId in teamIds)
            {
                foreach (UnitInfo unit in activeUnits)
                {
                    if (assigned.Contains(unit) || unit.team != teamId) continue;
                    unit.nextReadyTime = nextNode++;
                    unit.zeroCostActedAtTime = -1f;
                    assigned.Add(unit);
                    assignedAny = true;
                    break;
                }
            }
        }
        while (assignedAny);
    }

    // The unit being rescheduled is excluded from the collision test. Any other unit
    // already booked at this AP keeps its slot; the later booking moves forward.
    private float ReserveNextActionNode(UnitInfo unit, float proposedTime)
    {
        if (unit == null) return proposedTime;

        int candidate = Mathf.CeilToInt(proposedTime - 0.001f);
        int currentNode = Mathf.FloorToInt(battleTimeAP + 0.001f);
        if (candidate <= currentNode) candidate = currentNode + 1;

        bool occupied;
        do
        {
            occupied = false;
            foreach (UnitInfo other in units)
            {
                if (other == null || other == unit || other.carrier != null || other.isSkinChest) continue;
                if (Mathf.Abs(other.nextReadyTime - candidate) < 0.001f)
                {
                    occupied = true;
                    break;
                }
            }
            if (occupied) candidate++;
        }
        while (occupied);

        unit.nextReadyTime = candidate;
        return unit.nextReadyTime;
    }

    private void MoveUnitAlongPath(UnitInfo unit, List<Vector2Int> path)
    {
        foreach (Vector2Int point in path)
        {
            if(unit.unitId=="fortress") unit.fortressGarrisoned=false;
            unit.currentDirection = GetDirectionForMove(point.x - unit.q, point.y - unit.r);
            unit.q = point.x;
            unit.r = point.y;
            unit.visual.localPosition = HexToLocalPosition(unit.q, unit.r);
            unit.lastFrame = -1;
        }
    }

    private int GetAttackDefense(UnitInfo target,HexAttackType type)=>type==HexAttackType.Bullet?target.bulletDefense:target.explosionDefense;
    private string AttackTypeName(HexAttackType type)=>type==HexAttackType.Bullet?"子弹":"爆炸";

    private int RankFromExperience(int experience)
    {
        if (experience >= 5000) return 3;
        if (experience >= 2600) return 2;
        if (experience >= 1000) return 1;
        return 0;
    }

    private string RankName(int rank)
    {
        switch(rank)
        {
            case 1: return "一条杠";
            case 2: return "两条杠";
            case 3: return "将星";
            default: return "列兵";
        }
    }

    private void GrantExperience(UnitInfo unit, int amount)
    {
        if (unit == null || amount <= 0 || unit.experience >= 5000) return;
        int oldRank = RankFromExperience(unit.experience);
        unit.experience = Mathf.Clamp(unit.experience + amount, 0, 5000);
        int newRank = RankFromExperience(unit.experience);
        if (newRank > oldRank)
        {
            for (int newLevel = oldRank + 1; newLevel <= newRank; newLevel++)
            {
                unit.maxHp += 10;
                // Do not revive a unit that has already been lethally damaged.
                if (unit.hp > 0) unit.hp += 10;
            }
            Debug.Log($"{unit.unitName}升阶为{RankName(newRank)}：最大生命值 +{(newRank-oldRank)*10}，攻击力提升到基础攻击的 {100+newRank*10}% 。");
        }
        unit.rank = newRank;
        unit.attack = Mathf.RoundToInt(unit.baseAttackPower * (1f + 0.1f * newRank));
    }

    private void ApplyDamageAndAwardExperience(UnitInfo attacker, UnitInfo target, int damage)
    {
        if (target == null || damage <= 0) return;
        int actualDamage = Mathf.Min(target.hp, damage);
        target.hp = Mathf.Max(0, target.hp - actualDamage);
        // Being damaged grants 2 XP per point of actual HP lost.
        GrantExperience(target, actualDamage * 2);
        if (target.hp <= 0 && attacker != null)
        {
            // The defeated unit transfers half of its current accumulated XP to the killer.
            GrantExperience(attacker, Mathf.FloorToInt(target.experience * 0.5f));
        }
    }

    private bool IsUnitReady(UnitInfo unit)
    {
        if (unit == null || unit.nextReadyTime > battleTimeAP + 0.001f) return false;
        // A zero-AP action still consumes this unit's current activation, without inventing AP cost.
        return Mathf.Abs(unit.zeroCostActedAtTime - battleTimeAP) > 0.001f;
    }

    private void BeginAttackMode()
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        BeginConfiguredAttack("常规攻击", 1f, selectedUnit.attackType, selectedUnit.attackAP,
            false, false, false, 1, Mathf.Max(1, selectedUnit.attackRange));
    }

    private void GetConfiguredActionRange(int actionIndex, int fallbackMin, int fallbackMax,
        out int minRange, out int maxRange)
    {
        minRange = Mathf.Max(0, fallbackMin);
        maxRange = Mathf.Max(minRange, fallbackMax);
        if (selectedUnit == null || selectedUnit.actions == null ||
            actionIndex < 0 || actionIndex >= selectedUnit.actions.Length) return;
        UnitActionProfile action = selectedUnit.actions[actionIndex];
        if (action == null || !action.rangeConfigured) return;
        minRange = Mathf.Max(0, action.minRange);
        maxRange = Mathf.Max(minRange, action.maxRange);
    }

    private string ConfiguredRangeLabel(int actionIndex, int fallbackMin = 1, int fallbackMax = 1)
    {
        GetConfiguredActionRange(actionIndex, fallbackMin, fallbackMax, out int minRange, out int maxRange);
        return $"{minRange}-{maxRange}";
    }

    private void BeginProfiledAttack(int actionIndex, string name, float multiplier, HexAttackType type, int cost,
        bool navalOnly = false, bool trueDamage = false, bool airOnly = false,
        int fallbackMinRange = 1, int fallbackMaxRange = 1)
    {
        GetConfiguredActionRange(actionIndex, fallbackMinRange, fallbackMaxRange, out int minRange, out int maxRange);
        BeginConfiguredAttack(name, multiplier, type, cost, navalOnly, trueDamage, airOnly, minRange, maxRange);
    }

    private void HighlightAttackTargets()
    {
        var targets = new List<Vector2Int>();
        if (selectedUnit != null)
        {
            Vector2Int origin = new Vector2Int(selectedUnit.q, selectedUnit.r);
            foreach (UnitInfo target in units)
            {
                if (target == null || target.team == selectedUnit.team || target.hp <= 0 ||
                    target.carrier != null || (target.renderer != null && !target.renderer.enabled)) continue;
                int distance = HexDistance(origin, new Vector2Int(target.q, target.r));
                if (distance < selectedAttackMinRange || distance > selectedAttackMaxRange) continue;
                if (selectedAttackNavalOnly && target.unitClass != HexUnitClass.Naval) continue;
                if (selectedAttackAirOnly && target.unitClass != HexUnitClass.Air) continue;
                targets.Add(new Vector2Int(target.q, target.r));
            }
        }
        if (battlefield != null) battlefield.SetActionHighlights(targets);
    }

    private void BeginConfiguredAttack(string name, float multiplier, HexAttackType type, int cost,
        bool navalOnly = false, bool trueDamage = false, bool airOnly = false, int minRange = 1, int maxRange = 1)
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        if (selectedUnit.unitId == "fortress" && !selectedUnit.fortressGarrisoned)
        { statusMessage = "要塞处于机动形态，不能攻击。"; return; }
        if (selectedUnit.ammoCapacity > 0 && selectedUnit.currentAmmo <= 0)
        { statusMessage = "弹药不足。"; return; }
        moveMode = false;
        attackMode = true;
        supportAction = "";
        supportMode = false;
        transportMode = false;
        selectedAttackName = name;
        selectedAttackMultiplier = multiplier;
        selectedAttackType = type;
        selectedAttackCost = cost;
        selectedAttackMinRange = Mathf.Max(0, minRange);
        selectedAttackMaxRange = Mathf.Max(selectedAttackMinRange, maxRange);
        selectedAttackNavalOnly = navalOnly;
        selectedAttackTrueDamage = trueDamage;
        selectedAttackAirOnly = airOnly;
        actionMenuHiddenForTargetSelection = true;
        ClearHighlights();
        HighlightAttackTargets();
        statusMessage = $"{name}模式：射程 {selectedAttackMinRange}-{selectedAttackMaxRange}；点击橙色高亮目标，Esc 或右键取消。";
    }

    private void ExecutePlayerAttack(UnitInfo target)
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        int distance = HexDistance(new Vector2Int(selectedUnit.q, selectedUnit.r), new Vector2Int(target.q, target.r));
        if (distance < selectedAttackMinRange || distance > selectedAttackMaxRange)
        { statusMessage = $"目标超出{selectedAttackName}的射程（当前距离 {distance} 格，要求 {selectedAttackMinRange}-{selectedAttackMaxRange}）。"; return; }
        if(selectedAttackNavalOnly && target.unitClass!=HexUnitClass.Naval) { statusMessage="该行动只能攻击海军单位。"; return; }
        if(selectedAttackAirOnly && target.unitClass!=HexUnitClass.Air) { statusMessage="该行动只能攻击空军单位。"; return; }
        if(selectedUnit.ammoCapacity>0 && selectedUnit.currentAmmo<=0) { statusMessage="弹药不足。"; return; }
        int terrainDefense=battlefield.GetDefenseBonus(target.q,target.r);
        int rawDamage=Mathf.RoundToInt(selectedUnit.attack*selectedAttackMultiplier);
        int damage=selectedAttackTrueDamage?Mathf.Max(1,rawDamage):Mathf.Max(1,rawDamage-GetAttackDefense(target,selectedAttackType)-terrainDefense);
        ApplyDamageAndAwardExperience(selectedUnit,target,damage);
        if(selectedUnit.ammoCapacity>0) selectedUnit.currentAmmo=Mathf.Max(0,selectedUnit.currentAmmo-1);
        int actionCost=selectedAttackCost;
        statusMessage=$"{selectedUnit.unitName}使用{selectedAttackName}造成 {damage} 伤害；消耗 {actionCost} AP。";
        if (target.hp <= 0)
        {
            if (target.isSkinChest) SkinChestDefeated?.Invoke(target.rewardSkinId);
            units.Remove(target);
            if (target.visual != null) Destroy(target.visual.gameObject);
            if (!target.isSkinChest) statusMessage += " 敌方单位被击破。";
            CheckEliminationResult();
        }

        FinishPlayerActivation(actionCost);
    }

    private void OccupyCity()
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        if (!selectedUnit.canCapture)
        {
            statusMessage = "该单位不具备占领能力。";
            return;
        }
        int cost = selectedUnit.captureAP;
        if (buildingEconomy != null && buildingEconomy.TryCaptureBuildingAt(selectedUnit.q, selectedUnit.r, selectedUnit.team, this, out string captureMessage))
        {
            statusMessage = captureMessage + $" 行动消耗 {cost} AP。";
            FinishPlayerActivation(cost);
            return;
        }
        if (!battlefield.IsCity(selectedUnit.q, selectedUnit.r))
        {
            statusMessage = "当前地块不是可占领建筑。";
            return;
        }
        if (battlefield.GetCityOwner(selectedUnit.q, selectedUnit.r) == 0)
        {
            statusMessage = "这座城市已经属于我方。";
            return;
        }
        battlefield.CaptureCity(selectedUnit.q, selectedUnit.r, 0);
        statusMessage = $"占领完成：城市归属已变为我方，行动消耗 {cost} AP。";
        FinishPlayerActivation(cost);
    }

    private void Rest()
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        statusMessage=$"休整完成：没有额外效果，行动消耗 {selectedUnit.restAP} AP。";
        FinishPlayerActivation(selectedUnit.restAP);
    }

    private void FinishPlayerActivation(int actionCost)
    {
        if (selectedUnit == null) return;
        UnitInfo acted = selectedUnit;
        int totalCost = acted.pendingMoveAP + actionCost;

        // Only the non-movement action cost awards AP-based experience; movement AP is excluded.
        GrantExperience(acted, actionCost);

        if (totalCost == 0)
            acted.zeroCostActedAtTime = battleTimeAP;
        else
            acted.zeroCostActedAtTime = -1f;

        float nextActionNode = ReserveNextActionNode(acted, battleTimeAP + totalCost);
        acted.pendingMoveAP = 0;
        acted.hasMovedThisActivation = false;
        selectedUnit = null;
        moveMode = false;
        attackMode = false;
        actionMenuHiddenForTargetSelection = false;
        ClearHighlights();

        statusMessage = $"本次行动结束：移动与行动合计消耗 {totalCost} AP；下次可行动时间为 AP {nextActionNode:0}。";
        if (!gameplayPaused && !battleEnded && !HasReadyBlueUnit())
            StartCoroutine(RunSimulationUntilPlayerCanAct());
    }

    private IEnumerator RunSimulationUntilPlayerCanAct()
    {
        if (simulationRunning) yield break;
        simulationRunning = true;
        moveMode = false;
        attackMode = false;
        ClearHighlights();
        statusMessage = "敌方正在计算行动顺序……";

        int safety = 0;
        while (!gameplayPaused && !battleEnded && !HasReadyBlueUnit() && safety++ < 300)
        {
            UnitInfo readyEnemy = null;
            foreach (UnitInfo unit in units)
                if (unit.team == 1 && !unit.isSkinChest && unit.carrier == null && IsUnitReady(unit)) { readyEnemy = unit; break; }

            if (readyEnemy != null)
            {
                yield return StartCoroutine(PerformEnemyActivation(readyEnemy));
                if (gameplayPaused || battleEnded) break;
                continue;
            }

            float nextTime = float.PositiveInfinity;
            foreach (UnitInfo unit in units)
                if (unit.carrier == null && unit.nextReadyTime > battleTimeAP + 0.001f && unit.nextReadyTime < nextTime)
                    nextTime = unit.nextReadyTime;

            if (float.IsInfinity(nextTime))
            {
                statusMessage = "没有未来 AP 事件。请检查是否所有单位都执行了零 AP 休整。";
                break;
            }

            // Discrete event jump: never based on Time.deltaTime.
            float previousAP = battleTimeAP;
            battleTimeAP = nextTime;
            if (buildingEconomy != null) buildingEconomy.AdvanceAP(previousAP, battleTimeAP, this);
            if (gameplayPaused || battleEnded) break;
        }

        if (safety >= 300) Debug.LogWarning("Simulation safety limit reached.");
        simulationRunning = false;
        if (!gameplayPaused && !battleEnded)
        {
            selectedUnit = null;
            statusMessage = HasReadyBlueUnit()
                ? "战场时间已推进；现在有我方单位可以行动。"
                : statusMessage;
        }
    }

    private IEnumerator PerformEnemyActivation(UnitInfo enemy)
    {
        if (enemy == null) yield break;
        UnitInfo target = FindNearestBlue(enemy.q, enemy.r);
        if (target == null)
        {
            ReserveNextActionNode(enemy, battleTimeAP + blockedEnemyWaitAP);
            yield break;
        }

        int moveCost = 0;
        Dictionary<Vector2Int, PathNode> paths = CalculatePaths(enemy);
        Vector2Int start = new Vector2Int(enemy.q, enemy.r);
        Vector2Int destination = start;
        int bestDistance = HexDistance(start, new Vector2Int(target.q, target.r));
        int bestCost = int.MaxValue;

        foreach (KeyValuePair<Vector2Int, PathNode> pair in paths)
        {
            if (pair.Key == start) continue;
            int distance = HexDistance(pair.Key, new Vector2Int(target.q, target.r));
            if (distance < bestDistance || (distance == bestDistance && pair.Value.cost < bestCost))
            {
                bestDistance = distance;
                bestCost = pair.Value.cost;
                destination = pair.Key;
            }
        }

        if (destination != start && bestDistance < HexDistance(start, new Vector2Int(target.q, target.r)))
        {
            List<Vector2Int> path = ReconstructPathFrom(paths, start, destination);
            moveCost = paths[destination].cost;
            statusMessage = $"敌方{ClassName(enemy.unitClass)}移动……";
            foreach (Vector2Int point in path)
            {
                enemy.currentDirection = GetDirectionForMove(point.x - enemy.q, point.y - enemy.r);
                enemy.q = point.x;
                enemy.r = point.y;
                enemy.visual.localPosition = HexToLocalPosition(enemy.q, enemy.r);
                enemy.lastFrame = -1;
                if (enemyActionDisplayDelay > 0f)
                    yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
                while (gameplayPaused && !battleEnded) yield return null;
                if (battleEnded) yield break;
            }
        }

        target = FindNearestBlue(enemy.q, enemy.r);
        int actionCost = 0;
        if (target != null &&
            HexDistance(new Vector2Int(enemy.q, enemy.r), new Vector2Int(target.q, target.r)) <= enemy.attackRange)
        {
            int damage=Mathf.Max(1,enemy.attack-GetAttackDefense(target,enemy.attackType)-battlefield.GetDefenseBonus(target.q,target.r));
            ApplyDamageAndAwardExperience(enemy,target,damage);
            actionCost=enemy.attackAP;
            statusMessage = $"敌方攻击我方单位，造成 {damage} 伤害。";
            if (target.hp <= 0)
            {
                units.Remove(target);
                if (target.visual != null) Destroy(target.visual.gameObject);
                statusMessage += " 我方单位被击破。";
                CheckEliminationResult();
                if (battleEnded) yield break;
            }
            if (enemyActionDisplayDelay > 0f)
                yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
            while (gameplayPaused && !battleEnded) yield return null;
            if (battleEnded) yield break;
        }
        else if (enemy.canCapture && buildingEconomy != null && buildingEconomy.CanCaptureAt(enemy.q, enemy.r, enemy.team))
        {
            buildingEconomy.TryCaptureBuildingAt(enemy.q, enemy.r, enemy.team, this, out string captureMessage);
            actionCost = enemy.captureAP;
            statusMessage = $"敌方{captureMessage} 消耗 {actionCost} AP。";
            if (enemyActionDisplayDelay > 0f) yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
            if (gameplayPaused || battleEnded) yield break;
        }
        else if (battlefield.IsCity(enemy.q, enemy.r) && battlefield.GetCityOwner(enemy.q, enemy.r) != enemy.team && enemy.canCapture)
        {
            battlefield.CaptureCity(enemy.q, enemy.r, enemy.team);
            actionCost = enemy.captureAP;
            statusMessage = $"敌方占领城市，消耗 {actionCost} AP。";
            if (enemyActionDisplayDelay > 0f) yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
            if (gameplayPaused || battleEnded) yield break;
        }
        else if (moveCost == 0)
        {
            actionCost = blockedEnemyWaitAP;
            statusMessage = $"敌方{ClassName(enemy.unitClass)}未找到有效行动，等待 {blockedEnemyWaitAP} AP。";
        }
        else
        {
            // Enemy movement also has a non-movement end action: a no-effect rest costing zero AP.
            actionCost = 0;
            statusMessage = $"敌方移动结束并休整（休整 0 AP）；移动成本为 {moveCost} AP。";
        }

        // AP experience is based on the non-movement action only, not moveCost.
        GrantExperience(enemy, actionCost);
        int totalCost = moveCost + actionCost;
        ReserveNextActionNode(enemy, battleTimeAP + totalCost);
        if (totalCost == 0) enemy.zeroCostActedAtTime = battleTimeAP;
        else enemy.zeroCostActedAtTime = -1f;
    }

    private Dictionary<Vector2Int, PathNode> CalculatePaths(UnitInfo unit)
    {
        var paths = new Dictionary<Vector2Int, PathNode>();
        var open = new List<Vector2Int>();
        Vector2Int start = new Vector2Int(unit.q, unit.r);
        paths[start]=new PathNode{cost=0,movementPoints=0,steps=0,previous=start};
        open.Add(start);

        while (open.Count > 0)
        {
            int bi = 0;
            for (int i = 1; i < open.Count; i++)
                if (paths[open[i]].cost < paths[open[bi]].cost) bi = i;
            Vector2Int current = open[bi];
            open.RemoveAt(bi);
            PathNode node = paths[current];

            foreach (Vector2Int delta in Neighbors)
            {
                Vector2Int next = current + delta;
                if (!battlefield.IsInsideMap(next.x, next.y) ||
                    !battlefield.IsTileWalkable(next.x, next.y, unit.unitClass)) continue;
                UnitInfo occupied = FindUnitAt(next.x, next.y);
                if (occupied != null && occupied != unit) continue;

                int cost=node.cost+battlefield.GetMovementCostAP(next.x,next.y,unit.unitClass);
                int movementPoints=node.movementPoints+battlefield.GetMovementPointCost(next.x,next.y,unit.unitClass);
                int steps=node.steps+1;
                if(movementPoints>unit.movementPower||cost>unit.movementAPLimit)continue;
                if(paths.TryGetValue(next,out PathNode old)&&old.cost<=cost&&old.movementPoints<=movementPoints)continue;
                paths[next]=new PathNode{cost=cost,movementPoints=movementPoints,steps=steps,previous=current};
                if (!open.Contains(next)) open.Add(next);
            }
        }
        return paths;
    }

    private List<Vector2Int> ReconstructPathFrom(Dictionary<Vector2Int, PathNode> paths, Vector2Int start, Vector2Int end)
    {
        var path = new List<Vector2Int>();
        Vector2Int cursor = end;
        int guard = 0;
        while (cursor != start && guard++ < battlefield.columns * battlefield.rows + 1)
        {
            path.Add(cursor);
            if (!paths.TryGetValue(cursor, out PathNode node)) return new List<Vector2Int>();
            cursor = node.previous;
        }
        if (cursor != start) return new List<Vector2Int>();
        path.Reverse();
        return path;
    }

    private int HexDistance(Vector2Int a, Vector2Int b)
    {
        int dq = a.x - b.x, dr = a.y - b.y;
        return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2;
    }

    private UnitInfo FindNearestBlue(int q, int r)
    {
        UnitInfo best = null;
        int distance = int.MaxValue;
        foreach (UnitInfo unit in units)
        {
            if (unit.team != 0 || unit.carrier != null) continue;
            int d = HexDistance(new Vector2Int(q, r), new Vector2Int(unit.q, unit.r));
            if (d < distance) { distance = d; best = unit; }
        }
        return best;
    }

    private bool HasReadyBlueUnit()
    {
        foreach (UnitInfo unit in units)
            if (unit.team == 0 && unit.carrier == null && IsUnitReady(unit)) return true;
        return false;
    }

    private UnitInfo FindUnitAt(int q, int r)
    {
        foreach (UnitInfo unit in units)
            if (unit.carrier == null && unit.q == q && unit.r == r) return unit;
        return null;
    }

    private bool TryReadHexCoordinates(string name, out int q, out int r)
    {
        q = r = 0;
        string[] parts = name.Split('_');
        return parts.Length >= 4 && parts[0] == "Hex" &&
               int.TryParse(parts[1], out q) && int.TryParse(parts[2], out r);
    }

    private string GetDirectionForMove(int dq, int dr)
    {
        if (dq == 1 && dr == 0) return "up_right";
        if (dq == -1 && dr == 0) return "down_left";
        if (dq == 0 && dr == 1) return "up_right";
        if (dq == 0 && dr == -1) return "down_right";
        if (dq == 1 && dr == -1) return "down_right";
        if (dq == -1 && dr == 1) return "up_left";
        return "down_right";
    }

    private string ClassName(HexUnitClass type)
    {
        switch (type)
        {
            case HexUnitClass.HeavyGround: return "重型地面单位";
            case HexUnitClass.Air: return "空军单位";
            case HexUnitClass.Naval: return "舰船单位";
            default: return "步兵";
        }
    }

    private string CategoryName(string category)
    {
        switch(category)
        {
            case "ground_vehicle": return "战车";
            case "artillery": return "炮兵";
            case "air": return "空军";
            case "naval": return "海军";
            default: return "步兵";
        }
    }

    private void OnGUI()
    {
        if (!showBattleHUD || gameplayPaused || battleEnded) return;

        if (actionMenuHiddenForTargetSelection)
        {
            Rect promptRect = GetTargetSelectionPromptRect();
            GUILayout.BeginArea(promptRect, GUI.skin.box);
            GUILayout.Label(statusMessage);
            GUILayout.EndArea();
            return;
        }

        DrawResourceHUD();
        DrawAPTimelineHUD();
        DrawUnitInfoHUD();
        DrawTileInfoHUD();
        DrawActionMenuHUD();
    }

    private void DrawResourceHUD()
    {
        Rect rect = GetResourcePanelRect();
        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label("我方资源");
        // Resource values are intentionally placeholders until the economy system is connected.
        GUILayout.Label(buildingEconomy != null ? buildingEconomy.GetResourceSummary(0) : "经济系统尚未初始化。");
        GUILayout.Label($"战场 AP：{battleTimeAP:0}");
        GUILayout.Label(simulationRunning ? "阶段：敌方行动 / 时间推进中" : "阶段：等待我方操作");
        if (!string.IsNullOrEmpty(statusMessage))
            GUILayout.Label(statusMessage);
        if (GUILayout.Button("局内菜单（Esc）") && gameFlowController != null) gameFlowController.OpenPauseMenuFromHUD();
        if (!gameplayPaused && !battleEnded && !simulationRunning && !HasReadyBlueUnit() &&
            GUILayout.Button("推进战场时间 / 执行敌方行动")) StartCoroutine(RunSimulationUntilPlayerCanAct());
        GUILayout.EndArea();
    }

    private void DrawAPTimelineHUD()
    {
        Rect rect = GetAPTimelinePanelRect();
        GUI.Box(rect, GUIContent.none);

        float pad = 12f;
        float trackX = rect.x + pad;
        float trackWidth = Mathf.Max(80f, rect.width - pad * 2f);
        float titleY = rect.y + 5f;
        float tickY = rect.y + 25f;
        float trackY = rect.y + 43f;
        float trackHeight = 10f;
        GUI.Label(new Rect(trackX, titleY, trackWidth * 0.7f, 20f), "我方行动时间轴（未来 500 AP）");
        GUI.Label(new Rect(rect.xMax - 150f, titleY, 138f, 20f), $"当前 AP：{battleTimeAP:0}");

        Color originalColor = GUI.color;
        GUI.color = new Color(0.18f, 0.20f, 0.23f, 1f);
        GUI.DrawTexture(new Rect(trackX, trackY, trackWidth, trackHeight), Texture2D.whiteTexture);
        GUI.color = new Color(0.55f, 0.58f, 0.62f, 1f);
        for (int tick = 0; tick <= 500; tick += 100)
        {
            float x = trackX + trackWidth * tick / 500f;
            GUI.DrawTexture(new Rect(x, trackY - 4f, 1f, trackHeight + 8f), Texture2D.whiteTexture);
            GUI.Label(new Rect(Mathf.Clamp(x - 15f, trackX - 4f, trackX + trackWidth - 28f), tickY, 36f, 16f), tick.ToString());
        }

        var visibleUnits = new List<UnitInfo>();
        int outsideRangeCount = 0;
        foreach (UnitInfo unit in units)
        {
            if (unit == null || unit.team != 0 || unit.carrier != null) continue;
            float remaining = Mathf.Max(0f, unit.nextReadyTime - battleTimeAP);
            if (remaining <= 500f + 0.001f) visibleUnits.Add(unit);
            else outsideRangeCount++;
        }
        visibleUnits.Sort((a, b) => a.nextReadyTime.CompareTo(b.nextReadyTime));

        float labelWidth = Mathf.Min(105f, Mathf.Max(72f, trackWidth / 5f));
        const int laneCount = 3;
        float[] laneRight = new float[laneCount];
        for (int i = 0; i < laneCount; i++) laneRight[i] = trackX - labelWidth - 10f;
        int laneY = 0;

        for (int i = 0; i < visibleUnits.Count; i++)
        {
            UnitInfo unit = visibleUnits[i];
            float remaining = Mathf.Max(0f, unit.nextReadyTime - battleTimeAP);
            float markerX = trackX + trackWidth * Mathf.Clamp01(remaining / 500f);
            GUI.color = new Color(0.25f, 0.72f, 1f, 1f);
            GUI.DrawTexture(new Rect(markerX - 2f, trackY - 5f, 4f, trackHeight + 10f), Texture2D.whiteTexture);
            GUI.color = originalColor;

            float labelX = Mathf.Clamp(markerX - labelWidth * 0.5f, trackX, trackX + trackWidth - labelWidth);
            laneY = -1;
            for (int lane = 0; lane < laneCount; lane++)
            {
                if (labelX >= laneRight[lane] + 3f) { laneY = lane; break; }
            }
            if (laneY < 0) laneY = i % laneCount;
            laneRight[laneY] = labelX + labelWidth;
            float labelY = trackY + 14f + laneY * 18f;
            GUI.Label(new Rect(labelX, labelY, labelWidth, 18f), $"{unit.unitName} {remaining:0} AP");
        }

        if (outsideRangeCount > 0)
            GUI.Label(new Rect(trackX, rect.yMax - 21f, trackWidth, 18f), $"另有 {outsideRangeCount} 个我方单位的下次行动超出 500 AP 范围");
        GUI.color = originalColor;
    }

    private void DrawUnitInfoHUD()
    {
        if (selectedUnit == null) return;
        Rect rect = GetUnitInfoPanelRect();
        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label(selectedUnit.team == 0 ? "我方单位信息" : "敌方单位信息（只读）");
        unitInfoScrollPosition = GUILayout.BeginScrollView(
            unitInfoScrollPosition,
            GUILayout.Width(rect.width - 10f),
            GUILayout.Height(rect.height - 34f));

        GUILayout.Label($"单位：{selectedUnit.unitName}　分类：{CategoryName(selectedUnit.unitCategory)}");
        GUILayout.Label($"阵营：{(selectedUnit.team == 0 ? "我方" : "敌方")}　坐标：({selectedUnit.q},{selectedUnit.r})");
        GUILayout.Label($"生命：{selectedUnit.hp}/{selectedUnit.maxHp}（{100f * selectedUnit.hp / Mathf.Max(1, selectedUnit.maxHp):0}%）");
        GUILayout.Label($"经验：{selectedUnit.experience}/5000　军阶：{RankName(selectedUnit.rank)}");
        GUILayout.Label($"攻击力：{selectedUnit.attack}　攻击类型：{AttackTypeName(selectedUnit.attackType)}　范围：{selectedUnit.attackRange}");
        GUILayout.Label($"子弹防御：{selectedUnit.bulletDefense}　爆炸防御：{selectedUnit.explosionDefense}");
        GUILayout.Label($"弹药：{(selectedUnit.ammoCapacity < 0 ? "待确认" : selectedUnit.currentAmmo + "/" + selectedUnit.ammoCapacity)}　燃料：{(selectedUnit.fuelCapacity < 0 ? "待确认" : selectedUnit.currentFuel + "/" + selectedUnit.fuelCapacity)}");
        GUILayout.Label($"载运：{(selectedUnit.resourceCapacity < 0 ? "待确认" : selectedUnit.cargo.Count + "/" + selectedUnit.resourceCapacity)}");
        GUILayout.Label($"侦察：{(selectedUnit.reconRange < 0 ? "待确认" : selectedUnit.reconRange + " 格")}　隐蔽：{(selectedUnit.concealment < 0 ? "待确认" : selectedUnit.concealment + "/100")}");
        GUILayout.Label($"移动力：{selectedUnit.movementPower}　暂记移动 AP：{selectedUnit.pendingMoveAP}");
        string actionReadiness = IsUnitReady(selectedUnit)
            ? "就绪"
            : "等待 " + Mathf.Max(0, Mathf.CeilToInt(selectedUnit.nextReadyTime - battleTimeAP)) + " AP";
        GUILayout.Label($"占领能力：{(selectedUnit.canCapture ? "可以" : "不可以")}　状态：{actionReadiness}");
        if (!string.IsNullOrWhiteSpace(selectedUnit.description))
            GUILayout.Label($"定位：{selectedUnit.description}");
        if (selectedUnit.actions != null && selectedUnit.actions.Length > 0)
            GUILayout.Label("行动配置：" + ActionDescriptions(selectedUnit));
        if (selectedUnit.cargo.Count > 0)
            foreach (UnitInfo passenger in selectedUnit.cargo)
                GUILayout.Label("载员：" + passenger.unitName);

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawTileInfoHUD()
    {
        if (selectedUnit == null && !hasSelectedTile) return;

        int q = hasSelectedTile ? selectedTileQ : selectedUnit.q;
        int r = hasSelectedTile ? selectedTileR : selectedUnit.r;
        Rect rect = GetTileInfoPanelRect();
        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label("地块信息");
        tileInfoScrollPosition = GUILayout.BeginScrollView(
            tileInfoScrollPosition,
            GUILayout.Width(rect.width - 10f),
            GUILayout.Height(rect.height - 34f));
        GUILayout.Label($"坐标：({q},{r})　类型：{battlefield.GetTerrainDisplayName(q, r)}");
        GUILayout.Label($"移动力消耗：步兵 {battlefield.GetMovementPointCost(q, r, HexUnitClass.Infantry)}；重型 {battlefield.GetMovementPointCost(q, r, HexUnitClass.HeavyGround)}；空军 {battlefield.GetMovementPointCost(q, r, HexUnitClass.Air)}");
        GUILayout.Label($"地块 AP 成本：步兵 {TerrainAPCost(q, r, HexUnitClass.Infantry)}；空军 {TerrainAPCost(q, r, HexUnitClass.Air)}");
        GUILayout.Label($"地块加成：{battlefield.GetTerrainBonusDescription(q, r)}");
        if (buildingEconomy != null) buildingEconomy.DrawTileControls(selectedUnit == null ? 0 : selectedUnit.team, q, r, this);
        else if (battlefield.IsCity(q, r))
        {
            int owner = battlefield.GetCityOwner(q, r);
            GUILayout.Label($"城市归属：{(owner == 0 ? "我方" : owner == 1 ? "敌方" : "无主")}");
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawActionMenuHUD()
    {
        if (!IsActionMenuVisible()) return;
        UnitInfo menuUnit = selectedUnit;
        Rect rect = GetActionMenuRect();
        GUILayout.BeginArea(rect, GUI.skin.box);
        GUILayout.Label($"行动菜单：{menuUnit.unitName}");
        actionMenuScrollPosition = GUILayout.BeginScrollView(
            actionMenuScrollPosition,
            GUILayout.Width(rect.width - 10f),
            GUILayout.Height(rect.height - 50f));
        DrawActionMenuContent(menuUnit);
        GUILayout.EndScrollView();
        if (selectedUnit == menuUnit && !string.IsNullOrEmpty(statusMessage))
            GUILayout.Label(statusMessage);
        GUILayout.EndArea();
        GUI.enabled = true;
    }

    private void DrawActionMenuContent(UnitInfo menuUnit)
    {
        if (selectedUnit != menuUnit) return;
        bool ready = IsUnitReady(menuUnit);
        GUILayout.Label(ready ? "行动状态：就绪" : $"行动状态：等待 {Mathf.Max(0, Mathf.CeilToInt(menuUnit.nextReadyTime - battleTimeAP))} AP");

        GUI.enabled = ready && !menuUnit.hasMovedThisActivation && !attackMode;
        if (GUILayout.Button("移动（可选；不结束行动）"))
        {
            moveMode = true;
            attackMode = false;
            actionMenuHiddenForTargetSelection = true;
            RefreshMovementHighlights();
            statusMessage = "行动菜单已隐藏：点击青色地块移动；移动后仍需执行非移动行动。";
        }
        GUI.enabled = ready && menuUnit.attack > 0;
        DrawConfiguredActionButtons(ready);
        if (selectedUnit != menuUnit) return;

        bool canCaptureBuilding = buildingEconomy != null &&
            buildingEconomy.CanCaptureAt(menuUnit.q, menuUnit.r, menuUnit.team);
        bool canCaptureLegacyCity = battlefield.IsCity(menuUnit.q, menuUnit.r) &&
            battlefield.GetCityOwner(menuUnit.q, menuUnit.r) != menuUnit.team;
        GUI.enabled = ready && !attackMode && menuUnit.canCapture && (canCaptureBuilding || canCaptureLegacyCity);
        if (GUILayout.Button($"占领建筑/城市（{menuUnit.captureAP} AP）")) OccupyCity();
        if (selectedUnit != menuUnit) return;

        GUI.enabled = ready && !attackMode;
        if (CanUnitTransport(menuUnit) && GUILayout.Button(transportMode ? "取消装载/卸载" : "装载/卸载（100 AP）"))
        {
            transportMode = !transportMode;
            supportAction = "";
            supportMode = false;
            attackMode = false;
            ClearHighlights();
            if (transportMode)
            {
                moveMode = false;
                actionMenuHiddenForTargetSelection = true;
                HighlightTransportTargets();
                statusMessage = "运输模式：行动菜单已隐藏；点击橙色高亮目标装载或卸载，Esc 或右键取消。";
            }
            else
            {
                actionMenuHiddenForTargetSelection = false;
                moveMode = !menuUnit.hasMovedThisActivation;
                if (moveMode) RefreshMovementHighlights();
                statusMessage = "已退出运输模式。";
            }
        }
        DrawSupportButtons(ready);
        if (selectedUnit != menuUnit) return;

        GUI.enabled = ready && !attackMode && !transportMode && string.IsNullOrEmpty(supportAction);
        if (GUILayout.Button($"休整（{menuUnit.restAP} AP；结束行动）")) Rest();
        if (selectedUnit != menuUnit) return;

        if (menuUnit.unitId == "fortress" && GUILayout.Button(menuUnit.fortressGarrisoned ? "要塞切换机动形态（0 AP）" : "要塞切换驻守形态（0 AP）"))
        {
            menuUnit.fortressGarrisoned = !menuUnit.fortressGarrisoned;
            statusMessage = menuUnit.fortressGarrisoned ? "要塞已进入驻守形态，可攻击。" : "要塞已进入机动形态，无法攻击。";
            FinishPlayerActivation(0);
        }
        if (selectedUnit != menuUnit) return;

        GUI.enabled = ready;
        if (attackMode && GUILayout.Button("取消攻击"))
        {
            attackMode = false;
            moveMode = !menuUnit.hasMovedThisActivation;
            RefreshMovementHighlights();
            statusMessage = "已取消攻击模式。";
        }
        GUI.enabled = true;

        if (menuUnit.cargo.Count > 0)
            foreach (UnitInfo passenger in menuUnit.cargo)
                GUILayout.Label("载员：" + passenger.unitName);
        if (attackMode) GUILayout.Label("当前攻击行动：" + selectedAttackName);
        if (transportMode) GUILayout.Label("运输模式：点击相邻友军装载，或点击相邻空地卸载。");
        if (!string.IsNullOrEmpty(supportAction)) GUILayout.Label("支援模式：" + supportAction + "；点击相邻友军。");
        GUILayout.Label(menuUnit.hasMovedThisActivation ? "已移动：仍须执行一个非移动行动。" : "移动为可选行动；非移动行动结束本次行动。");
    }

    private string ActionDescriptions(UnitInfo unit)
    {
        var list=new List<string>();
        if(unit.actions!=null) foreach(UnitActionProfile action in unit.actions)
            if(action!=null && !string.IsNullOrWhiteSpace(action.description)) list.Add(action.description);
        return string.Join("；",list);
    }

    private void DrawConfiguredActionButtons(bool ready)
    {
        GUI.enabled = ready && !transportMode && string.IsNullOrEmpty(supportAction);
        switch (selectedUnit.unitId)
        {
            case "infantry":
                if (GUILayout.Button($"步枪（50 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "步枪", 1f, HexAttackType.Bullet, 50);
                if (GUILayout.Button($"手雷（150%爆炸，50 AP；射程 {ConfiguredRangeLabel(1)}）")) BeginProfiledAttack(1, "手雷", 1.5f, HexAttackType.Explosion, 50);
                break;
            case "heavy_infantry":
                if (GUILayout.Button($"机枪（60 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "机枪", 1f, HexAttackType.Bullet, 60);
                if (GUILayout.Button($"集束手雷（200%，60 AP；射程 {ConfiguredRangeLabel(1)}）")) BeginProfiledAttack(1, "集束手雷", 2f, HexAttackType.Explosion, 60);
                break;
            case "engineer":
                if (GUILayout.Button($"步枪（40 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "步枪", 1f, HexAttackType.Bullet, 40);
                break;
            case "sniper":
                if (GUILayout.Button($"狙击（80 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "狙击", 1f, HexAttackType.Bullet, 80);
                break;
            case "spy":
                if (GUILayout.Button($"刺杀（真实伤害；100 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "刺杀", 1f, HexAttackType.Bullet, 100, false, true);
                break;
            case "scout":
            case "apc":
            case "transport_aircraft":
            case "transport_ship":
                if (GUILayout.Button($"机枪/机炮（50 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "机枪/机炮", 1f, HexAttackType.Bullet, 50);
                break;
            case "tank":
            case "heavy_tank":
            case "self_propelled_gun":
            case "rockets":
            case "battleship":
            case "bomber":
                if (GUILayout.Button($"主武器攻击（80 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "主武器攻击", 1f, HexAttackType.Explosion, 80);
                break;
            case "sub":
                if (GUILayout.Button($"鱼雷（仅海军，80 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "鱼雷", 1f, HexAttackType.Explosion, 80, true);
                break;
            case "anti_air":
                if (GUILayout.Button($"防空炮（仅空军，80 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "防空炮", 1f, HexAttackType.Bullet, 80, false, false, true);
                break;
            case "fortress":
                if (GUILayout.Button($"驻守炮击（80 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "驻守炮击", 1f, HexAttackType.Explosion, 80);
                break;
            case "fighter":
                if (GUILayout.Button($"机枪（50%，50 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "机枪", 0.5f, HexAttackType.Bullet, 50);
                if (GUILayout.Button($"空袭（80 AP；射程 {ConfiguredRangeLabel(1)}）")) BeginProfiledAttack(1, "空袭", 1f, HexAttackType.Explosion, 80);
                break;
            case "torpedo_plane":
                if (GUILayout.Button($"鱼雷攻击（仅海军，80 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "鱼雷攻击", 1f, HexAttackType.Explosion, 80, true);
                break;
            case "destroyer":
                if (GUILayout.Button($"舰炮（50%，50 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "舰炮", 0.5f, HexAttackType.Bullet, 50);
                if (GUILayout.Button($"鱼雷（80 AP；射程 {ConfiguredRangeLabel(1)}）")) BeginProfiledAttack(1, "鱼雷", 1f, HexAttackType.Explosion, 80);
                break;
            case "cruiser":
                if (GUILayout.Button($"舰炮（50%，50 AP；射程 {ConfiguredRangeLabel(0)}）")) BeginProfiledAttack(0, "舰炮", 0.5f, HexAttackType.Bullet, 50);
                if (GUILayout.Button($"鱼雷/重炮（80 AP；射程 {ConfiguredRangeLabel(1)}）")) BeginProfiledAttack(1, "鱼雷/重炮", 1f, HexAttackType.Explosion, 80);
                break;
        }
        GUI.enabled = true;
    }

    private bool CanUnitTransport(UnitInfo u) => u!=null && u.resourceCapacity>0 && (u.unitId=="apc"||u.unitId=="transport_aircraft"||u.unitId=="transport_ship");

    private void HighlightTransportTargets()
    {
        var targets = new List<Vector2Int>();
        if (selectedUnit == null) return;
        Vector2Int origin = new Vector2Int(selectedUnit.q, selectedUnit.r);

        if (selectedUnit.cargo.Count > 0)
        {
            UnitInfo passenger = selectedUnit.cargo[0];
            foreach (Vector2Int offset in Neighbors)
            {
                Vector2Int candidate = origin + offset;
                if (battlefield.IsInsideMap(candidate.x, candidate.y) &&
                    FindUnitAt(candidate.x, candidate.y) == null &&
                    battlefield.IsTileWalkable(candidate.x, candidate.y, passenger.unitClass))
                    targets.Add(candidate);
            }
        }
        else
        {
            foreach (UnitInfo unit in units)
            {
                if (unit == null || unit == selectedUnit || unit.team != selectedUnit.team ||
                    unit.carrier != null || unit.unitClass == HexUnitClass.Air ||
                    unit.unitClass == HexUnitClass.Naval) continue;
                if (HexDistance(origin, new Vector2Int(unit.q, unit.r)) <= 1)
                    targets.Add(new Vector2Int(unit.q, unit.r));
            }
        }
        if (battlefield != null) battlefield.SetActionHighlights(targets);
    }

    private void DrawSupportButtons(bool ready)
    {
        if(!ready||selectedUnit==null||attackMode||transportMode) return;
        if(selectedUnit.unitId=="medic")
        { if(GUILayout.Button("治疗友军（恢复损血的50%，100 AP）")) BeginSupportMode("heal"); if(GUILayout.Button("鼓舞友军（等待AP减半，80 AP）")) BeginSupportMode("inspire"); }
        if(selectedUnit.unitId=="apc"||selectedUnit.unitId=="transport_ship")
        { if(selectedUnit.currentFuel>=5&&GUILayout.Button("补给燃料 +5（80 AP）")) BeginSupportMode("fuel"); if(selectedUnit.currentAmmo>=2&&GUILayout.Button("补给弹药 +2（80 AP）")) BeginSupportMode("ammo"); }
        if((selectedUnit.unitId=="refuel_aircraft"||selectedUnit.unitId=="aircraft_carrier")&&selectedUnit.currentFuel>=5&&GUILayout.Button("加油 +5（80 AP）")) BeginSupportMode("fuel");
    }
    private void BeginSupportMode(string action)
    {
        supportAction = action;
        supportMode = true;
        attackMode = false;
        transportMode = false;
        moveMode = false;
        actionMenuHiddenForTargetSelection = true;
        ClearHighlights();
        var targets = new List<Vector2Int>();
        if (selectedUnit != null)
        {
            foreach (UnitInfo unit in units)
            {
                if (unit == null || unit == selectedUnit || unit.team != selectedUnit.team || unit.carrier != null)
                    continue;
                if (HexDistance(new Vector2Int(selectedUnit.q, selectedUnit.r),
                    new Vector2Int(unit.q, unit.r)) <= 1)
                    targets.Add(new Vector2Int(unit.q, unit.r));
            }
        }
        if (battlefield != null) battlefield.SetActionHighlights(targets);
        statusMessage = "支援模式：" + action + "。行动菜单已隐藏；点击橙色高亮友军，Esc 或右键取消。";
    }
    private void ExecuteSupportAction(UnitInfo target)
    {
        if(selectedUnit==null||target==null||target.team!=selectedUnit.team||HexDistance(new Vector2Int(selectedUnit.q,selectedUnit.r),new Vector2Int(target.q,target.r))>1) { statusMessage="支援目标必须是相邻友军。"; return; }
        int cost=80;
        if(supportAction=="heal") { int amount=Mathf.FloorToInt(Mathf.Max(0,target.maxHp-target.hp)*0.5f); if(amount<=0){statusMessage="目标不需要治疗。";return;} target.hp=Mathf.Min(target.maxHp,target.hp+amount);cost=100;statusMessage=$"{target.unitName}恢复 {amount} HP。"; }
        else if(supportAction=="inspire") { float remain=Mathf.Max(0,target.nextReadyTime-battleTimeAP);if(remain<=0){statusMessage="目标已就绪。";return;}ReserveNextActionNode(target,battleTimeAP+remain*0.5f);statusMessage=$"{target.unitName}等待AP减半。"; }
        else if(supportAction=="fuel") { if(selectedUnit.currentFuel<5||target.fuelCapacity<=0||target.currentFuel>=target.fuelCapacity){statusMessage="燃料补给条件不满足。";return;}int n=Mathf.Min(5,target.fuelCapacity-target.currentFuel);selectedUnit.currentFuel-=n;target.currentFuel+=n;statusMessage=$"补充燃料 {n}。"; }
        else if(supportAction=="ammo") { if(selectedUnit.currentAmmo<2||target.ammoCapacity<=0||target.currentAmmo>=target.ammoCapacity){statusMessage="弹药补给条件不满足。";return;}int n=Mathf.Min(2,target.ammoCapacity-target.currentAmmo);selectedUnit.currentAmmo-=n;target.currentAmmo+=n;statusMessage=$"补充弹药 {n}。"; }
        supportAction="";supportMode=false;FinishPlayerActivation(cost);
    }
    private void TryLoadCargo(UnitInfo passenger)
    {
        if(selectedUnit==null||!CanUnitTransport(selectedUnit)||passenger.team!=selectedUnit.team||passenger.carrier!=null||passenger.unitClass==HexUnitClass.Air||passenger.unitClass==HexUnitClass.Naval){statusMessage="只能装载相邻、同阵营的陆地单位。";return;}
        if(selectedUnit.cargo.Count>=selectedUnit.resourceCapacity){statusMessage="运载容量已满。";return;}
        if(HexDistance(new Vector2Int(selectedUnit.q,selectedUnit.r),new Vector2Int(passenger.q,passenger.r))>1){statusMessage="载员必须相邻。";return;}
        selectedUnit.cargo.Add(passenger);passenger.carrier=selectedUnit;if(passenger.renderer!=null)passenger.renderer.enabled=false;transportMode=false;statusMessage=passenger.unitName+"已装载。";FinishPlayerActivation(100);
    }
    private void TryUnloadCargo(int q,int r)
    {
        if(selectedUnit==null||!CanUnitTransport(selectedUnit)||selectedUnit.cargo.Count==0){statusMessage="运输单位没有载员。";return;}
        if(HexDistance(new Vector2Int(selectedUnit.q,selectedUnit.r),new Vector2Int(q,r))>1){statusMessage="卸载地块须相邻。";return;}
        UnitInfo passenger=selectedUnit.cargo[0];
        if(!battlefield.IsInsideMap(q,r)||FindUnitAt(q,r)!=null||!battlefield.IsTileWalkable(q,r,passenger.unitClass)){statusMessage="此地块不可卸载。";return;}
        selectedUnit.cargo.RemoveAt(0);passenger.carrier=null;passenger.q=q;passenger.r=r;ReserveNextActionNode(passenger,Mathf.Max(battleTimeAP+1f,passenger.nextReadyTime));if(passenger.visual!=null)passenger.visual.localPosition=HexToLocalPosition(q,r);if(passenger.renderer!=null)passenger.renderer.enabled=true;transportMode=false;statusMessage=passenger.unitName+"已卸载。";FinishPlayerActivation(100);
    }

    private string TerrainAPCost(int q,int r,HexUnitClass type)
    {
        int value=battlefield.GetMovementCostAP(q,r,type);
        return value==int.MaxValue?"不可通过":value.ToString();
    }

    private Sprite CreatePixelUnitSprite(Color32 body, Color32 accent)
    {
        const int w = 16, h = 20;
        Texture2D texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { name = "GeneratedPixelUnitTexture", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        Color32 clear = new Color32(0, 0, 0, 0), outline = new Color32(24, 26, 34, 255), shadow = new Color32(45, 48, 60, 255);
        Color32[] pixels = new Color32[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;
        FillRect(pixels, w, h, 3, 0, 7, 5, outline); FillRect(pixels, w, h, 4, 1, 6, 4, body);
        FillRect(pixels, w, h, 8, 1, 10, 4, outline); FillRect(pixels, w, h, 9, 2, 10, 4, body);
        FillRect(pixels, w, h, 1, 7, 4, 12, outline); FillRect(pixels, w, h, 2, 8, 3, 11, body);
        FillRect(pixels, w, h, 10, 7, 13, 12, outline); FillRect(pixels, w, h, 11, 8, 12, 11, body);
        FillRect(pixels, w, h, 4, 5, 11, 13, outline); FillRect(pixels, w, h, 5, 6, 10, 12, body);
        FillRect(pixels, w, h, 6, 9, 9, 10, accent); FillRect(pixels, w, h, 4, 13, 11, 19, outline);
        FillRect(pixels, w, h, 5, 14, 10, 18, body); FillRect(pixels, w, h, 5, 15, 10, 16, accent);
        FillRect(pixels, w, h, 6, 17, 9, 18, shadow);
        texture.SetPixels32(pixels); texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 0.02f), 20f);
    }

    private void FillRect(Color32[] pixels, int width, int height, int xMin, int yMin, int xMax, int yMax, Color32 color)
    {
        for (int y = Mathf.Max(0, yMin); y <= Mathf.Min(height - 1, yMax); y++)
        for (int x = Mathf.Max(0, xMin); x <= Mathf.Min(width - 1, xMax); x++)
            pixels[y * width + x] = color;
    }
}