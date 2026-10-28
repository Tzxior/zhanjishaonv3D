using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public enum HexAttackType { Bullet, Explosion }

/// <summary>
/// Unit selection, terrain-cost movement, optional movement followed by a required non-movement action,
/// discrete AP scheduling, simple attacks, city capture, and basic enemy AI.
/// Simulation AP never advances with wall-clock time. WaitForSecondsRealtime is used only for presentation.
/// </summary>
public class HexUnitDemo : MonoBehaviour
{
    [Header("Starting Positions")]
    public Vector2Int[] blueStartingPositions = { new Vector2Int(1, 1), new Vector2Int(2, 3) };
    public Vector2Int[] redStartingPositions = { new Vector2Int(7, 6), new Vector2Int(7, 7) };

    [System.Serializable] public class UnitBattleProfile
    {
        public string id = "infantry";
        public string unitName = "步兵";
        public HexUnitClass unitClass = HexUnitClass.Infantry;
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
    [Header("Individual Unit Profiles (array order matches starting positions)")]
    [Tooltip("每个部署单位独立配置，增加条目即可继续制作原作的15个主要单位。")]
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

        public string unitName;
        public HexAttackType attackType;
        public int hp=100, maxHp=100, attack=20;
        public int bulletDefense=10, explosionDefense=8;
        public int movementPower=6, movementAPLimit=100;
        public int attackAP=100, captureAP=50, restAP=0, attackRange=1;
        public bool canCapture;
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
    private HexBattlefieldDemo battlefield;
    private float battleTimeAP;
    private bool simulationRunning;
    private bool moveMode = true;
    private bool attackMode;
    private bool hasSelectedTile;
    private int selectedTileQ;
    private int selectedTileR;
    private string statusMessage = "选择我方单位开始行动。";

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
        var blue = LoadDirectionalFrames(blueFramesResourcePath, "我方",
            new Color32(70, 155, 255, 255), new Color32(235, 245, 255, 255));
        var red = LoadDirectionalFrames(redFramesResourcePath, "敌方",
            new Color32(235, 75, 70, 255), new Color32(255, 225, 190, 255));

        LoadUnitProfileDatabase();
        SpawnStartingUnits(blueStartingPositions, blueUnitProfiles, blueStartingProfileIds, 0, blue);
        SpawnStartingUnits(redStartingPositions, redUnitProfiles, redStartingProfileIds, 1, red);
        statusMessage = "我方操作阶段：移动可选；必须再执行攻击、占领或休整才能结束本次行动。";
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
        if (WasRightClickPressed()) { DeselectAll(); return; }
        if (!simulationRunning && WasLeftClickPressed() && !PointerOverHud())
            HandleMapClick();
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

    private bool WasRightClickPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(1);
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

    private bool PointerOverHud()
    {
        Vector2 p = GetPointerPosition();
        return p.x <= 400f && p.y >= Screen.height - 640f;
    }

    private void HandleMapClick()
    {
        if(mapCamera==null)mapCamera=Camera.main;if(mapCamera==null)return;
        Ray ray=mapCamera.ScreenPointToRay(GetPointerPosition());
        if(!Physics.Raycast(ray,out RaycastHit hit,1000f))return;
        if(!TryReadHexCoordinates(hit.collider.gameObject.name,out int q,out int r))return;
        UnitInfo clicked=FindUnitAt(q,r);
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
                moveMode=false;attackMode=false;ClearHighlights();
                statusMessage=$"移动完成：消耗 {routeMove} 移动力、暂记 {routeAP} AP；移动不结束行动，请继续选择非移动行动。";return;
            }
            statusMessage="已选中地块。移动是可选行动；本次行动仍需以攻击、占领或休整结束。";return;
        }
        statusMessage="已选中地块，可查看地形类型、移动力消耗与地块加成。";
    }

    private void DeselectAll()
    {
        if(selectedUnit!=null&&selectedUnit.visual!=null)selectedUnit.visual.localScale=selectedUnit.baseScale;
        selectedUnit=null;hasSelectedTile=false;moveMode=false;attackMode=false;ClearHighlights();
        statusMessage="已取消选择单位和地块。";
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
                unitName=string.IsNullOrWhiteSpace(p.unitName)?"未命名单位":p.unitName,
                hp=Mathf.Max(1,p.maxHP),maxHp=Mathf.Max(1,p.maxHP),attack=Mathf.Max(0,p.attackPower),attackType=p.attackType,
                bulletDefense=Mathf.Max(0,p.bulletDefense),explosionDefense=Mathf.Max(0,p.explosionDefense),
                movementPower=Mathf.Max(1,p.movementPower),movementAPLimit=Mathf.Max(1,p.movementAPLimit),
                attackAP=Mathf.Max(0,p.attackAP),captureAP=Mathf.Max(0,p.captureAP),restAP=Mathf.Max(0,p.restAP),attackRange=Mathf.Max(1,p.attackRange),canCapture=p.canCapture};
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
        if (unit.visual != null)
            unit.visual.localScale = unit.baseScale * selectedScaleMultiplier;

        moveMode = unit.team == 0 && IsUnitReady(unit) && !unit.hasMovedThisActivation && !simulationRunning;
        attackMode = false;
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
        battlefield.SetMovementHighlights(null);
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

    private void MoveUnitAlongPath(UnitInfo unit, List<Vector2Int> path)
    {
        foreach (Vector2Int point in path)
        {
            unit.currentDirection = GetDirectionForMove(point.x - unit.q, point.y - unit.r);
            unit.q = point.x;
            unit.r = point.y;
            unit.visual.localPosition = HexToLocalPosition(unit.q, unit.r);
            unit.lastFrame = -1;
        }
    }

    private int GetAttackDefense(UnitInfo target,HexAttackType type)=>type==HexAttackType.Bullet?target.bulletDefense:target.explosionDefense;
    private string AttackTypeName(HexAttackType type)=>type==HexAttackType.Bullet?"子弹":"爆炸";

    private bool IsUnitReady(UnitInfo unit)
    {
        if (unit == null || unit.nextReadyTime > battleTimeAP + 0.001f) return false;
        // A zero-AP action still consumes this unit's current activation, without inventing AP cost.
        return Mathf.Abs(unit.zeroCostActedAtTime - battleTimeAP) > 0.001f;
    }

    private void BeginAttackMode()
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        moveMode = false;
        attackMode = true;
        ClearHighlights();
        statusMessage = $"攻击模式：攻击范围 {selectedUnit.attackRange} 格。点击范围内敌军。";
    }

    private void ExecutePlayerAttack(UnitInfo target)
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
        int distance = HexDistance(new Vector2Int(selectedUnit.q, selectedUnit.r), new Vector2Int(target.q, target.r));
        if (distance > selectedUnit.attackRange)
        {
            statusMessage = $"目标超出攻击范围（当前距离 {distance} 格）。";
            return;
        }

        int terrainDefense=battlefield.GetDefenseBonus(target.q,target.r);
        int damage=Mathf.Max(1,selectedUnit.attack-GetAttackDefense(target,selectedUnit.attackType)-terrainDefense);
        target.hp-=damage;int actionCost=selectedUnit.attackAP;
        statusMessage=$"{selectedUnit.unitName}使用{AttackTypeName(selectedUnit.attackType)}攻击，造成 {damage} 伤害；本次攻击消耗 {actionCost} AP。";
        if (target.hp <= 0)
        {
            units.Remove(target);
            if (target.visual != null) Destroy(target.visual.gameObject);
            statusMessage += " 敌方单位被击破。";
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
        if (!battlefield.IsCity(selectedUnit.q, selectedUnit.r))
        {
            statusMessage = "当前地块不是城市，无法执行占领。";
            return;
        }
        if (battlefield.GetCityOwner(selectedUnit.q, selectedUnit.r) == 0)
        {
            statusMessage = "这座城市已经属于我方。";
            return;
        }

        battlefield.CaptureCity(selectedUnit.q, selectedUnit.r, 0);
        int cost=selectedUnit.captureAP;
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

        if (totalCost == 0)
            acted.zeroCostActedAtTime = battleTimeAP;
        else
            acted.zeroCostActedAtTime = -1f;

        acted.nextReadyTime = battleTimeAP + totalCost;
        acted.pendingMoveAP = 0;
        acted.hasMovedThisActivation = false;
        selectedUnit = null;
        moveMode = false;
        attackMode = false;
        ClearHighlights();

        statusMessage = $"本次行动结束：移动与行动合计消耗 {totalCost} AP；下次可行动时间为 AP {acted.nextReadyTime:0}。";
        if (!HasReadyBlueUnit())
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
        while (!HasReadyBlueUnit() && safety++ < 300)
        {
            UnitInfo readyEnemy = null;
            foreach (UnitInfo unit in units)
                if (unit.team == 1 && IsUnitReady(unit)) { readyEnemy = unit; break; }

            if (readyEnemy != null)
            {
                yield return StartCoroutine(PerformEnemyActivation(readyEnemy));
                continue;
            }

            float nextTime = float.PositiveInfinity;
            foreach (UnitInfo unit in units)
                if (unit.nextReadyTime > battleTimeAP + 0.001f && unit.nextReadyTime < nextTime)
                    nextTime = unit.nextReadyTime;

            if (float.IsInfinity(nextTime))
            {
                statusMessage = "没有未来 AP 事件。请检查是否所有单位都执行了零 AP 休整。";
                break;
            }

            // Discrete event jump: never based on Time.deltaTime.
            battleTimeAP = nextTime;
        }

        if (safety >= 300) Debug.LogWarning("Simulation safety limit reached.");
        simulationRunning = false;
        selectedUnit = null;
        statusMessage = HasReadyBlueUnit()
            ? "战场时间已推进；现在有我方单位可以行动。"
            : statusMessage;
    }

    private IEnumerator PerformEnemyActivation(UnitInfo enemy)
    {
        if (enemy == null) yield break;
        UnitInfo target = FindNearestBlue(enemy.q, enemy.r);
        if (target == null)
        {
            enemy.nextReadyTime = battleTimeAP + blockedEnemyWaitAP;
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
            }
        }

        target = FindNearestBlue(enemy.q, enemy.r);
        int actionCost = 0;
        if (target != null &&
            HexDistance(new Vector2Int(enemy.q, enemy.r), new Vector2Int(target.q, target.r)) <= enemy.attackRange)
        {
            int damage=Mathf.Max(1,enemy.attack-GetAttackDefense(target,enemy.attackType)-battlefield.GetDefenseBonus(target.q,target.r));
            target.hp-=damage;actionCost=enemy.attackAP;
            statusMessage = $"敌方攻击我方单位，造成 {damage} 伤害。";
            if (target.hp <= 0)
            {
                units.Remove(target);
                if (target.visual != null) Destroy(target.visual.gameObject);
                statusMessage += " 我方单位被击破。";
            }
            if (enemyActionDisplayDelay > 0f)
                yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
        }
        else if (battlefield.IsCity(enemy.q, enemy.r) && battlefield.GetCityOwner(enemy.q, enemy.r) != 1 && enemy.unitClass != HexUnitClass.Air)
        {
            battlefield.CaptureCity(enemy.q, enemy.r, 1);
            actionCost=enemy.captureAP;
            statusMessage = $"敌方占领城市，消耗 {actionCost} AP。";
            if (enemyActionDisplayDelay > 0f)
                yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
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

        int totalCost = moveCost + actionCost;
        enemy.nextReadyTime = battleTimeAP + totalCost;
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
            if (unit.team != 0) continue;
            int d = HexDistance(new Vector2Int(q, r), new Vector2Int(unit.q, unit.r));
            if (d < distance) { distance = d; best = unit; }
        }
        return best;
    }

    private bool HasReadyBlueUnit()
    {
        foreach (UnitInfo unit in units)
            if (unit.team == 0 && IsUnitReady(unit)) return true;
        return false;
    }

    private UnitInfo FindUnitAt(int q, int r)
    {
        foreach (UnitInfo unit in units)
            if (unit.q == q && unit.r == r) return unit;
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

    private void OnGUI()
    {
        if(!showBattleHUD)return;
        GUILayout.BeginArea(new Rect(12f,12f,400f,640f),GUI.skin.box);
        GUILayout.Label($"战场 AP：{battleTimeAP:0}");
        GUILayout.Label(simulationRunning?"阶段：敌方行动（模拟时间离散推进）":"阶段：我方操作（模拟时间暂停）");
        if(selectedUnit!=null)
        {
            GUILayout.Label(selectedUnit.team==0?"我方单位属性":"敌方单位属性（只读）");
            GUILayout.Label($"单位：{selectedUnit.unitName}　兵种：{ClassName(selectedUnit.unitClass)}");
            GUILayout.Label($"阵营：{(selectedUnit.team==0?"我方":"敌方")}　坐标：({selectedUnit.q},{selectedUnit.r})");
            GUILayout.Label($"生命：{selectedUnit.hp}/{selectedUnit.maxHp}（{100f*selectedUnit.hp/Mathf.Max(1,selectedUnit.maxHp):0}%）");
            GUILayout.Label($"攻击力：{selectedUnit.attack}　攻击类型：{AttackTypeName(selectedUnit.attackType)}　范围：{selectedUnit.attackRange}");
            GUILayout.Label($"子弹防御：{selectedUnit.bulletDefense}　爆炸防御：{selectedUnit.explosionDefense}");
            GUILayout.Label($"移动力：{selectedUnit.movementPower}　暂记移动 AP：{selectedUnit.pendingMoveAP}");
            GUILayout.Label($"占领能力：{(selectedUnit.canCapture ? "可以" : "不可以")}");
            GUILayout.Label(IsUnitReady(selectedUnit)?"行动状态：就绪":$"恢复中，剩余 {Mathf.CeilToInt(selectedUnit.nextReadyTime-battleTimeAP)} AP");
            if(selectedUnit.team==0&&!simulationRunning)
            {
                bool ready=IsUnitReady(selectedUnit);
                GUI.enabled=ready&&!selectedUnit.hasMovedThisActivation&&!attackMode;
                if(GUILayout.Button("移动（可选；不结束行动）"))
                {moveMode=true;attackMode=false;RefreshMovementHighlights();statusMessage="点击青色地块移动；移动后仍需执行非移动行动。";}
                GUI.enabled=ready;
                if(GUILayout.Button($"攻击（{selectedUnit.attackAP} AP；范围 {selectedUnit.attackRange}）"))BeginAttackMode();
                GUI.enabled=ready&&!attackMode&&battlefield.IsCity(selectedUnit.q,selectedUnit.r)&&
                    battlefield.GetCityOwner(selectedUnit.q,selectedUnit.r)!=0&&selectedUnit.canCapture;
                if(GUILayout.Button($"占领城市（{selectedUnit.captureAP} AP）"))OccupyCity();
                GUI.enabled=ready;
                if(GUILayout.Button($"休整（{selectedUnit.restAP} AP；结束行动）"))Rest();
                if(attackMode&&GUILayout.Button("取消攻击"))
                {attackMode=false;moveMode=selectedUnit!=null&&!selectedUnit.hasMovedThisActivation;RefreshMovementHighlights();statusMessage="已取消攻击模式。";}
                GUI.enabled=true;
                GUILayout.Label(selectedUnit.hasMovedThisActivation?"已移动：仍须执行攻击、占领或休整。":"移动为可选行动；非移动行动结束本次行动。");
            }
        }
        else GUILayout.Label("未选中单位。点击单位查看属性，点击空地块查看地形。");

        if(hasSelectedTile)
        {
            GUILayout.Space(6);GUILayout.Label("地块属性");
            GUILayout.Label($"坐标：({selectedTileQ},{selectedTileR})　类型：{battlefield.GetTerrainDisplayName(selectedTileQ,selectedTileR)}");
            GUILayout.Label($"移动力消耗：步兵 {battlefield.GetMovementPointCost(selectedTileQ,selectedTileR,HexUnitClass.Infantry)}；重型 {battlefield.GetMovementPointCost(selectedTileQ,selectedTileR,HexUnitClass.HeavyGround)}；空军 {battlefield.GetMovementPointCost(selectedTileQ,selectedTileR,HexUnitClass.Air)}");
            GUILayout.Label($"地块 AP 成本：步兵 {TerrainAPCost(selectedTileQ,selectedTileR,HexUnitClass.Infantry)}；空军 {TerrainAPCost(selectedTileQ,selectedTileR,HexUnitClass.Air)}");
            GUILayout.Label($"地块加成：{battlefield.GetTerrainBonusDescription(selectedTileQ,selectedTileR)}");
            if(battlefield.IsCity(selectedTileQ,selectedTileR))
            {int owner=battlefield.GetCityOwner(selectedTileQ,selectedTileR);GUILayout.Label($"城市归属：{(owner==0?"我方":owner==1?"敌方":"中立")}");}
        }
        GUILayout.Space(5);GUI.enabled=!simulationRunning&&!HasReadyBlueUnit();
        if(GUILayout.Button("推进战场时间 / 执行敌方行动"))StartCoroutine(RunSimulationUntilPlayerCanAct());
        GUI.enabled=true;GUILayout.Label(statusMessage);GUILayout.EndArea();
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