using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Unit selection, terrain-cost pathfinding, AP scheduling, player actions and simple enemy movement.
/// Battle AP advances only through the simulation scheduler, never from Time.deltaTime.
/// Visual waits in enemy animations do not change the simulated AP clock.
/// </summary>
public class HexUnitDemo : MonoBehaviour
{
    [Header("Starting Positions")]
    public Vector2Int[] blueStartingPositions = { new Vector2Int(1, 1), new Vector2Int(2, 3) };
    public Vector2Int[] redStartingPositions = { new Vector2Int(7, 6), new Vector2Int(7, 7) };

    [Header("Unit Classes (match order of starting positions)")]
    public HexUnitClass[] blueUnitClasses = { HexUnitClass.Infantry, HexUnitClass.HeavyGround };
    public HexUnitClass[] redUnitClasses = { HexUnitClass.Infantry, HexUnitClass.HeavyGround };

    [Header("Sprite Resources")]
    [Tooltip("Path relative to Resources, ending with the down_right folder.")]
    public string blueFramesResourcePath = "GadgetSprites/MU_00000/frames/down_right";
    [Tooltip("Empty uses a temporary red placeholder sprite.")]
    public string redFramesResourcePath = "";

    [Header("Animation & Appearance")]
    [Min(1f)] public float pixelsPerUnit = 48f;
    [Min(0.1f)] public float animationFramesPerSecond = 6f;
    [Min(0.1f)] public float iconScale = 1.6f;
    [Min(0f)] public float liftAboveTile = 0.12f;
    [Min(1f)] public float selectedScaleMultiplier = 1.2f;
    [Tooltip("Enemy animation display delay only. Does not affect AP.")]
    [Min(0f)] public float enemyActionDisplayDelay = 0.25f;

