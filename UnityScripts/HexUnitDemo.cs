
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// 六边形大战场单位系统。
/// 支持原作四帧动画、四方向切换、阵营区分、单位选择和相邻地块移动。
///
/// 本脚本与 HexBattlefieldDemo 挂在同一个 GameObject 上。
///
/// 资源目录示例：
/// Assets/Resources/GadgetSprites/MU_00000/frames/down_right/frame_01.png
/// Assets/Resources/GadgetSprites/MU_00000/frames/down_left/frame_01.png
/// Assets/Resources/GadgetSprites/MU_00000/frames/up_left/frame_01.png
/// Assets/Resources/GadgetSprites/MU_00000/frames/up_right/frame_01.png
///
/// Inspector 中的资源路径填写到 down_right 文件夹，不带 .png。
/// 其他三个方向放在同级目录下。
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
    [Min(1f)]
    public float pixelsPerUnit = 48f;

    [Min(0.1f)]
    public float animationFramesPerSecond = 6f;

    [Header("图标外观")]
    [Min(0.1f)]
    public float iconScale = 1.6f;

    [Min(0f)]
    public float liftAboveTile = 0.12f;

    [Min(1f)]
    public float selectedScaleMultiplier = 1.2f;

    private static readonly string[] Directions =
    {
        "down_right",
        "down_left",
        "up_left",
        "up_right"
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
    }

    private readonly List<UnitInfo> units = new List<UnitInfo>();

    private UnitInfo selectedUnit;
    private Camera mapCamera;
    private HexBattlefieldDemo battlefield;

    private void Start()
    {
        battlefield = GetComponent<HexBattlefieldDemo>();

        if (battlefield == null)
        {
            Debug.LogError(
                "HexUnitDemo 必须与 HexBattlefieldDemo 挂在同一个物体上。"
            );

            enabled = false;
            return;
        }

        mapCamera = Camera.main;

        Dictionary<string, Sprite[]> blueAnimations =
            LoadDirectionalFrames(
                blueFramesResourcePath,
                "我方",
                new Color32(70, 155, 255, 255),
                new Color32(235, 245, 255, 255)
            );

        Dictionary<string, Sprite[]> redAnimations =
            LoadDirectionalFrames(
                redFramesResourcePath,
                "敌方",
                new Color32(235, 75, 70, 255),
                new Color32(255, 225, 190, 255)
            );

        SpawnStartingUnits(
            blueStartingPositions,
            0,
            blueAnimations
        );

        SpawnStartingUnits(
            redStartingPositions,
            1,
            redAnimations
        );

        Debug.Log(
            "大战场单位系统启动完成。图标缩放：" +
            iconScale +
            "。选择我方单位后点击相邻空地块即可移动。"
        );
    }

    // 加载 down_right、down_left、up_left、up_right 四种朝向。
    // 如果其他方向尚未导入，就暂时使用 down_right 动画代替。
    private Dictionary<string, Sprite[]> LoadDirectionalFrames(
        string downRightPath,
        string teamLabel,
        Color32 bodyColor,
        Color32 accentColor)
    {
        Dictionary<string, Sprite[]> result =
            new Dictionary<string, Sprite[]>();

        string cleanPath = string.IsNullOrWhiteSpace(downRightPath)
            ? ""
            : downRightPath.Trim().Replace("\\", "/").Trim('/');

        Sprite[] fallbackFrames = null;
        string parentPath = "";

        if (!string.IsNullOrEmpty(cleanPath))
        {
            int lastSlash = cleanPath.LastIndexOf('/');

            if (lastSlash >= 0)
            {
                parentPath = cleanPath.Substring(0, lastSlash);
            }

            fallbackFrames = TryLoadFourFrames(cleanPath);
        }

        if (fallbackFrames == null)
        {
            Sprite placeholder = CreatePixelUnitSprite(
                bodyColor,
                accentColor
            );

            fallbackFrames = new[]
            {
                placeholder,
                placeholder,
                placeholder,
                placeholder
            };

            if (!string.IsNullOrEmpty(cleanPath))
            {
                Debug.LogWarning(
                    teamLabel +
                    "角色的 down_right 动画未能加载，暂时使用占位图。"
                );
            }
            else
            {
                Debug.Log(
                    teamLabel +
                    "未设置原作图片路径，暂时使用占位图。"
                );
            }
        }

        foreach (string direction in Directions)
        {
            Sprite[] frames;

            if (direction == "down_right")
            {
                frames = fallbackFrames;
            }
            else if (string.IsNullOrEmpty(cleanPath))
            {
                frames = fallbackFrames;
            }
            else
            {
                string directionPath =
                    parentPath + "/" + direction;

                frames = TryLoadFourFrames(directionPath);

                if (frames == null)
                {
                    frames = fallbackFrames;

                    Debug.LogWarning(
                        teamLabel + "缺少 " + direction +
                        " 动画，暂时使用 down_right 动画代替。"
                    );
                }
                else
                {
                    Debug.Log(
                        teamLabel + "已加载朝向 " +
                        direction + " 的四帧动画。"
                    );
                }
            }

            result[direction] = frames;
        }

        return result;
    }

    // 每个方向必须有 frame_01.png 至 frame_04.png。
    private Sprite[] TryLoadFourFrames(string folderPath)
    {
        Sprite[] frames = new Sprite[4];

        for (int i = 0; i < 4; i++)
        {
            string resourcePath =
                folderPath + "/frame_" + (i + 1).ToString("00");

            Sprite sourceSprite =
                Resources.Load<Sprite>(resourcePath);

            if (sourceSprite == null)
            {
                return null;
            }

            sourceSprite.texture.filterMode = FilterMode.Point;
            sourceSprite.texture.wrapMode = TextureWrapMode.Clamp;

            frames[i] = Sprite.Create(
                sourceSprite.texture,
                sourceSprite.rect,
                new Vector2(0.5f, 0.02f),
                pixelsPerUnit
            );

            frames[i].name =
                folderPath.Replace("/", "_") +
                "_frame_" + (i + 1).ToString("00");
        }

        return frames;
    }

    private void Update()
    {
        AnimateUnits();

        if (WasLeftClickPressed())
        {
            HandleMapClick();
        }
    }

    private void LateUpdate()
    {
        if (mapCamera == null)
        {
            mapCamera = Camera.main;
        }

        if (mapCamera == null)
        {
            return;
        }

        // 让二维图标始终朝向战场摄像机。
        // 角色朝向由所选动画行决定，而不是靠旋转图片实现。
        foreach (UnitInfo unit in units)
        {
            if (unit.visual == null)
            {
                continue;
            }

            Vector3 toCamera =
                mapCamera.transform.position -
                unit.visual.position;

            if (toCamera.sqrMagnitude > 0.001f)
            {
                unit.visual.rotation =
                    Quaternion.LookRotation(toCamera, Vector3.up);
            }
        }
    }

    private void AnimateUnits()
    {
        if (animationFramesPerSecond <= 0f)
        {
            return;
        }

        foreach (UnitInfo unit in units)
        {
            if (unit.renderer == null)
            {
                continue;
            }

            if (!unit.directionalFrames.TryGetValue(
                    unit.currentDirection,
                    out Sprite[] frames) ||
                frames == null ||
                frames.Length == 0)
            {
                continue;
            }

            int frameIndex = Mathf.FloorToInt(
                (Time.time + unit.animationOffset) *
                animationFramesPerSecond
            ) % frames.Length;

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
        return Mouse.current != null &&
               Mouse.current.leftButton.wasPressedThisFrame;
#else
        return Input.GetMouseButtonDown(0);
#endif
    }

    private Vector2 GetPointerPosition()
    {
#if ENABLE_INPUT_SYSTEM
        return Mouse.current != null
            ? Mouse.current.position.ReadValue()
            : Vector2.zero;
#else
        return Input.mousePosition;
#endif
    }

    private void HandleMapClick()
    {
        if (mapCamera == null)
        {
            mapCamera = Camera.main;
        }

        if (mapCamera == null)
        {
            return;
        }

        Ray ray = mapCamera.ScreenPointToRay(GetPointerPosition());

        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f))
        {
            return;
        }

        if (!TryReadHexCoordinates(
                hit.collider.gameObject.name,
                out int q,
                out int r))
        {
            return;
        }

        UnitInfo clickedUnit = FindUnitAt(q, r);

        if (selectedUnit == null)
        {
            if (clickedUnit != null && clickedUnit.team == 0)
            {
                SelectUnit(clickedUnit);
            }
            else if (clickedUnit != null)
            {
                Debug.Log("这是敌方单位，请先选择我方单位。");
            }
            else
            {
                Debug.Log(
                    $"已点击地块 ({q}, {r})，请先选择我方单位。"
                );
            }

            return;
        }

        if (clickedUnit != null)
        {
            if (clickedUnit.team == 0)
            {
                SelectUnit(clickedUnit);
            }
            else
            {
                Debug.Log("目标地块已有敌方单位，无法移动过去。");
            }

            return;
        }

        if (selectedUnit.q == q && selectedUnit.r == r)
        {
            DeselectUnit();
            return;
        }

        if (!IsAdjacent(selectedUnit.q, selectedUnit.r, q, r))
        {
            Debug.Log("目标地块不相邻，当前只能移动一格。");
            return;
        }

        if (hit.collider.gameObject.name.EndsWith("_Water"))
        {
            Debug.Log("目标为水域，当前原型禁止进入。");
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
        {
            return;
        }

        foreach (Vector2Int position in positions)
        {
            int q = Mathf.Clamp(
                position.x, 0, battlefield.columns - 1
            );

            int r = Mathf.Clamp(
                position.y, 0, battlefield.rows - 1
            );

            if (FindUnitAt(q, r) != null)
            {
                Debug.LogWarning(
                    $"地块 ({q}, {r}) 已有单位，跳过重复部署。"
                );

                continue;
            }

            GameObject iconObject = new GameObject(
                team == 0 ? "BlueUnitIcon" : "RedUnitIcon"
            );

            iconObject.transform.SetParent(transform, false);

            iconObject.transform.localPosition =
                HexToLocalPosition(q, r);

            iconObject.transform.localScale =
                Vector3.one * iconScale;

            SpriteRenderer spriteRenderer =
                iconObject.AddComponent<SpriteRenderer>();

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
                animationOffset =
                    (q * 3 + r * 7 + team * 5) * 0.037f
            };

            unit.renderer.sprite =
                animations["down_right"][0];

            unit.renderer.sortingOrder = 10;

            units.Add(unit);
        }
    }

    private Vector3 HexToLocalPosition(int q, int r)
    {
        float size = battlefield.hexSize;

        float x = 1.5f * size * q;

        float z = Mathf.Sqrt(3f) *
                  size *
                  (r + q * 0.5f);

        return new Vector3(
            x,
            battlefield.tileThickness * 0.5f + liftAboveTile,
            z
        );
    }

    private void SelectUnit(UnitInfo unit)
    {
        if (selectedUnit != null &&
            selectedUnit.visual != null)
        {
            selectedUnit.visual.localScale =
                selectedUnit.baseScale;
        }

        selectedUnit = unit;

        selectedUnit.visual.localScale =
            selectedUnit.baseScale * selectedScaleMultiplier;

        Debug.Log(
            (unit.team == 0 ? "已选中我方单位" : "已选中敌方单位") +
            $"，坐标：({unit.q}, {unit.r})。"
        );
    }

    private void DeselectUnit()
    {
        if (selectedUnit != null &&
            selectedUnit.visual != null)
        {
            selectedUnit.visual.localScale =
                selectedUnit.baseScale;
        }

        selectedUnit = null;
        Debug.Log("已取消选择单位。");
    }

    private void MoveUnit(UnitInfo unit, int q, int r)
    {
        int dq = q - unit.q;
        int dr = r - unit.r;

        unit.currentDirection = GetDirectionForMove(dq, dr);

        unit.q = q;
        unit.r = r;

        unit.visual.localPosition =
            HexToLocalPosition(q, r);

        unit.lastFrame = -1;

        unit.baseScale = Vector3.one * iconScale;

        unit.visual.localScale =
            unit.baseScale *
            (selectedUnit == unit
                ? selectedScaleMultiplier
                : 1f);

        Debug.Log(
            $"单位移动到地块 ({q}, {r})，" +
            $"切换朝向：{unit.currentDirection}。"
        );
    }

    // 根据六边形相邻地块的坐标变化选择最接近的四种原作朝向。
    // 上下两个正中方向分别归入一个相邻的对角方向。
    private string GetDirectionForMove(int dq, int dr)
    {
        if (dq == 1 && dr == 0)
            return "up_right";

        if (dq == -1 && dr == 0)
            return "down_left";

        if (dq == 0 && dr == 1)
            return "up_right";

        if (dq == 0 && dr == -1)
            return "down_right";

        if (dq == 1 && dr == -1)
            return "down_right";

        if (dq == -1 && dr == 1)
            return "up_left";

        return "down_right";
    }

    private UnitInfo FindUnitAt(int q, int r)
    {
        foreach (UnitInfo unit in units)
        {
            if (unit.q == q && unit.r == r)
            {
                return unit;
            }
        }

        return null;
    }

    private bool IsAdjacent(int fromQ, int fromR, int toQ, int toR)
    {
        int dq = toQ - fromQ;
        int dr = toR - fromR;

        return
            (dq == 1 && dr == 0) ||
            (dq == -1 && dr == 0) ||
            (dq == 0 && dr == 1) ||
            (dq == 0 && dr == -1) ||
            (dq == 1 && dr == -1) ||
            (dq == -1 && dr == 1);
    }

    private bool TryReadHexCoordinates(
        string objectName,
        out int q,
        out int r)
    {
        q = 0;
        r = 0;

        string[] parts = objectName.Split('_');

        return
            parts.Length >= 4 &&
            parts[0] == "Hex" &&
            int.TryParse(parts[1], out q) &&
            int.TryParse(parts[2], out r);
    }

    private Sprite CreatePixelUnitSprite(
        Color32 bodyColor,
        Color32 accentColor)
    {
        const int width = 16;
        const int height = 20;

        Texture2D texture = new Texture2D(
            width,
            height,
            TextureFormat.RGBA32,
            false
        );

        texture.name = "GeneratedPixelUnitTexture";
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;

        Color32 clear = new Color32(0, 0, 0, 0);
        Color32 outline = new Color32(24, 26, 34, 255);
        Color32 shadow = new Color32(45, 48, 60, 255);

        Color32[] pixels = new Color32[width * height];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = clear;
        }

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
            20f
        );
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
        for (
            int y = Mathf.Max(0, yMin);
            y <= Mathf.Min(height - 1, yMax);
            y++)
        {
            for (
                int x = Mathf.Max(0, xMin);
                x <= Mathf.Min(width - 1, xMax);
                x++)
            {
                pixels[y * width + x] = color;
            }
        }
    }
}
