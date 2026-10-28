using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 六边形大战场单位系统。
/// 保留原作像素角色动画，并实现移动范围高亮、地形/单位阻挡、共享 AP 时钟与行动恢复。
/// 本脚本和 HexBattlefieldDemo 挂在同一个 GameObject 上。
/// </summary>
public class HexUnitDemo : MonoBehaviour
{
    [Header("我方初始位置（六边形坐标）")]
    public Vector2Int[] blueStartingPositions =
    {
        new Vector2Int(1, 1),
        new Vector2Int(2, 3)
    };

    [Header("敌方初始位置（六边形坐标）")]
    public Vector2Int[] redStartingPositions =
    {
        new Vector2Int(7, 6),
        new Vector2Int(7, 7)
    };

    [Header("我方动画资源路径")]
    [Tooltip("填写 down_right 文件夹相对于 Resources 的路径")]
    public string blueFramesResourcePath =
        "GadgetSprites/MU_00000/frames/down_right";

    [Header("敌方动画资源路径")]
    [Tooltip("留空则使用红色临时占位图")]
    public string redFramesResourcePath = "";

    [Header("动画设置")]
    [Min(1f)] public float pixelsPerUnit = 48f;
    [Min(0.1f)] public float animationFramesPerSecond = 6f;

    [Header("图标外观")]
    [Min(0.1f)] public float iconScale = 1.6f;
    [Min(0f)] public float liftAboveTile = 0.12f;
    [Min(1f)] public float selectedScaleMultiplier = 1.2f;

    [Header("移动规则")]
    [Min(1)] public int blueMovementRange = 3;
    [Min(1)] public int redMovementRange = 3;
    [Tooltip("一次移动完成后，需要等待多少 AP 才能再次行动。此值为原型暂定值。")]
    [Min(1f)] public float movementRecoveryAP = 100f;

    [Header("共享行动时间轴")]
    [Tooltip("战场时钟每现实秒推进多少 AP。此值只控制原型推进速度。")]
    [Min(1f)] public float actionPointsPerSecond = 25f;
    public bool pauseBattleClock = false;
    [Tooltip("显示当前战场 AP 与已选单位恢复状态。")]
    public bool showBattleClockHUD = true;

    private static readonly string[] Directions =
    {
        "down_right",
        "down_left",
        "up_left",
        "up_right"
    };

