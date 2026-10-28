using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 生成六边形战场地图，并统一管理地块颜色与可通行性。
/// 与 HexUnitDemo 挂在同一个 GameObject 上。
/// </summary>
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

    private Mesh hexMesh;
    private Camera mainCamera;

    private readonly Dictionary<Collider, TileInfo> tilesByCollider =
        new Dictionary<Collider, TileInfo>();

    private readonly Dictionary<Vector2Int, TileInfo> tilesByCoordinates =
        new Dictionary<Vector2Int, TileInfo>();

    private class TileInfo
    {
        public int q;
        public int r;
        public string terrain;
        public Material material;
        public Color baseColor;
        public bool isReachable;
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
        float centerX = (columns - 1) * 1.5f * hexSize * 0.5f;
        float centerZ = Mathf.Sqrt(3f) * hexSize *
                        ((rows - 1) + (columns - 1) * 0.5f) * 0.5f;
        return new Vector3(centerX, 0f, centerZ);
    }

    private void BuildMap()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
            shader = Shader.Find("Standard");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        if (shader == null)
        {
            Debug.LogError("HexBattlefieldDemo: Could not find a compatible material shader.");
            return;
        }

        float mapWidth = (columns - 1) * 1.5f * hexSize + 2f * hexSize;
        float mapDepth = Mathf.Sqrt(3f) * hexSize *
                         ((rows - 1) + (columns - 1) * 0.5f) +
                         Mathf.Sqrt(3f) * hexSize;

        GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Plane);
        backdrop.name = "HexMapBackdrop";
        backdrop.transform.SetParent(transform, false);

        Vector3 mapCenter = GetMapCenter();
        backdrop.transform.position = new Vector3(
            mapCenter.x, -tileThickness * 0.5f - 0.06f, mapCenter.z);
        backdrop.transform.localScale = new Vector3(mapWidth / 10f, 1f, mapDepth / 10f);

        Material backingMaterial = new Material(shader);
        backingMaterial.color = new Color(0.12f, 0.14f, 0.13f);
        backdrop.GetComponent<Renderer>().sharedMaterial = backingMaterial;

        float verticalStep = Mathf.Sqrt(3f) * hexSize;

        for (int q = 0; q < columns; q++)
        {
            for (int r = 0; r < rows; r++)
            {
                Vector3 position = new Vector3(
                    1.5f * hexSize * q,
                    0f,
                    verticalStep * (r + q * 0.5f));

                GetTerrain(q, r, out string terrain, out Color terrainColor);

                GameObject tile = new GameObject($"Hex_{q}_{r}_{terrain}");
                tile.transform.SetParent(transform, false);
                tile.transform.localPosition = position;

                MeshFilter meshFilter = tile.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = hexMesh;

                MeshRenderer meshRenderer = tile.AddComponent<MeshRenderer>();
                Material tileMaterial = new Material(shader);
                tileMaterial.color = terrainColor;
                meshRenderer.sharedMaterial = tileMaterial;

                MeshCollider meshCollider = tile.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = hexMesh;

                TileInfo info = new TileInfo
                {
                    q = q,
                    r = r,
                    terrain = terrain,
                    material = tileMaterial,
                    baseColor = terrainColor,
                    isReachable = false
                };

                tilesByCollider[meshCollider] = info;
                tilesByCoordinates[new Vector2Int(q, r)] = info;
            }
        }

        Debug.Log($"Hex battlefield ready: {columns} x {rows} = {columns * rows} hexes.");
    }

    private void GetTerrain(int q, int r, out string terrain, out Color color)
    {
        int value = (q * 37 + r * 19 + q * r * 13) % 100;

        if (value < 8)
        {
            terrain = "Water";
            color = new Color(0.16f, 0.36f, 0.55f);
        }
        else if (value < 21)
        {
            terrain = "Rock";
            color = new Color(0.43f, 0.45f, 0.46f);
        }
        else if (value < 40)
        {
            terrain = "Forest";
            color = new Color(0.16f, 0.34f, 0.21f);
        }
        else if (value < 57)
        {
            terrain = "Dryland";
            color = new Color(0.63f, 0.53f, 0.34f);
        }
        else
        {
            terrain = "Grassland";
            color = new Color(0.32f, 0.49f, 0.29f);
        }
    }

    private void PositionCamera()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("HexBattlefieldDemo: 找不到标记为 MainCamera 的摄像机。");
            return;
        }

        Vector3 center = GetMapCenter();
        mainCamera.transform.position = center + new Vector3(0f, cameraHeight, -cameraBackOffset);
        mainCamera.transform.LookAt(center);
        mainCamera.fieldOfView = cameraFieldOfView;
    }

    /// <summary>将当前可移动范围内的地块染色；传入空集合则清除高亮。</summary>
    public void SetMovementHighlights(IEnumerable<Vector2Int> coordinates)
    {
        foreach (TileInfo tile in tilesByCoordinates.Values)
        {
            tile.isReachable = false;
        }

        if (coordinates != null)
        {
            foreach (Vector2Int coordinate in coordinates)
            {
                if (tilesByCoordinates.TryGetValue(coordinate, out TileInfo tile))
                {
                    tile.isReachable = true;
                }
            }
        }

        foreach (TileInfo tile in tilesByCoordinates.Values)
        {
            RefreshTileColor(tile);
        }
    }

    public bool IsTileWalkable(int q, int r)
    {
        return tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile)
               && tile.terrain != "Water";
    }

    public bool IsInsideMap(int q, int r)
    {
        return q >= 0 && q < columns && r >= 0 && r < rows;
    }

    public bool TryGetTerrain(int q, int r, out string terrain)
    {
        if (tilesByCoordinates.TryGetValue(new Vector2Int(q, r), out TileInfo tile))
        {
            terrain = tile.terrain;
            return true;
        }

        terrain = "";
        return false;
    }

    private void RefreshTileColor(TileInfo tile)
    {
        if (tile.material == null)
            return;

        tile.material.color = tile.isReachable
            ? reachableTileColor
            : tile.baseColor;
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
            int next = (i + 1) % 6;
            float angle = Mathf.Deg2Rad * (60f * i);
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;

            vertices[1 + i] = new Vector3(x, half, z);
            vertices[8 + i] = new Vector3(x, -half, z);
        }

        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6;
            int start = 14 + i * 4;

            vertices[start] = vertices[1 + i];
            vertices[start + 1] = vertices[1 + next];
            vertices[start + 2] = vertices[8 + next];
            vertices[start + 3] = vertices[8 + i];
        }

        int t = 0;
        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6;

            triangles[t++] = 0;
            triangles[t++] = 1 + next;
            triangles[t++] = 1 + i;

            triangles[t++] = 7;
            triangles[t++] = 8 + i;
            triangles[t++] = 8 + next;

            int start = 14 + i * 4;
            triangles[t++] = start;
            triangles[t++] = start + 1;
            triangles[t++] = start + 2;
            triangles[t++] = start;
            triangles[t++] = start + 2;
            triangles[t++] = start + 3;
        }

        Mesh mesh = new Mesh();
        mesh.name = "GeneratedHexPrism";
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}