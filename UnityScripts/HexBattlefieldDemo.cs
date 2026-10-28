using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// First prototype of the strategy battlefield: generates a flat-top hex grid,
/// colors cells by terrain type, positions the camera, and lets the player select cells.
/// The grid is generated at runtime and will disappear when Play mode stops.
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

    private Mesh hexMesh;
    private Camera mainCamera;
    private readonly Dictionary<Collider, TileInfo> tilesByCollider =
        new Dictionary<Collider, TileInfo>();
    private TileInfo selectedTile;

    private class TileInfo
    {
        public int q;
        public int r;
        public string terrain;
        public Material material;
        public Color baseColor;
    }

    private void Start()
    {
        columns = Mathf.Max(1, columns);
        rows = Mathf.Max(1, rows);
        hexMesh = CreateHexPrismMesh(hexSize * 0.96f, tileThickness);
        BuildMap();
        PositionCamera();
    }

    private void Update()
    {
        HandleTileSelection();
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

        // A dark backing plane shows through the small gaps between hexes.
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

                string terrain;
                Color terrainColor;
                GetTerrain(q, r, out terrain, out terrainColor);

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
                    baseColor = terrainColor
                };
                tilesByCollider[meshCollider] = info;
            }
        }

        Debug.Log($"Hex battlefield ready: {columns} x {rows} = {columns * rows} hexes. Click a hex in Game view to select it.");
    }

    private void GetTerrain(int q, int r, out string terrain, out Color color)
    {
        // Deterministic pseudo-random layout so the map stays the same each run.
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
            Debug.LogWarning("HexBattlefieldDemo: No camera tagged MainCamera was found. Add a camera and set its Tag to MainCamera.");
            return;
        }

        Vector3 center = GetMapCenter();
        mainCamera.transform.position = center + new Vector3(0f, cameraHeight, -cameraBackOffset);
        mainCamera.transform.LookAt(center);
        mainCamera.fieldOfView = cameraFieldOfView;
    }

    private void HandleTileSelection()
    {
        bool clicked = false;
        Vector2 pointerPosition = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            clicked = true;
            pointerPosition = Mouse.current.position.ReadValue();
        }
#else
        if (Input.GetMouseButtonDown(0))
        {
            clicked = true;
            pointerPosition = Input.mousePosition;
        }
#endif

        if (!clicked || mainCamera == null)
            return;

        Ray ray = mainCamera.ScreenPointToRay(pointerPosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f) &&
            tilesByCollider.TryGetValue(hit.collider, out TileInfo tile))
        {
            if (selectedTile != null)
                selectedTile.material.color = selectedTile.baseColor;

            selectedTile = tile;
            selectedTile.material.color = new Color(1f, 0.82f, 0.16f);
            Debug.Log($"Selected hex ({tile.q}, {tile.r}) - Terrain: {tile.terrain}");
        }
    }

    private Mesh CreateHexPrismMesh(float radius, float thickness)
    {
        float half = thickness * 0.5f;
        Vector3[] vertices = new Vector3[38];
        int[] triangles = new int[72];

        vertices[0] = new Vector3(0f, half, 0f);   // Top center
        vertices[7] = new Vector3(0f, -half, 0f); // Bottom center

        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.Deg2Rad * (60f * i);
            float x = Mathf.Cos(angle) * radius;
            float z = Mathf.Sin(angle) * radius;
            vertices[1 + i] = new Vector3(x, half, z);
            vertices[8 + i] = new Vector3(x, -half, z);
        }

        // Separate vertices for the sides keep the top and side normals crisp.
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

            // Top face: upward-facing triangles.
            triangles[t++] = 0;
            triangles[t++] = 1 + next;
            triangles[t++] = 1 + i;

            // Bottom face: downward-facing triangles.
            triangles[t++] = 7;
            triangles[t++] = 8 + i;
            triangles[t++] = 8 + next;

            // Side face.
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
