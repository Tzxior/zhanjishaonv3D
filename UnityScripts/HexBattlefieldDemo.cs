using System.Collections.Generic;
using UnityEngine;

public enum HexUnitClass { Infantry, HeavyGround, Air, Naval }

public class HexBattlefieldDemo : MonoBehaviour
{
    [Header("Map Size")]
    [Min(1)] public int columns = 9;
    [Min(1)] public int rows = 9;
    [Min(0.2f)] public float hexSize = 1f;
    [Min(0.05f)] public float tileThickness = 0.16f;

    [Header("Camera")]
    public float cameraHeight = 18f;
    public float cameraBackOffset = 18f;
    [Range(30f, 80f)] public float cameraFieldOfView = 55f;

    [Header("Camera Controls")]
    [Min(0.1f)] public float cameraPanSensitivity = 1f;
    [Min(0.01f)] public float cameraRotationSensitivity = 0.25f;
    [Range(10f, 60f)] public float minimumCameraPolarAngle = 20f;
    [Range(45f, 89f)] public float maximumCameraPolarAngle = 80f;

    [Header("Movement Highlight")]
    public Color reachableTileColor = new Color(0.18f, 0.78f, 0.88f);
    public Color actionTargetColor = new Color(1f, 0.42f, 0.08f);
    public Color neutralCityColor = new Color(0.75f, 0.69f, 0.42f);
    [Header("Faction Colors")]
    public Color playerFactionColor = new Color(0.86f, 0.16f, 0.21f);
    public Color opponentFactionColor = new Color(0.16f, 0.39f, 0.86f);

    private Mesh hexMesh;
    private readonly Dictionary<Color, Material> generatedMaterials = new Dictionary<Color, Material>();
    private Camera mainCamera;
    private Vector3 cameraFocus;
    private readonly Dictionary<Collider, TileInfo> tilesByCollider = new Dictionary<Collider, TileInfo>();
    private readonly Dictionary<Vector2Int, TileInfo> tilesByCoordinates = new Dictionary<Vector2Int, TileInfo>();

    private class TileInfo
    {
        public int q, r;
        public string terrain;
        public Material material;
        public Color baseColor;
        public bool isReachable;
        public bool isActionTarget;
        public int ownerTeam = -1; // -1 neutral, 0 blue, 1 red
    }

    private void Start()
    {
        columns = Mathf.Max(1, columns);
        rows = Mathf.Max(1, rows);
        hexMesh = CreateHexPrismMesh(hexSize * 0.96f, tileThickness);
        BuildMap();
        PositionCamera();
    }

    private Vector3 GetMapCenter()
    {
        float x = (columns - 1) * 1.5f * hexSize * 0.5f;
        float z = Mathf.Sqrt(3f) * hexSize * ((rows - 1) + (columns - 1) * 0.5f) * 0.5f;
        return new Vector3(x, 0f, z);
    }

