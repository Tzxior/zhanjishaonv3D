using System.Collections.Generic;
using UnityEngine;

public enum HexUnitClass { Infantry, HeavyGround, Air }

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

    [Header("Movement Highlight")]
    public Color reachableTileColor = new Color(0.18f, 0.78f, 0.88f);
    public Color neutralCityColor = new Color(0.75f, 0.69f, 0.42f);
    public Color blueCityColor = new Color(0.18f, 0.42f, 0.82f);
    public Color redCityColor = new Color(0.78f, 0.22f, 0.20f);

    private Mesh hexMesh;
    private Camera mainCamera;
    private readonly Dictionary<Collider, TileInfo> tilesByCollider = new Dictionary<Collider, TileInfo>();
    private readonly Dictionary<Vector2Int, TileInfo> tilesByCoordinates = new Dictionary<Vector2Int, TileInfo>();

    private class TileInfo
    {
        public int q, r;
        public string terrain;
        public Material material;
        public Color baseColor;
        public bool isReachable;
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
        }
        Debug.Log($"Hex battlefield ready: {columns} x {rows} = {columns * rows} hexes.");
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
        else if (value < 39) { terrain = "Forest"; color = new Color(0.16f, 0.34f, 0.21f); }
        else if (value < 57) { terrain = "Dryland"; color = new Color(0.63f, 0.53f, 0.34f); }
        else { terrain = "Grassland"; color = new Color(0.32f, 0.49f, 0.29f); }
    }

    private void PositionCamera()
    {
        mainCamera = Camera.main;
        if (mainCamera == null) { Debug.LogWarning("No MainCamera found."); return; }
        Vector3 center = GetMapCenter();
        mainCamera.transform.position = center + new Vector3(0f, cameraHeight, -cameraBackOffset);
        mainCamera.transform.LookAt(center);
        mainCamera.fieldOfView = cameraFieldOfView;
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
        return unitClass == HexUnitClass.Air || tile.terrain != "Water";
    }

    public int GetMovementCostAP(int q, int r, HexUnitClass unitClass)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)) return int.MaxValue;
        if (unitClass == HexUnitClass.Air) return 10;
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

    public int GetDefenseBonus(int q, int r)
    {
        if (!tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)) return 0;
        switch (tile.terrain)
        {
            case "Forest": return 10;
            case "Mountain":
            case "MountainRoad": return 20;
            case "City": return 25;
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

    private void RefreshTileColor(TileInfo tile)
    {
        if (tile.material == null) return;
        if (tile.isReachable) { tile.material.color = reachableTileColor; return; }
        if (tile.terrain == "City")
            tile.material.color = tile.ownerTeam == 0 ? blueCityColor : tile.ownerTeam == 1 ? redCityColor : neutralCityColor;
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