    private static readonly Vector2Int[] Neighbors =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1),
        new Vector2Int(1, -1),
        new Vector2Int(-1, 1)
    };

    private class UnitInfo
    {
        public int q;
        public int r;
        public int team;
        public Transform visual;
        public SpriteRenderer renderer;
        public Dictionary<string, Sprite[]> directionalFrames;
        public string currentDirection = "down_right";
        public Vector3 baseScale;
        public float animationOffset;
        public int lastFrame = -1;

        // 该单位下次可以行动时，公共 AP 时钟需要到达的数值。
        public float nextReadyTime;
    }

    private readonly List<UnitInfo> units = new List<UnitInfo>();
    private readonly HashSet<Vector2Int> reachableTiles = new HashSet<Vector2Int>();

    private UnitInfo selectedUnit;
    private Camera mapCamera;
    private HexBattlefieldDemo battlefield;
    private float battleTimeAP;

    private void Start()
    {
        battlefield = GetComponent<HexBattlefieldDemo>();

        if (battlefield == null)
        {
            Debug.LogError("HexUnitDemo 必须与 HexBattlefieldDemo 挂在同一个物体上。");
            enabled = false;
            return;
        }

        mapCamera = Camera.main;

        Dictionary<string, Sprite[]> blueAnimations = LoadDirectionalFrames(
            blueFramesResourcePath,
            "我方",
            new Color32(70, 155, 255, 255),
            new Color32(235, 245, 255, 255));

        Dictionary<string, Sprite[]> redAnimations = LoadDirectionalFrames(
            redFramesResourcePath,
            "敌方",
            new Color32(235, 75, 70, 255),
            new Color32(255, 225, 190, 255));

        SpawnStartingUnits(blueStartingPositions, 0, blueAnimations);
        SpawnStartingUnits(redStartingPositions, 1, redAnimations);

        Debug.Log(
            "大战场行动系统启动。共享 AP 时钟已开始；我方移动范围 " +
            blueMovementRange + " 格，移动恢复时间 " +
            movementRecoveryAP + " AP。");
    }

    private Dictionary<string, Sprite[]> LoadDirectionalFrames(
        string downRightPath,
        string teamLabel,
        Color32 bodyColor,
        Color32 accentColor)
    {
        Dictionary<string, Sprite[]> result = new Dictionary<string, Sprite[]>();

        string cleanPath = string.IsNullOrWhiteSpace(downRightPath)
            ? ""
            : downRightPath.Trim().Replace("\\", "/").Trim('/');

        Sprite[] fallbackFrames = null;
        string parentPath = "";

        if (!string.IsNullOrEmpty(cleanPath))
        {
            int lastSlash = cleanPath.LastIndexOf('/');
            if (lastSlash >= 0)
                parentPath = cleanPath.Substring(0, lastSlash);

            fallbackFrames = TryLoadFourFrames(cleanPath);
        }

        if (fallbackFrames == null)
        {
            Sprite placeholder = CreatePixelUnitSprite(bodyColor, accentColor);
            fallbackFrames = new[] { placeholder, placeholder, placeholder, placeholder };

            if (!string.IsNullOrEmpty(cleanPath))
                Debug.LogWarning(teamLabel + "角色的 down_right 动画未能加载，暂时使用占位图。");
            else
                Debug.Log(teamLabel + "未设置原作图片路径，暂时使用占位图。");
        }

        foreach (string direction in Directions)
        {
            Sprite[] frames;

            if (direction == "down_right" || string.IsNullOrEmpty(cleanPath))
            {
                frames = fallbackFrames;
            }
            else
            {
                frames = TryLoadFourFrames(parentPath + "/" + direction);

                if (frames == null)
                {
                    frames = fallbackFrames;
                    Debug.LogWarning(teamLabel + "缺少 " + direction + " 动画，暂时使用 down_right 动画代替。");
                }
                else
                {
                    Debug.Log(teamLabel + "已加载朝向 " + direction + " 的四帧动画。");
                }
            }

            result[direction] = frames;
        }

        return result;
    }

    private Sprite[] TryLoadFourFrames(string folderPath)
    {
        Sprite[] frames = new Sprite[4];

        for (int i = 0; i < 4; i++)
        {
            string resourcePath = folderPath + "/frame_" + (i + 1).ToString("00");
            Sprite sourceSprite = Resources.Load<Sprite>(resourcePath);

            if (sourceSprite == null)
                return null;

            sourceSprite.texture.filterMode = FilterMode.Point;
            sourceSprite.texture.wrapMode = TextureWrapMode.Clamp;

            frames[i] = Sprite.Create(
                sourceSprite.texture,
                sourceSprite.rect,
                new Vector2(0.5f, 0.02f),
                pixelsPerUnit);

            frames[i].name = folderPath.Replace("/", "_") + "_frame_" + (i + 1).ToString("00");
        }

        return frames;
    }

    private void Update()
    {
        AdvanceBattleClock();
        AnimateUnits();

        if (WasLeftClickPressed())
            HandleMapClick();
    }

    private void AdvanceBattleClock()
    {
        if (!pauseBattleClock && actionPointsPerSecond > 0f)
            battleTimeAP += Time.deltaTime * actionPointsPerSecond;
    }

    private void LateUpdate()
    {
        if (mapCamera == null)
            mapCamera = Camera.main;

        if (mapCamera == null)
            return;

        foreach (UnitInfo unit in units)
        {
            if (unit.visual == null)
                continue;

            Vector3 toCamera = mapCamera.transform.position - unit.visual.position;
            if (toCamera.sqrMagnitude > 0.001f)
                unit.visual.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
        }
    }

    private void AnimateUnits()
    {
        if (animationFramesPerSecond <= 0f)
            return;

        foreach (UnitInfo unit in units)
        {
            if (unit.renderer == null ||
                !unit.directionalFrames.TryGetValue(unit.currentDirection, out Sprite[] frames) ||
                frames == null || frames.Length == 0)
                continue;

            int frameIndex = Mathf.FloorToInt(
                (Time.time + unit.animationOffset) * animationFramesPerSecond) % frames.Length;

            if (frameIndex != unit.lastFrame)
            {
                unit.renderer.sprite = frames[frameIndex];
                unit.lastFrame = frameIndex;
            }
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

    private void HandleMapClick()
    {
        if (mapCamera == null)
            mapCamera = Camera.main;

        if (mapCamera == null)
            return;

        Ray ray = mapCamera.ScreenPointToRay(GetPointerPosition());
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f))
            return;

        if (!TryReadHexCoordinates(hit.collider.gameObject.name, out int q, out int r))
            return;

        UnitInfo clickedUnit = FindUnitAt(q, r);

        if (clickedUnit != null)
        {
            if (clickedUnit.team == 0)
            {
                if (selectedUnit == clickedUnit)
                {
                    DeselectUnit();
                }
                else
                {
                    SelectUnit(clickedUnit);
                }
            }
            else
            {
                Debug.Log("目标地块已有敌方单位。攻击功能将在下一阶段加入。");
            }

            return;
        }

        if (selectedUnit == null)
        {
            Debug.Log($"已点击地块 ({q}, {r})，请先选择我方单位。");
            return;
        }

        if (!IsUnitReady(selectedUnit))
        {
            Debug.Log(
                $"该单位正在恢复，还需等待 {Mathf.CeilToInt(selectedUnit.nextReadyTime - battleTimeAP)} AP。");
            return;
        }

        Vector2Int destination = new Vector2Int(q, r);
        if (!reachableTiles.Contains(destination))
        {
            if (!battlefield.IsInsideMap(q, r))
                Debug.Log("目标位置超出地图范围。");
            else if (!battlefield.IsTileWalkable(q, r))
                Debug.Log("目标是水域，当前原型禁止进入。");
            else
                Debug.Log("目标不在当前移动范围内，或路径被地形/其他单位阻挡。");

            return;
        }

        MoveUnit(selectedUnit, q, r);
    }

    private void SpawnStartingUnits(
        Vector2Int[] positions,
        int team,
        Dictionary<string, Sprite[]> animations)
    {
        if (positions == null)
            return;

        foreach (Vector2Int position in positions)
        {
            if (!battlefield.IsInsideMap(position.x, position.y))
            {
                Debug.LogWarning($"单位初始坐标 ({position.x}, {position.y}) 超出地图，已跳过。");
                continue;
            }

            int q = position.x;
            int r = position.y;

            if (FindUnitAt(q, r) != null)
            {
                Debug.LogWarning($"地块 ({q}, {r}) 已有单位，跳过重复部署。");
                continue;
            }

            GameObject iconObject = new GameObject(team == 0 ? "BlueUnitIcon" : "RedUnitIcon");
            iconObject.transform.SetParent(transform, false);
            iconObject.transform.localPosition = HexToLocalPosition(q, r);
            iconObject.transform.localScale = Vector3.one * iconScale;

            SpriteRenderer spriteRenderer = iconObject.AddComponent<SpriteRenderer>();

            UnitInfo unit = new UnitInfo
            {
                q = q,
                r = r,
                team = team,
                visual = iconObject.transform,
                renderer = spriteRenderer,
                directionalFrames = animations,
                currentDirection = "down_right",
                baseScale = Vector3.one * iconScale,
                animationOffset = (q * 3 + r * 7 + team * 5) * 0.037f,
                nextReadyTime = 0f
            };

            unit.renderer.sprite = animations["down_right"][0];
            unit.renderer.sortingOrder = 10;
            units.Add(unit);
        }
    }

    private Vector3 HexToLocalPosition(int q, int r)
    {
        float size = battlefield.hexSize;
        float x = 1.5f * size * q;
        float z = Mathf.Sqrt(3f) * size * (r + q * 0.5f);

        return new Vector3(
            x,
            battlefield.tileThickness * 0.5f + liftAboveTile,
            z);
    }

    private void SelectUnit(UnitInfo unit)
    {
        if (selectedUnit != null && selectedUnit.visual != null)
            selectedUnit.visual.localScale = selectedUnit.baseScale;

        selectedUnit = unit;

        if (selectedUnit.visual != null)
            selectedUnit.visual.localScale = selectedUnit.baseScale * selectedScaleMultiplier;

        RefreshMovementHighlights();

        if (IsUnitReady(unit))
        {
            Debug.Log(
                $"已选中我方单位，坐标 ({unit.q}, {unit.r})，可以移动最多 {blueMovementRange} 格。");
        }
        else
        {
            Debug.Log(
                $"已选中单位，但它正在恢复；还需等待 {Mathf.CeilToInt(unit.nextReadyTime - battleTimeAP)} AP。");
        }
    }

    private void DeselectUnit()
    {
        if (selectedUnit != null && selectedUnit.visual != null)
            selectedUnit.visual.localScale = selectedUnit.baseScale;

        selectedUnit = null;
        reachableTiles.Clear();
        battlefield.SetMovementHighlights(null);
        Debug.Log("已取消选择单位。");
    }

    private void RefreshMovementHighlights()
    {
        reachableTiles.Clear();

        if (selectedUnit == null || selectedUnit.team != 0 || !IsUnitReady(selectedUnit))
        {
            battlefield.SetMovementHighlights(null);
            return;
        }

        int maxDistance = Mathf.Max(1, blueMovementRange);
        Vector2Int start = new Vector2Int(selectedUnit.q, selectedUnit.r);

        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int>();

        queue.Enqueue(start);
        distances[start] = 0;

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            int currentDistance = distances[current];

            if (currentDistance >= maxDistance)
                continue;

            foreach (Vector2Int direction in Neighbors)
            {
                Vector2Int next = current + direction;

                if (distances.ContainsKey(next))
                    continue;

                if (!battlefield.IsInsideMap(next.x, next.y))
                    continue;

                if (!battlefield.IsTileWalkable(next.x, next.y))
                    continue;

                UnitInfo occupant = FindUnitAt(next.x, next.y);
                if (occupant != null && occupant != selectedUnit)
                    continue;

                int nextDistance = currentDistance + 1;
                distances[next] = nextDistance;
                queue.Enqueue(next);

                if (next != start)
                    reachableTiles.Add(next);
            }
        }

        battlefield.SetMovementHighlights(reachableTiles);
    }

    private void MoveUnit(UnitInfo unit, int q, int r)
    {
        int dq = q - unit.q;
        int dr = r - unit.r;

        unit.currentDirection = GetDirectionForMove(dq, dr);
        unit.q = q;
        unit.r = r;
        unit.visual.localPosition = HexToLocalPosition(q, r);
        unit.lastFrame = -1;
        unit.baseScale = Vector3.one * iconScale;

        unit.visual.localScale = unit.baseScale *
            (selectedUnit == unit ? selectedScaleMultiplier : 1f);

        unit.nextReadyTime = battleTimeAP + movementRecoveryAP;

        reachableTiles.Clear();
        battlefield.SetMovementHighlights(null);

        Debug.Log(
            $"单位移动到 ({q}, {r})。本次移动结束；当前战场时间 {Mathf.FloorToInt(battleTimeAP)} AP，" +
            $"预计在 {Mathf.CeilToInt(unit.nextReadyTime)} AP 时恢复行动。");
    }

    private bool IsUnitReady(UnitInfo unit)
    {
        return unit != null && battleTimeAP + 0.001f >= unit.nextReadyTime;
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

    private UnitInfo FindUnitAt(int q, int r)
    {
        foreach (UnitInfo unit in units)
        {
            if (unit.q == q && unit.r == r)
                return unit;
        }

        return null;
    }

    private bool TryReadHexCoordinates(string objectName, out int q, out int r)
    {
        q = 0;
        r = 0;
        string[] parts = objectName.Split('_');

        return parts.Length >= 4 &&
               parts[0] == "Hex" &&
               int.TryParse(parts[1], out q) &&
               int.TryParse(parts[2], out r);
    }

    private void OnGUI()
    {
        if (!showBattleClockHUD)
            return;

        GUILayout.BeginArea(new Rect(12f, 12f, 360f, 116f), GUI.skin.box);
        GUILayout.Label($"战场共享时间：{Mathf.FloorToInt(battleTimeAP)} AP");

        if (selectedUnit == null)
        {
            GUILayout.Label("当前未选中我方单位。");
        }
        else
        {
            string status = IsUnitReady(selectedUnit)
                ? "可以行动"
                : $"恢复中，剩余 {Mathf.CeilToInt(selectedUnit.nextReadyTime - battleTimeAP)} AP";

            GUILayout.Label($"所选单位：({selectedUnit.q}, {selectedUnit.r})　{status}");
        }

        GUILayout.Label(
            $"移动范围：{blueMovementRange} 格　移动恢复：{movementRecoveryAP:0} AP　" +
            $"时钟速度：{actionPointsPerSecond:0} AP/秒");
        GUILayout.EndArea();
    }

    private Sprite CreatePixelUnitSprite(Color32 bodyColor, Color32 accentColor)
    {
        const int width = 16;
        const int height = 20;

        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.name = "GeneratedPixelUnitTexture";
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;

        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 outline = new Color32(24, 26, 34, 255);
        Color32 shadow = new Color32(45, 48, 60, 255);

        Color32[] pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = clear;

        FillRect(pixels, width, height, 3, 0, 7, 5, outline);
        FillRect(pixels, width, height, 4, 1, 6, 4, bodyColor);
        FillRect(pixels, width, height, 8, 1, 10, 4, outline);
        FillRect(pixels, width, height, 9, 2, 10, 4, bodyColor);
        FillRect(pixels, width, height, 1, 7, 4, 12, outline);
        FillRect(pixels, width, height, 2, 8, 3, 11, bodyColor);
        FillRect(pixels, width, height, 10, 7, 13, 12, outline);
        FillRect(pixels, width, height, 11, 8, 12, 11, bodyColor);
        FillRect(pixels, width, height, 4, 5, 11, 13, outline);
        FillRect(pixels, width, height, 5, 6, 10, 12, bodyColor);
        FillRect(pixels, width, height, 6, 9, 9, 10, accentColor);
        FillRect(pixels, width, height, 4, 13, 11, 19, outline);
        FillRect(pixels, width, height, 5, 14, 10, 18, bodyColor);
        FillRect(pixels, width, height, 5, 15, 10, 16, accentColor);
        FillRect(pixels, width, height, 6, 17, 9, 18, shadow);

        texture.SetPixels32(pixels);
        texture.Apply(false, true);

        return Sprite.Create(
            texture,
            new Rect(0, 0, width, height),
            new Vector2(0.5f, 0.02f),
            20f);
    }

    private void FillRect(
        Color32[] pixels,
        int width,
        int height,
        int xMin,
        int yMin,
        int xMax,
        int yMax,
        Color32 color)
    {
        for (int y = Mathf.Max(0, yMin); y <= Mathf.Min(height - 1, yMax); y++)
        {
            for (int x = Mathf.Max(0, xMin); x <= Mathf.Min(width - 1, xMax); x++)
                pixels[y * width + x] = color;
        }
    }
}