    [Header("AP Rules")]
    [Tooltip("Movement route AP budget for infantry. Example: six road tiles cost 60 AP.")]
    [Min(1)] public int infantryMoveAPBudget = 100;
    [Min(1)] public int heavyMoveAPBudget = 100;
    [Min(1)] public int airMoveAPBudget = 100;
    [Min(1)] public int infantryMaxSteps = 6;
    [Min(1)] public int heavyMaxSteps = 2;
    [Min(1)] public int airMaxSteps = 6;
    [Min(1)] public int cityOccupationCostAP = 50;
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
        public int hp = 100;
        public int attack = 20;
        public int defense = 10;
    }

    private class PathNode
    {
        public int cost;
        public int steps;
        public Vector2Int previous;
    }

    private readonly List<UnitInfo> units = new List<UnitInfo>();
    private readonly HashSet<Vector2Int> reachableTiles = new HashSet<Vector2Int>();
    private readonly Dictionary<Vector2Int, PathNode> currentPaths = new Dictionary<Vector2Int, PathNode>();

    private UnitInfo selectedUnit;
    private Camera mapCamera;
    private HexBattlefieldDemo battlefield;
    private float battleTimeAP;
    private bool simulationRunning;
    private bool moveMode = true;
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

        SpawnStartingUnits(blueStartingPositions, blueUnitClasses, 0, blue);
        SpawnStartingUnits(redStartingPositions, redUnitClasses, 1, red);
        statusMessage = "时间暂停在我方操作阶段。选择一支我方单位。";
        Debug.Log("AP 战场调度启动：模拟时间只在推进时间/敌方行动阶段变化。");
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
        // OnGUI uses top-left origin; pointer input uses bottom-left origin.
        return p.x <= 380f && p.y >= Screen.height - 350f;
    }

    private void HandleMapClick()
    {
        if (mapCamera == null) mapCamera = Camera.main;
        if (mapCamera == null) return;

        Ray ray = mapCamera.ScreenPointToRay(GetPointerPosition());
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f)) return;
        if (!TryReadHexCoordinates(hit.collider.gameObject.name, out int q, out int r)) return;

        UnitInfo clickedUnit = FindUnitAt(q, r);
        if (clickedUnit != null)
        {
            SelectUnit(clickedUnit);
            return;
        }

        if (selectedUnit == null || selectedUnit.team != 0)
        {
            statusMessage = "请先选择我方单位。";
            return;
        }

        if (!moveMode)
        {
            statusMessage = "当前未选择移动行动。请在面板中点击“移动”。";
            return;
        }

        Vector2Int target = new Vector2Int(q, r);
        if (!reachableTiles.Contains(target))
        {
            if (!battlefield.IsInsideMap(q, r)) statusMessage = "目标超出地图边界。";
            else if (!battlefield.IsTileWalkable(q, r, selectedUnit.unitClass)) statusMessage = "该兵种无法进入目标地形。";
            else statusMessage = "目标不在本次移动范围内，或路径被阻挡。";
            return;
        }

        List<Vector2Int> path = ReconstructPath(target);
        if (path.Count == 0) return;

        int routeCost = currentPaths[target].cost;
        MoveUnitToPath(selectedUnit, path, false, () =>
        {
            selectedUnit.nextReadyTime = battleTimeAP + routeCost;
            statusMessage = $"移动完成：消耗 {routeCost} AP；该单位将在 AP {selectedUnit.nextReadyTime:0} 恢复。";
            ClearHighlights();
            if (!HasReadyBlueUnit()) StartCoroutine(RunSimulationUntilPlayerCanAct());
        });
    }

    private void SpawnStartingUnits(Vector2Int[] positions, HexUnitClass[] classes, int team,
        Dictionary<string, Sprite[]> animations)
    {
        if (positions == null) return;
        for (int i = 0; i < positions.Length; i++)
        {
            int q = positions[i].x, r = positions[i].y;
            if (!battlefield.IsInsideMap(q, r))
            {
                Debug.LogWarning($"单位初始坐标 ({q},{r}) 超出地图。");
                continue;
            }
            if (FindUnitAt(q, r) != null)
            {
                Debug.LogWarning($"地块 ({q},{r}) 已有单位，跳过重复部署。");
                continue;
            }

            HexUnitClass unitClass = classes != null && i < classes.Length ? classes[i] : HexUnitClass.Infantry;
            GameObject icon = new GameObject(team == 0 ? "BlueUnitIcon" : "RedUnitIcon");
            icon.transform.SetParent(transform, false);
            icon.transform.localPosition = HexToLocalPosition(q, r);
            icon.transform.localScale = Vector3.one * iconScale;

            SpriteRenderer sr = icon.AddComponent<SpriteRenderer>();
            var unit = new UnitInfo
            {
                q = q, r = r, team = team, unitClass = unitClass,
                visual = icon.transform, renderer = sr, directionalFrames = animations,
                currentDirection = "down_right", baseScale = Vector3.one * iconScale,
                animationOffset = (q * 3 + r * 7 + team * 5) * 0.037f,
                nextReadyTime = 0f,
                hp = unitClass == HexUnitClass.HeavyGround ? 140 : unitClass == HexUnitClass.Air ? 80 : 100,
                attack = unitClass == HexUnitClass.HeavyGround ? 30 : unitClass == HexUnitClass.Air ? 25 : 20,
                defense = unitClass == HexUnitClass.HeavyGround ? 20 : 10
            };
            sr.sprite = animations["down_right"][0];
            sr.sortingOrder = 10;
            units.Add(unit);
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

        moveMode = unit.team == 0 && IsUnitReady(unit) && !simulationRunning;
        RefreshMovementHighlights();
        statusMessage = unit.team == 0
            ? (IsUnitReady(unit) ? "我方单位已就绪，可选择行动。" : $"该单位恢复中，还需 {Mathf.CeilToInt(unit.nextReadyTime - battleTimeAP)} AP。")
            : "敌方单位：仅查看属性，不能直接下达命令。";
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

        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit) || simulationRunning)
        {
            battlefield.SetMovementHighlights(null);
            return;
        }

        Vector2Int start = new Vector2Int(selectedUnit.q, selectedUnit.r);
        currentPaths[start] = new PathNode { cost = 0, steps = 0, previous = start };
        var open = new List<Vector2Int> { start };
        int apBudget = GetAPBudget(selectedUnit.unitClass);
        int maxSteps = GetMaxSteps(selectedUnit.unitClass);

        while (open.Count > 0)
        {
            int bestIndex = 0;
            for (int i = 1; i < open.Count; i++)
                if (currentPaths[open[i]].cost < currentPaths[open[bestIndex]].cost) bestIndex = i;

            Vector2Int current = open[bestIndex];
            open.RemoveAt(bestIndex);
            PathNode currentNode = currentPaths[current];

            foreach (Vector2Int delta in Neighbors)
            {
                Vector2Int next = current + delta;
                if (!battlefield.IsInsideMap(next.x, next.y) ||
                    !battlefield.IsTileWalkable(next.x, next.y, selectedUnit.unitClass)) continue;

                UnitInfo occupant = FindUnitAt(next.x, next.y);
                if (occupant != null && occupant != selectedUnit) continue;

                int stepCost = battlefield.GetMovementCostAP(next.x, next.y, selectedUnit.unitClass);
                int nextCost = currentNode.cost + stepCost;
                int nextSteps = currentNode.steps + 1;
                if (nextSteps > maxSteps || nextCost > apBudget) continue;

                if (currentPaths.TryGetValue(next, out PathNode old) && old.cost <= nextCost) continue;

                currentPaths[next] = new PathNode { cost = nextCost, steps = nextSteps, previous = current };
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
        int safety = 0;
        while (cursor != start && safety++ < columnsForSafety())
        {
            path.Add(cursor);
            if (!currentPaths.TryGetValue(cursor, out PathNode node)) return new List<Vector2Int>();
            cursor = node.previous;
        }
        if (cursor != start) return new List<Vector2Int>();
        path.Reverse();
        return path;
    }

    private int columnsForSafety() => Mathf.Max(1, battlefield.columns * battlefield.rows + 1);
    private int GetAPBudget(HexUnitClass type) => type == HexUnitClass.Air ? airMoveAPBudget : type == HexUnitClass.HeavyGround ? heavyMoveAPBudget : infantryMoveAPBudget;
    private int GetMaxSteps(HexUnitClass type) => type == HexUnitClass.Air ? airMaxSteps : type == HexUnitClass.HeavyGround ? heavyMaxSteps : infantryMaxSteps;

    private void MoveUnitToPath(UnitInfo unit, List<Vector2Int> path, bool enemy, System.Action onComplete)
    {
        StartCoroutine(MovePathRoutine(unit, path, enemy, onComplete));
    }

    private IEnumerator MovePathRoutine(UnitInfo unit, List<Vector2Int> path, bool enemy, System.Action onComplete)
    {
        if (enemy) statusMessage = $"敌方{ClassName(unit.unitClass)}正在移动……";
        foreach (Vector2Int point in path)
        {
            int dq = point.x - unit.q, dr = point.y - unit.r;
            unit.currentDirection = GetDirectionForMove(dq, dr);
            unit.q = point.x; unit.r = point.y;
            unit.visual.localPosition = HexToLocalPosition(unit.q, unit.r);
            unit.lastFrame = -1;
            if (enemy && enemyActionDisplayDelay > 0f)
                yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
        }
        onComplete?.Invoke();
    }

    private void OccupyCity()
    {
        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit)) return;
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

        int q = selectedUnit.q, r = selectedUnit.r;
        battlefield.CaptureCity(q, r, 0);
        selectedUnit.nextReadyTime = battleTimeAP + cityOccupationCostAP;
        statusMessage = $"占领完成：城市已变为我方城市，消耗 {cityOccupationCostAP} AP。";
        RefreshMovementHighlights();
        if (!HasReadyBlueUnit()) StartCoroutine(RunSimulationUntilPlayerCanAct());
    }

    private IEnumerator RunSimulationUntilPlayerCanAct()
    {
        if (simulationRunning) yield break;
        simulationRunning = true;
        moveMode = false;
        ClearHighlights();
        statusMessage = "敌方正在计算并执行行动……";

        int safety = 0;
        while (!HasReadyBlueUnit() && safety++ < 300)
        {
            UnitInfo readyEnemy = null;
            foreach (UnitInfo unit in units)
            {
                if (unit.team == 1 && unit.nextReadyTime <= battleTimeAP + 0.001f)
                {
                    readyEnemy = unit;
                    break;
                }
            }

            if (readyEnemy != null)
            {
                yield return StartCoroutine(PerformEnemyAction(readyEnemy));
                continue;
            }

            float nextTime = float.PositiveInfinity;
            foreach (UnitInfo unit in units)
                if (unit.nextReadyTime > battleTimeAP + 0.001f && unit.nextReadyTime < nextTime)
                    nextTime = unit.nextReadyTime;

            if (float.IsInfinity(nextTime))
            {
                statusMessage = "没有待执行的行动事件。";
                break;
            }

            // Discrete simulation-time jump: no relationship to real elapsed seconds.
            battleTimeAP = nextTime;
        }

        if (safety >= 300)
            Debug.LogWarning("Simulation safety limit reached; check unit scheduling rules.");

        simulationRunning = false;
        if (selectedUnit != null && selectedUnit.team == 0 && IsUnitReady(selectedUnit))
        {
            moveMode = true;
            RefreshMovementHighlights();
        }
        else if (selectedUnit != null)
        {
            RefreshMovementHighlights();
        }

        statusMessage = HasReadyBlueUnit()
            ? "时间已推进。现在轮到可行动的我方单位。"
            : "当前没有可行动的我方单位；可以再次推进时间。";
    }

    private IEnumerator PerformEnemyAction(UnitInfo enemy)
    {
        if (enemy == null) yield break;

        UnitInfo target = FindNearestBlue(enemy.q, enemy.r);
        if (target == null)
        {
            enemy.nextReadyTime = battleTimeAP + blockedEnemyWaitAP;
            yield break;
        }

        // Capture if already standing on an unowned city.
        if (battlefield.IsCity(enemy.q, enemy.r) && battlefield.GetCityOwner(enemy.q, enemy.r) != 1)
        {
            battlefield.CaptureCity(enemy.q, enemy.r, 1);
            enemy.nextReadyTime = battleTimeAP + cityOccupationCostAP;
            statusMessage = "敌方占领了一座城市。";
            if (enemyActionDisplayDelay > 0f) yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
            yield break;
        }

        Dictionary<Vector2Int, PathNode> paths = CalculatePaths(enemy);
        Vector2Int best = new Vector2Int(enemy.q, enemy.r);
        int currentDistance = HexDistance(best, new Vector2Int(target.q, target.r));
        int bestDistance = currentDistance;
        int bestCost = 0;

        foreach (KeyValuePair<Vector2Int, PathNode> pair in paths)
        {
            if (pair.Key == best) continue;
            int distance = HexDistance(pair.Key, new Vector2Int(target.q, target.r));
            if (distance < bestDistance || (distance == bestDistance && distance < currentDistance && pair.Value.cost < bestCost))
            {
                best = pair.Key; bestDistance = distance; bestCost = pair.Value.cost;
            }
        }

        if (best == new Vector2Int(enemy.q, enemy.r) || bestDistance >= currentDistance)
        {
            enemy.nextReadyTime = battleTimeAP + blockedEnemyWaitAP;
            statusMessage = $"敌方{ClassName(enemy.unitClass)}保持位置，等待下一次行动。";
            if (enemyActionDisplayDelay > 0f) yield return new WaitForSecondsRealtime(enemyActionDisplayDelay);
            yield break;
        }

        List<Vector2Int> path = ReconstructPathFrom(paths, new Vector2Int(enemy.q, enemy.r), best);
        int cost = paths[best].cost;
        enemy.nextReadyTime = battleTimeAP + cost;
        yield return StartCoroutine(MovePathRoutine(enemy, path, true, null));
        statusMessage = $"敌方单位移动至 ({enemy.q}, {enemy.r})，消耗 {cost} AP。";
    }

    private Dictionary<Vector2Int, PathNode> CalculatePaths(UnitInfo unit)
    {
        var paths = new Dictionary<Vector2Int, PathNode>();
        var open = new List<Vector2Int>();
        Vector2Int start = new Vector2Int(unit.q, unit.r);
        paths[start] = new PathNode { cost = 0, steps = 0, previous = start };
        open.Add(start);
        while (open.Count > 0)
        {
            int bi = 0;
            for (int i = 1; i < open.Count; i++)
                if (paths[open[i]].cost < paths[open[bi]].cost) bi = i;
            Vector2Int current = open[bi]; open.RemoveAt(bi);
            PathNode node = paths[current];

            foreach (Vector2Int delta in Neighbors)
            {
                Vector2Int next = current + delta;
                if (!battlefield.IsInsideMap(next.x, next.y) ||
                    !battlefield.IsTileWalkable(next.x, next.y, unit.unitClass)) continue;
                UnitInfo occupied = FindUnitAt(next.x, next.y);
                if (occupied != null && occupied != unit) continue;
                int cost = node.cost + battlefield.GetMovementCostAP(next.x, next.y, unit.unitClass);
                int steps = node.steps + 1;
                if (steps > GetMaxSteps(unit.unitClass) || cost > GetAPBudget(unit.unitClass)) continue;
                if (paths.TryGetValue(next, out PathNode old) && old.cost <= cost) continue;
                paths[next] = new PathNode { cost = cost, steps = steps, previous = current };
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
            if (unit.team == 0 && unit.nextReadyTime <= battleTimeAP + 0.001f)
                return true;
        return false;
    }

    private bool IsUnitReady(UnitInfo unit) => unit != null && unit.nextReadyTime <= battleTimeAP + 0.001f;

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
            default: return "步兵";
        }
    }

    private void OnGUI()
    {
        if (!showBattleHUD) return;
        GUILayout.BeginArea(new Rect(12f, 12f, 360f, 330f), GUI.skin.box);
        GUILayout.Label($"战场 AP：{battleTimeAP:0}");
        GUILayout.Label(simulationRunning ? "阶段：敌方行动（模拟时间跳转）" : "阶段：我方操作（时间暂停）");
        GUILayout.Space(4);
        if (selectedUnit == null)
        {
            GUILayout.Label("未选择单位。点击我方单位查看属性和行动。");
        }
        else
        {
            GUILayout.Label(selectedUnit.team == 0 ? "我方单位属性" : "敌方单位属性（只读）");
            GUILayout.Label($"兵种：{ClassName(selectedUnit.unitClass)}");
            GUILayout.Label($"阵营：{(selectedUnit.team == 0 ? "我方" : "敌方")}");
            GUILayout.Label($"坐标：({selectedUnit.q}, {selectedUnit.r})");
            battlefield.TryGetTerrain(selectedUnit.q, selectedUnit.r, out string terrain);
            GUILayout.Label($"地形：{terrain}　防御地形加成：+{battlefield.GetDefenseBonus(selectedUnit.q, selectedUnit.r)}");
            GUILayout.Label($"HP：{selectedUnit.hp}　攻击：{selectedUnit.attack}　防御：{selectedUnit.defense}");
            GUILayout.Label(IsUnitReady(selectedUnit) ? "行动状态：就绪" :
                $"行动状态：恢复中，剩余 {Mathf.CeilToInt(selectedUnit.nextReadyTime - battleTimeAP)} AP");

            if (selectedUnit.team == 0 && !simulationRunning)
            {
                GUI.enabled = IsUnitReady(selectedUnit);
                if (GUILayout.Button(moveMode ? "移动（已选中）" : "移动"))
                {
                    moveMode = true;
                    RefreshMovementHighlights();
                    statusMessage = "点击青色地块移动；路线按地形累计 AP。";
                }

                bool canOccupy = IsUnitReady(selectedUnit) &&
                    battlefield.IsCity(selectedUnit.q, selectedUnit.r) &&
                    battlefield.GetCityOwner(selectedUnit.q, selectedUnit.r) != 0;
                GUI.enabled = canOccupy;
                if (GUILayout.Button($"占领城市（{cityOccupationCostAP} AP）"))
                    OccupyCity();
                GUI.enabled = true;
            }
        }

        GUILayout.Space(4);
        GUI.enabled = !simulationRunning && !HasReadyBlueUnit();
        if (GUILayout.Button("推进战场时间 / 执行敌方行动"))
            StartCoroutine(RunSimulationUntilPlayerCanAct());
        GUI.enabled = true;
        GUILayout.Label(statusMessage);
        GUILayout.EndArea();
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