    private void BuildMap()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) { Debug.LogError("HexBattlefieldDemo: No compatible shader."); return; }

        float mapWidth = (columns - 1) * 1.5f * hexSize + 2f * hexSize;
        float mapDepth = Mathf.Sqrt(3f) * hexSize * ((rows - 1) + (columns - 1) * 0.5f) + Mathf.Sqrt(3f) * hexSize;
        GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Plane);
        backdrop.name = "HexMapBackdrop";
        backdrop.transform.SetParent(transform, false);
        Vector3 center = GetMapCenter();
        backdrop.transform.position = new Vector3(center.x, -tileThickness * 0.5f - 0.06f, center.z);
        backdrop.transform.localScale = new Vector3(mapWidth / 10f, 1f, mapDepth / 10f);
        Material backing = new Material(shader);
        backing.color = new Color(0.12f, 0.14f, 0.13f);
        backdrop.GetComponent<Renderer>().sharedMaterial = backing;

        for (int q = 0; q < columns; q++)
        for (int r = 0; r < rows; r++)
        {
            Vector3 pos = new Vector3(1.5f * hexSize * q, 0f, Mathf.Sqrt(3f) * hexSize * (r + q * 0.5f));
            GetTerrain(q, r, out string terrain, out Color color);
            GameObject tile = new GameObject($"Hex_{q}_{r}_{terrain}");
            tile.transform.SetParent(transform, false);
            tile.transform.localPosition = pos;
            tile.AddComponent<MeshFilter>().sharedMesh = hexMesh;
            Material material = new Material(shader);
            material.color = color;
            tile.AddComponent<MeshRenderer>().sharedMaterial = material;
            MeshCollider collider = tile.AddComponent<MeshCollider>();
            collider.sharedMesh = hexMesh;
            TileInfo info = new TileInfo { q = q, r = r, terrain = terrain, material = material, baseColor = color };
            tilesByCollider[collider] = info;
            tilesByCoordinates[new Vector2Int(q, r)] = info;
            CreateTerrainVisuals(tile.transform, terrain, shader, q, r);
        }
        Debug.Log($"Hex battlefield ready: {columns} x {rows} = {columns * rows} hexes.");
    }

    private Material GetOrCreateVisualMaterial(Color color, Shader shader)
    {
        if (generatedMaterials.TryGetValue(color, out Material cached) && cached != null)
            return cached;
        Material material = new Material(shader) { color = color };
        generatedMaterials[color] = material;
        return material;
    }

    private GameObject AddTerrainPart(Transform parent, PrimitiveType shape, string partName,
        Vector3 localPosition, Vector3 localScale, Color color, Shader shader, Vector3 localEuler = default)
    {
        GameObject part = GameObject.CreatePrimitive(shape);
        part.name = partName;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = localScale;
        part.transform.localEulerAngles = localEuler;
        Collider collider = part.GetComponent<Collider>();
        if (collider != null) collider.enabled = false;
        Renderer renderer = part.GetComponent<Renderer>();
        if (renderer != null) renderer.sharedMaterial = GetOrCreateVisualMaterial(color, shader);
        return part;
    }

    private string TerrainIcon(string terrain)
    {
        switch (terrain)
        {
            case "Grassland": return "PL";
            case "Forest": return "FR";
            case "Hill": return "HL";
            case "Mountain": return "MT";
            case "Water": return "WT";
            case "Road": return "RD";
            case "MountainRoad": return "MR";
            case "Dryland": return "DR";
            case "City": return "CT";
            default: return "??";
        }
    }

    private void CreateTerrainVisuals(Transform tile, string terrain, Shader shader, int q, int r)
    {
        float top = tileThickness * 0.5f + 0.012f;
        float size = hexSize;
        Color dark = new Color(0.14f, 0.16f, 0.15f);
        Color stone = new Color(0.42f, 0.44f, 0.45f);
        Color foliage = new Color(0.08f, 0.25f, 0.13f);
        Color foliageLight = new Color(0.16f, 0.42f, 0.20f);
        Color soil = new Color(0.43f, 0.29f, 0.18f);
        Color paleSoil = new Color(0.72f, 0.58f, 0.36f);

        switch (terrain)
        {
            case "Forest":
                for (int i = 0; i < 3; i++)
                {
                    float x = (i - 1) * 0.27f * size;
                    float z = (i == 1 ? 0.19f : -0.17f) * size;
                    float height = (i == 1 ? 0.48f : 0.38f) * size;
                    AddTerrainPart(tile, PrimitiveType.Cylinder, "TreeTrunk",
                        new Vector3(x, top + height * 0.22f, z), new Vector3(0.065f, height * 0.30f, 0.065f) * size,
                        soil, shader);
                    AddTerrainPart(tile, PrimitiveType.Sphere, "TreeCanopy",
                        new Vector3(x, top + height * 0.68f, z), new Vector3(0.25f, height, 0.23f) * size,
                        (i == 1 ? foliageLight : foliage), shader);
                }
                break;

            case "Hill":
                AddTerrainPart(tile, PrimitiveType.Sphere, "HillMound",
                    new Vector3(0f, top + 0.09f * size, 0f), new Vector3(0.78f, 0.22f, 0.66f) * size,
                    new Color(0.48f, 0.36f, 0.24f), shader);
                AddTerrainPart(tile, PrimitiveType.Sphere, "HillRidge",
                    new Vector3(0.08f * size, top + 0.16f * size, 0.02f * size), new Vector3(0.38f, 0.10f, 0.44f) * size,
                    new Color(0.62f, 0.48f, 0.31f), shader);
                break;

            case "Mountain":
                AddTerrainPart(tile, PrimitiveType.Sphere, "MountainPeak",
                    new Vector3(-0.14f * size, top + 0.26f * size, -0.03f * size),
                    new Vector3(0.42f, 0.70f, 0.38f) * size, stone, shader, new Vector3(0f, 0f, -12f));
                AddTerrainPart(tile, PrimitiveType.Sphere, "MountainPeakSmall",
                    new Vector3(0.22f * size, top + 0.19f * size, 0.17f * size),
                    new Vector3(0.32f, 0.49f, 0.29f) * size, new Color(0.34f, 0.36f, 0.38f), shader);
                AddTerrainPart(tile, PrimitiveType.Cube, "RockFace",
                    new Vector3(-0.16f * size, top + 0.06f * size, 0.10f * size),
                    new Vector3(0.25f, 0.18f, 0.24f) * size, new Color(0.56f, 0.57f, 0.57f), shader,
                    new Vector3(0f, 0f, 28f));
                break;

            case "Water":
                for (int i = 0; i < 3; i++)
                    AddTerrainPart(tile, PrimitiveType.Sphere, "WaterRipple",
                        new Vector3((i - 1) * 0.24f * size, top + 0.012f, (i % 2 == 0 ? 0.13f : -0.12f) * size),
                        new Vector3(0.38f, 0.025f, 0.07f) * size,
                        i == 1 ? new Color(0.49f, 0.79f, 0.85f) : new Color(0.25f, 0.56f, 0.69f), shader,
                        new Vector3(0f, 0f, i == 1 ? 0f : -7f));
                break;

            case "Road":
                AddTerrainPart(tile, PrimitiveType.Cube, "RoadSurface",
                    new Vector3(0f, top + 0.018f, 0f), new Vector3(1.30f, 0.035f, 0.34f) * size,
                    new Color(0.28f, 0.28f, 0.26f), shader, new Vector3(0f, 30f, 0f));
                AddTerrainPart(tile, PrimitiveType.Cube, "RoadCenterMark",
                    new Vector3(0f, top + 0.038f, 0f), new Vector3(0.72f, 0.008f, 0.025f) * size,
                    new Color(0.79f, 0.73f, 0.54f), shader, new Vector3(0f, 30f, 0f));
                break;

            case "MountainRoad":
                AddTerrainPart(tile, PrimitiveType.Cube, "MountainRoadSurface",
                    new Vector3(0f, top + 0.022f, 0f), new Vector3(1.26f, 0.045f, 0.36f) * size,
                    new Color(0.35f, 0.29f, 0.23f), shader, new Vector3(0f, 30f, 0f));
                AddTerrainPart(tile, PrimitiveType.Cube, "MountainRoadStripe",
                    new Vector3(0f, top + 0.048f, 0f), new Vector3(0.72f, 0.008f, 0.025f) * size,
                    new Color(0.70f, 0.61f, 0.45f), shader, new Vector3(0f, 30f, 0f));
                for (int i = 0; i < 2; i++)
                    AddTerrainPart(tile, PrimitiveType.Sphere, "RoadsideRock",
                        new Vector3((i == 0 ? -0.34f : 0.34f) * size, top + 0.06f * size, (i == 0 ? 0.25f : -0.25f) * size),
                        new Vector3(0.17f, 0.12f, 0.15f) * size, stone, shader);
                break;

            case "Dryland":
                for (int i = 0; i < 3; i++)
                    AddTerrainPart(tile, PrimitiveType.Sphere, "DrylandScrub",
                        new Vector3((i - 1) * 0.29f * size, top + 0.025f, (i % 2 == 0 ? 0.20f : -0.18f) * size),
                        new Vector3(0.16f, 0.055f, 0.13f) * size, i == 1 ? paleSoil : soil, shader);
                break;

            case "City":
                AddTerrainPart(tile, PrimitiveType.Cube, "CityPlaza",
                    new Vector3(0f, top + 0.015f, 0f), new Vector3(0.88f, 0.035f, 0.76f) * size,
                    new Color(0.38f, 0.39f, 0.38f), shader);
                for (int i = 0; i < 3; i++)
                {
                    float x = (i - 1) * 0.25f * size;
                    float h = (i == 1 ? 0.24f : 0.17f) * size;
                    AddTerrainPart(tile, PrimitiveType.Cube, "CityBlock",
                        new Vector3(x, top + h * 0.55f, (i % 2 == 0 ? 0.16f : -0.12f) * size),
                        new Vector3(0.19f, h, 0.20f) * size,
                        new Color(0.56f, 0.56f, 0.52f), shader);
                    AddTerrainPart(tile, PrimitiveType.Cube, "CityRoof",
                        new Vector3(x, top + h + 0.008f, (i % 2 == 0 ? 0.16f : -0.12f) * size),
                        new Vector3(0.20f, 0.025f, 0.21f) * size,
                        new Color(0.26f, 0.29f, 0.32f), shader);
                }
                break;

            default: // Grassland remains open, with small tufts so it does not look identical to a blank tile.
                for (int i = 0; i < 2; i++)
                    AddTerrainPart(tile, PrimitiveType.Cube, "GrassTuft",
                        new Vector3((i == 0 ? -0.23f : 0.25f) * size, top + 0.025f, (i == 0 ? 0.12f : -0.20f) * size),
                        new Vector3(0.11f, 0.06f, 0.035f) * size,
                        new Color(0.20f, 0.39f, 0.20f), shader,
                        new Vector3(0f, 0f, i == 0 ? 20f : -20f));
                break;
        }

        var iconObject = new GameObject($"TerrainIcon_{TerrainIcon(terrain)}_{q}_{r}");
        iconObject.transform.SetParent(tile, false);
        iconObject.transform.localPosition = new Vector3(-0.49f * size, tileThickness * 0.5f + 0.022f, 0.37f * size);
        iconObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        TextMesh icon = iconObject.AddComponent<TextMesh>();
        icon.text = TerrainIcon(terrain);
        icon.fontSize = 42;
        icon.characterSize = 0.12f * size;
        icon.anchor = TextAnchor.MiddleCenter;
        icon.alignment = TextAlignment.Center;
        icon.color = new Color(0.08f, 0.10f, 0.09f, 0.92f);
    }

    public void SetFactionColors(Color playerColor, Color opponentColor)
    {
        playerFactionColor = playerColor;
        opponentFactionColor = opponentColor;
        foreach (TileInfo tile in tilesByCoordinates.Values)
            RefreshTileColor(tile);
    }

    public Color GetFactionColor(int team)
    {
        if (team == 0) return playerFactionColor;
        if (team == 1) return opponentFactionColor;
        return neutralCityColor;
    }

    private void GetTerrain(int q, int r, out string terrain, out Color color)
    {
        int cq = columns / 2, cr = rows / 2;
        if (q == cq && r == cr) { terrain = "City"; color = neutralCityColor; return; }

        // Visible road corridors let the prototype demonstrate terrain-dependent AP costs.
        if (r == cr || q == cq) { terrain = "Road"; color = new Color(0.46f, 0.43f, 0.34f); return; }
        if (q == cq + 1 && r >= Mathf.Max(0, cr - 2) && r <= Mathf.Min(rows - 1, cr + 2))
        { terrain = "MountainRoad"; color = new Color(0.56f, 0.40f, 0.29f); return; }

        int value = (q * 37 + r * 19 + q * r * 13) % 100;
        if (value < 8) { terrain = "Water"; color = new Color(0.16f, 0.36f, 0.55f); }
        else if (value < 20) { terrain = "Mountain"; color = new Color(0.43f, 0.45f, 0.46f); }
        else if (value < 34) { terrain = "Forest"; color = new Color(0.16f, 0.34f, 0.21f); }
        else if (value < 43) { terrain = "Hill"; color = new Color(0.48f, 0.40f, 0.29f); }
        else if (value < 58) { terrain = "Dryland"; color = new Color(0.63f, 0.53f, 0.34f); }
        else { terrain = "Grassland"; color = new Color(0.32f, 0.49f, 0.29f); }
    }

    private void PositionCamera()
    {
        mainCamera = Camera.main;
        if (mainCamera == null) { Debug.LogWarning("No MainCamera found."); return; }
        cameraFocus = GetMapCenter();
        mainCamera.transform.position = cameraFocus + new Vector3(0f, cameraHeight, -cameraBackOffset);
        mainCamera.transform.LookAt(cameraFocus);
        mainCamera.fieldOfView = cameraFieldOfView;
    }

    public void PanCameraByPixels(Vector2 pixelDelta)
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null || Screen.height <= 0) return;
        float distance = Vector3.Distance(mainCamera.transform.position, cameraFocus);
        float worldPerPixel = 2f * distance *
            Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height * cameraPanSensitivity;

        Vector3 right = mainCamera.transform.right;
        right.y = 0f;
        if (right.sqrMagnitude > 0.001f) right.Normalize();
        Vector3 forward = mainCamera.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.001f) forward.Normalize();
        Vector3 movement = (-right * pixelDelta.x - forward * pixelDelta.y) * worldPerPixel;
        mainCamera.transform.position += movement;
        cameraFocus += movement;
    }

    public void RotateCameraByPixels(Vector2 pixelDelta)
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (mainCamera == null) return;
        mainCamera.transform.RotateAround(cameraFocus, Vector3.up,
            pixelDelta.x * cameraRotationSensitivity);

        Vector3 offset = mainCamera.transform.position - cameraFocus;
        if (offset.sqrMagnitude > 0.001f)
        {
            float polarAngle = Vector3.Angle(offset.normalized, Vector3.up);
            float desiredPolarAngle = Mathf.Clamp(
                polarAngle - pixelDelta.y * cameraRotationSensitivity,
                Mathf.Min(minimumCameraPolarAngle, maximumCameraPolarAngle),
                Mathf.Max(minimumCameraPolarAngle, maximumCameraPolarAngle));
            float polarDelta = desiredPolarAngle - polarAngle;
            if (Mathf.Abs(polarDelta) > 0.001f)
                mainCamera.transform.RotateAround(cameraFocus, mainCamera.transform.right, polarDelta);
        }
        mainCamera.transform.LookAt(cameraFocus);
    }

    public bool IsInsideMap(int q, int r) => q >= 0 && q < columns && r >= 0 && r < rows;

    public bool TryGetTerrain(int q, int r, out string terrain)
    {
        if (tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)) { terrain = tile.terrain; return true; }
        terrain = ""; return false;
    }

    public bool IsTileWalkable(int q, int r, HexUnitClass unitClass)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)) return false;
        if (unitClass == HexUnitClass.Air) return true;
        if (unitClass == HexUnitClass.Naval) return tile.terrain == "Water";
        return tile.terrain != "Water";
    }

    public int GetMovementCostAP(int q, int r, HexUnitClass unitClass)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)) return int.MaxValue;
        if (unitClass == HexUnitClass.Air) return 10;
        if (unitClass == HexUnitClass.Naval) return tile.terrain == "Water" ? 10 : int.MaxValue;
        switch (tile.terrain)
        {
            case "Road": return 10;
            case "MountainRoad": return unitClass == HexUnitClass.HeavyGround ? 60 : 50;
            case "Mountain": return unitClass == HexUnitClass.HeavyGround ? 60 : 50;
            case "Forest": return unitClass == HexUnitClass.HeavyGround ? 40 : 30;
            case "Dryland": return 20;
            case "Grassland": return 15;
            case "City": return 10;
            case "Water": return int.MaxValue;
            default: return 20;
        }
    }


    // Movement points are independent from Action Points (AP).
    // Infantry: roads/open ground 1, forest/hills 2, mountains 3.
    // Air units ignore most ordinary terrain costs and blockers.
    public int GetMovementPointCost(int q, int r, HexUnitClass unitClass)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile))
            return int.MaxValue;
        if (unitClass == HexUnitClass.Air) return 1;
        if (unitClass == HexUnitClass.Naval) return tile.terrain == "Water" ? 1 : int.MaxValue;

        switch (tile.terrain)
        {
            case "Road":
            case "City":
            case "Dryland":
            case "Grassland": return 1;
            case "Forest":
            case "Hill":
            case "MountainRoad": return 2;
            case "Mountain": return 3;
            case "Water": return int.MaxValue;
            default: return 1;
        }
    }

    public string GetTerrainDisplayName(int q, int r)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile))
            return "地图外";
        switch (tile.terrain)
        {
            case "Road": return "道路";
            case "MountainRoad": return "山路";
            case "Grassland": return "平原";
            case "Dryland": return "干燥地";
            case "Forest": return "森林";
            case "Hill": return "丘陵";
            case "Mountain": return "山地";
            case "Water": return "水域";
            case "City": return "城市";
            default: return tile.terrain;
        }
    }

    public string GetTerrainBonusDescription(int q, int r)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile))
            return "无";
        switch (tile.terrain)
        {
            case "Forest": return "防御 +10；地面移动消耗 2";
            case "Hill": return "防御 +15；地面移动消耗 2";
            case "Mountain": return "防御 +20；地面移动消耗 3";
            case "MountainRoad": return "防御 +20；地面移动消耗 2";
            case "City": return "防御 +40；可占领改变归属";
            case "Road": return "地面移动消耗 1；AP 成本按道路规则";
            case "Water": return "地面单位不可进入；空军可通过";
            default: return "无额外防御加成；基础移动消耗 1";
        }
    }

    public int GetDefenseBonus(int q, int r)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)) return 0;
        switch (tile.terrain)
        {
            case "Forest": return 10;
            case "Hill": return 15;
            case "Mountain":
            case "MountainRoad": return 20;
            case "City": return 40;
            default: return 0;
        }
    }

    public bool IsCity(int q, int r) =>
        tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile) && tile.terrain == "City";

    public int GetCityOwner(int q, int r)
    {
        return tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile) && tile.terrain == "City"
            ? tile.ownerTeam : -2;
    }

    public bool CaptureCity(int q, int r, int team)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile) || tile.terrain != "City") return false;
        tile.ownerTeam = team;
        RefreshTileColor(tile);
        return true;
    }

    public void SetMovementHighlights(IEnumerable<Vector2Int> coordinates)
    {
        foreach (TileInfo tile in tilesByCoordinates.Values) tile.isReachable = false;
        if (coordinates != null)
            foreach (Vector2Int coordinate in coordinates)
                if (tilesByCoordinates.TryGetValue(coordinate, out TileInfo tile)) tile.isReachable = true;
        foreach (TileInfo tile in tilesByCoordinates.Values) RefreshTileColor(tile);
    }

    public void SetActionHighlights(IEnumerable<Vector2Int> coordinates)
    {
        foreach (TileInfo tile in tilesByCoordinates.Values) tile.isActionTarget = false;
        if (coordinates != null)
            foreach (Vector2Int coordinate in coordinates)
                if (tilesByCoordinates.TryGetValue(coordinate, out TileInfo tile)) tile.isActionTarget = true;
        foreach (TileInfo tile in tilesByCoordinates.Values) RefreshTileColor(tile);
    }

    private void RefreshTileColor(TileInfo tile)
    {
        if (tile.material == null) return;
        if (tile.isReachable) { tile.material.color = reachableTileColor; return; }
        if (tile.isActionTarget) { tile.material.color = actionTargetColor; return; }
        if (tile.terrain == "City")
            tile.material.color = tile.ownerTeam == 0 ? playerFactionColor :
                                  tile.ownerTeam == 1 ? opponentFactionColor : neutralCityColor;
        else tile.material.color = tile.baseColor;
    }

    private Mesh CreateHexPrismMesh(float radius, float thickness)
    {
        float half = thickness * 0.5f;
        Vector3[] vertices = new Vector3[38];
        int[] triangles = new int[72];
        vertices[0] = new Vector3(0f, half, 0f);
        vertices[7] = new Vector3(0f, -half, 0f);
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.Deg2Rad * 60f * i;
            float x = Mathf.Cos(angle) * radius, z = Mathf.Sin(angle) * radius;
            vertices[1 + i] = new Vector3(x, half, z);
            vertices[8 + i] = new Vector3(x, -half, z);
        }
        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6, start = 14 + i * 4;
            vertices[start] = vertices[1 + i];
            vertices[start + 1] = vertices[1 + next];
            vertices[start + 2] = vertices[8 + next];
            vertices[start + 3] = vertices[8 + i];
        }
        int t = 0;
        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6, start = 14 + i * 4;
            triangles[t++] = 0; triangles[t++] = 1 + next; triangles[t++] = 1 + i;
            triangles[t++] = 7; triangles[t++] = 8 + i; triangles[t++] = 8 + next;
            triangles[t++] = start; triangles[t++] = start + 1; triangles[t++] = start + 2;
            triangles[t++] = start; triangles[t++] = start + 2; triangles[t++] = start + 3;
        }
        Mesh mesh = new Mesh { name = "GeneratedHexPrism" };
        mesh.vertices = vertices; mesh.triangles = triangles; mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }
}