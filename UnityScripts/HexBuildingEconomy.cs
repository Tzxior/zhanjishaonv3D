using System;
using System.Collections.Generic;
using UnityEngine;

public enum HexBuildingType
{
    Headquarters,
    LargeCity,
    SmallCity,
    Village,
    Arsenal,
    TankFactory,
    ArtilleryFactory,
    Shipyard,
    Airport,
    IronMine,
    AluminiumMine
}

/// <summary>
/// Configurable strategic buildings, passive income, field repair, temporary production-resource reservations,
/// and one active production order per factory. Add this beside HexBattlefieldDemo and HexUnitDemo.
/// </summary>
public class HexBuildingEconomy : MonoBehaviour
{
    [Serializable]
    public class BuildingPlacement
    {
        public int q;
        public int r;
        public HexBuildingType type;
        [Tooltip("-1 = neutral; 0 = player; 1 = opponent. More teams can be added later.")]
        public int ownerTeam = -1;
    }

    [Serializable]
    public class UnitProductionProfile
    {
        public string unitId;
        public string displayName;
        [Min(1)] public int productionAP = 100;
        [Min(0)] public int moneyCost = 100;
        [Min(0)] public int labourRequired = 1;
        [Min(0)] public int ironRequired;
        [Min(0)] public int aluminiumRequired;
        public HexBuildingType[] allowedBuildings = new HexBuildingType[0];
    }

    private sealed class ProductionOrder
    {
        public UnitProductionProfile profile;
        public int team;
        public float progress;
        public float appliedMoneyCost;
    }

    private sealed class BuildingState
    {
        public int q;
        public int r;
        public HexBuildingType type;
        public int ownerTeam;
        public GameObject marker;
        public TextMesh label;
        public Renderer factionBannerRenderer;
        public Renderer factionBaseRenderer;
        public ProductionOrder activeOrder;
        public float nextReadyTime;
        public float zeroCostActedAtTime = -1f;
        public GameObject productionBarRoot;
        public Renderer productionBarFill;
    }

    private sealed class TeamWallet
    {
        public int money;
        public int labour;
        public int reservedLabour;
        public int reservedIron;
        public int reservedAluminium;
    }

    [Header("Test001 building layout")]
    public BuildingPlacement[] initialBuildings =
    {
        new BuildingPlacement { q = 1, r = 1, type = HexBuildingType.Headquarters, ownerTeam = 0 },
        new BuildingPlacement { q = 7, r = 7, type = HexBuildingType.Headquarters, ownerTeam = 1 },
        new BuildingPlacement { q = 4, r = 4, type = HexBuildingType.LargeCity, ownerTeam = -1 },
        new BuildingPlacement { q = 1, r = 4, type = HexBuildingType.Arsenal, ownerTeam = -1 },
        new BuildingPlacement { q = 2, r = 5, type = HexBuildingType.TankFactory, ownerTeam = -1 },
        new BuildingPlacement { q = 3, r = 4, type = HexBuildingType.ArtilleryFactory, ownerTeam = -1 },
        new BuildingPlacement { q = 6, r = 6, type = HexBuildingType.Shipyard, ownerTeam = -1 },
        new BuildingPlacement { q = 6, r = 4, type = HexBuildingType.Airport, ownerTeam = -1 },
        new BuildingPlacement { q = 2, r = 2, type = HexBuildingType.IronMine, ownerTeam = -1 },
        new BuildingPlacement { q = 5, r = 6, type = HexBuildingType.AluminiumMine, ownerTeam = -1 },
        new BuildingPlacement { q = 7, r = 2, type = HexBuildingType.Village, ownerTeam = -1 },
        new BuildingPlacement { q = 1, r = 6, type = HexBuildingType.SmallCity, ownerTeam = -1 }
    };

    [Header("Starting economy (prototype values; adjust in Inspector)")]
    [Min(0)] public int startingMoney = 500;
    [Min(0)] public int startingLabour = 2;

    [Header("Unit production data")]
    [Tooltip("留空时自动从 Resources/GadgetUnitProfiles.json 读取单位造价与生产时间；填写后以 Inspector 中的自定义配置为准。")]
    public UnitProductionProfile[] productionProfiles = new UnitProductionProfile[0];

    private readonly List<BuildingState> buildings = new List<BuildingState>();
    private readonly Dictionary<Vector2Int, BuildingState> buildingsByCoordinate = new Dictionary<Vector2Int, BuildingState>();
    private readonly Dictionary<int, TeamWallet> wallets = new Dictionary<int, TeamWallet>();
    private HexBattlefieldDemo battlefield;
    private HexUnitDemo unitController;
    private bool initialized;
    private Color playerFactionColor = new Color(0.86f, 0.16f, 0.21f, 1f);
    private Color opponentFactionColor = new Color(0.16f, 0.39f, 0.86f, 1f);
    private readonly Dictionary<Color, Material> generatedMaterials = new Dictionary<Color, Material>();
    private bool resultWasReported;
    private BuildingPlacement[] levelOneBuildings;
    private int levelOneStartingMoney;
    private int levelOneStartingLabour;
    private bool levelOneConfigurationCaptured;

    public int PlayerTeamId => 0;
    public event Action<int, int, HexBuildingType> HeadquartersCaptured;

    public void InitializeForBattlefield(HexBattlefieldDemo map, HexUnitDemo units)
    {
        if (initialized)
        {
            battlefield = map != null ? map : battlefield;
            unitController = units != null ? units : unitController;
            EnsureProductionProfilesFromUnitDatabase();
            return;
        }

        battlefield = map;
        unitController = units;
        if (!levelOneConfigurationCaptured)
        {
            levelOneBuildings = initialBuildings != null
                ? (BuildingPlacement[])initialBuildings.Clone()
                : new BuildingPlacement[0];
            levelOneStartingMoney = startingMoney;
            levelOneStartingLabour = startingLabour;
            levelOneConfigurationCaptured = true;
        }
        EnsureProductionProfilesFromUnitDatabase();
        if (battlefield == null)
        {
            Debug.LogError("HexBuildingEconomy requires a HexBattlefieldDemo on the same GameObject.");
            return;
        }

        EnsureWallet(0);
        EnsureWallet(1);
        BuildBuildings();
        initialized = true;
    }

    private void Start()
    {
        if (!initialized)
            InitializeForBattlefield(GetComponent<HexBattlefieldDemo>(), GetComponent<HexUnitDemo>());
    }

    private Camera markerCamera;

    private void LateUpdate()
    {
        if (markerCamera == null) markerCamera = Camera.main;
        if (markerCamera == null) return;
        foreach (BuildingState building in buildings)
        {
            if (building == null) continue;
            if (building.label != null)
            {
                ProductionOrder order = building.activeOrder;
                building.label.text = ShortName(building.type) +
                    (IsFactory(building.type) && order != null && order.profile != null
                        ? "\n" + Mathf.Clamp(Mathf.FloorToInt(100f * order.progress /
                            Mathf.Max(1, order.profile.productionAP)), 0, 100) + "%"
                        : "");
                Vector3 toCamera = markerCamera.transform.position - building.label.transform.position;
                if (toCamera.sqrMagnitude > 0.001f)
                    building.label.transform.rotation =
                        Quaternion.LookRotation(toCamera, Vector3.up) * Quaternion.Euler(0f, 180f, 0f);
            }

            if (building.productionBarRoot != null)
            {
                ProductionOrder order = building.activeOrder;
                bool active = IsFactory(building.type) && order != null && order.profile != null;
                building.productionBarRoot.SetActive(active);
                if (active)
                {
                    Vector3 toCamera = markerCamera.transform.position - building.productionBarRoot.transform.position;
                    if (toCamera.sqrMagnitude > 0.001f)
                        building.productionBarRoot.transform.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
                    float ratio = Mathf.Clamp01(order.progress / Mathf.Max(1, order.profile.productionAP));
                    Transform fill = building.productionBarFill != null ? building.productionBarFill.transform : null;
                    if (fill != null)
                    {
                        const float width = 0.90f;
                        fill.localScale = new Vector3(width * ratio, 0.065f, 0.028f);
                        fill.localPosition = new Vector3(-width * 0.5f + width * ratio * 0.5f, 0f, -0.018f);
                    }
                }
            }
        }
    }

    private void OnDestroy()
    {
        foreach (BuildingState building in buildings)
        {
            if (building != null && building.marker != null)
                Destroy(building.marker);
            if (building != null && building.productionBarRoot != null)
                Destroy(building.productionBarRoot);
        }
    }

    private TeamWallet EnsureWallet(int team)
    {
        if (!wallets.TryGetValue(team, out TeamWallet wallet))
        {
            wallet = new TeamWallet { money = startingMoney, labour = startingLabour };
            wallets[team] = wallet;
        }
        return wallet;
    }

    public void ConfigureLevel(int levelIndex)
    {
        if (!levelOneConfigurationCaptured)
        {
            levelOneBuildings = initialBuildings != null
                ? (BuildingPlacement[])initialBuildings.Clone()
                : new BuildingPlacement[0];
            levelOneStartingMoney = startingMoney;
            levelOneStartingLabour = startingLabour;
            levelOneConfigurationCaptured = true;
        }

        if (levelIndex <= 0)
        {
            initialBuildings = levelOneBuildings != null
                ? (BuildingPlacement[])levelOneBuildings.Clone()
                : new BuildingPlacement[0];
            startingMoney = levelOneStartingMoney;
            startingLabour = levelOneStartingLabour;
            return;
        }

        initialBuildings = new[]
        {
            new BuildingPlacement { q = 1, r = 1, type = HexBuildingType.Headquarters, ownerTeam = 0 },
            new BuildingPlacement { q = 9, r = 9, type = HexBuildingType.Headquarters, ownerTeam = 1 },
            new BuildingPlacement { q = 5, r = 5, type = HexBuildingType.LargeCity, ownerTeam = -1 },
            new BuildingPlacement { q = 4, r = 9, type = HexBuildingType.SmallCity, ownerTeam = -1 },
            new BuildingPlacement { q = 7, r = 2, type = HexBuildingType.SmallCity, ownerTeam = -1 },
            new BuildingPlacement { q = 2, r = 3, type = HexBuildingType.Village, ownerTeam = -1 },
            new BuildingPlacement { q = 8, r = 7, type = HexBuildingType.Village, ownerTeam = -1 },
            new BuildingPlacement { q = 1, r = 4, type = HexBuildingType.Arsenal, ownerTeam = 0 },
            new BuildingPlacement { q = 2, r = 5, type = HexBuildingType.TankFactory, ownerTeam = -1 },
            new BuildingPlacement { q = 3, r = 4, type = HexBuildingType.ArtilleryFactory, ownerTeam = -1 },
            new BuildingPlacement { q = 1, r = 7, type = HexBuildingType.Shipyard, ownerTeam = 0 },
            new BuildingPlacement { q = 2, r = 4, type = HexBuildingType.Airport, ownerTeam = 0 },
            new BuildingPlacement { q = 4, r = 2, type = HexBuildingType.IronMine, ownerTeam = 0 },
            new BuildingPlacement { q = 7, r = 8, type = HexBuildingType.AluminiumMine, ownerTeam = 1 },
            new BuildingPlacement { q = 8, r = 4, type = HexBuildingType.TankFactory, ownerTeam = 1 },
            new BuildingPlacement { q = 7, r = 7, type = HexBuildingType.ArtilleryFactory, ownerTeam = 1 }
        };
        startingMoney = 700;
        startingLabour = 4;
    }

    public void ResetForNewBattle(HexUnitDemo driver)
    {
        if (driver != null) unitController = driver;
        foreach (BuildingState building in buildings)
        {
            if (building != null && building.marker != null)
                Destroy(building.marker);
            if (building != null && building.productionBarRoot != null)
                Destroy(building.productionBarRoot);
        }
        buildings.Clear();
        buildingsByCoordinate.Clear();
        wallets.Clear();
        initialized = false;
        resultWasReported = false;
        InitializeForBattlefield(battlefield != null ? battlefield : GetComponent<HexBattlefieldDemo>(), unitController);
    }

    private void BuildBuildings()
    {
        buildings.Clear();
        buildingsByCoordinate.Clear();
        if (initialBuildings == null) return;

        foreach (BuildingPlacement placement in initialBuildings)
        {
            if (placement == null || !battlefield.IsInsideMap(placement.q, placement.r))
            {
                if (placement != null)
                    Debug.LogWarning($"Building at ({placement.q},{placement.r}) is outside the map and was skipped.");
                continue;
            }

            Vector2Int coordinate = new Vector2Int(placement.q, placement.r);
            if (buildingsByCoordinate.ContainsKey(coordinate))
            {
                Debug.LogWarning($"More than one building uses ({placement.q},{placement.r}); only the first was created.");
                continue;
            }

            var building = new BuildingState
            {
                q = placement.q,
                r = placement.r,
                type = placement.type,
                ownerTeam = placement.ownerTeam
            };
            buildings.Add(building);
            buildingsByCoordinate.Add(coordinate, building);
            if (building.ownerTeam >= 0) EnsureWallet(building.ownerTeam);

            if ((building.type == HexBuildingType.LargeCity || building.type == HexBuildingType.SmallCity) &&
                battlefield.IsCity(building.q, building.r))
                battlefield.CaptureCity(building.q, building.r, building.ownerTeam);

            CreateMarker(building);
        }
    }

    private void EnsureProductionProfilesFromUnitDatabase()
    {
        if (productionProfiles != null && productionProfiles.Length > 0) return;
        if (unitController == null) return;
        UnitProductionProfile[] generated = unitController.BuildProductionProfilesFromUnitDatabase();
        if (generated != null && generated.Length > 0)
            productionProfiles = generated;
    }

    private Vector3 TilePosition(int q, int r)
    {
        float size = battlefield.hexSize;
        return new Vector3(
            1.5f * size * q,
            battlefield.tileThickness * 0.5f + 0.012f,
            Mathf.Sqrt(3f) * size * (r + q * 0.5f));
    }

    private Material GetOrCreateMaterial(Color color, Shader shader)
    {
        if (generatedMaterials.TryGetValue(color, out Material cached) && cached != null)
            return cached;
        Material material = new Material(shader) { color = color };
        generatedMaterials[color] = material;
        return material;
    }

    private Renderer AddBuildingPart(Transform parent, PrimitiveType shape, string partName,
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
        if (renderer != null) renderer.sharedMaterial = GetOrCreateMaterial(color, shader);
        return renderer;
    }

    private void CreateBuildingSilhouette(BuildingState building, Shader shader)
    {
        Transform root = building.marker.transform;
        Color concrete = new Color(0.46f, 0.48f, 0.49f);
        Color roof = new Color(0.24f, 0.27f, 0.30f);
        Color wall = new Color(0.61f, 0.57f, 0.48f);
        Color industrial = new Color(0.36f, 0.40f, 0.43f);
        Color metal = new Color(0.68f, 0.72f, 0.73f);

        switch (building.type)
        {
            case HexBuildingType.Headquarters:
                AddBuildingPart(root, PrimitiveType.Cube, "CommandHall",
                    new Vector3(0f, 0.24f, 0f), new Vector3(0.48f, 0.48f, 0.42f), wall, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "CommandTower",
                    new Vector3(0f, 0.57f, 0f), new Vector3(0.24f, 0.28f, 0.25f), roof, shader);
                AddBuildingPart(root, PrimitiveType.Cylinder, "RadarMast",
                    new Vector3(0.05f, 0.82f, -0.02f), new Vector3(0.025f, 0.18f, 0.025f), metal, shader);
                AddBuildingPart(root, PrimitiveType.Sphere, "RadarDish",
                    new Vector3(0.05f, 0.98f, -0.02f), new Vector3(0.15f, 0.045f, 0.15f), metal, shader);
                break;

            case HexBuildingType.LargeCity:
                AddBuildingPart(root, PrimitiveType.Cube, "CityTowerA",
                    new Vector3(-0.20f, 0.32f, 0f), new Vector3(0.25f, 0.64f, 0.25f), concrete, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "CityTowerB",
                    new Vector3(0.12f, 0.23f, -0.10f), new Vector3(0.28f, 0.46f, 0.26f), wall, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "CityBlockC",
                    new Vector3(0.16f, 0.13f, 0.20f), new Vector3(0.25f, 0.26f, 0.20f), roof, shader);
                break;

            case HexBuildingType.SmallCity:
                AddBuildingPart(root, PrimitiveType.Cube, "SmallCityBlockA",
                    new Vector3(-0.14f, 0.13f, 0f), new Vector3(0.30f, 0.26f, 0.27f), wall, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "SmallCityBlockB",
                    new Vector3(0.16f, 0.18f, 0.12f), new Vector3(0.24f, 0.36f, 0.22f), concrete, shader);
                break;

            case HexBuildingType.Village:
                for (int i = 0; i < 3; i++)
                {
                    float x = (i - 1) * 0.23f;
                    float z = i % 2 == 0 ? 0.12f : -0.13f;
                    AddBuildingPart(root, PrimitiveType.Cube, "VillageHouse",
                        new Vector3(x, 0.12f, z), new Vector3(0.18f, 0.22f, 0.18f), wall, shader);
                    AddBuildingPart(root, PrimitiveType.Cylinder, "VillageRoof",
                        new Vector3(x, 0.25f, z), new Vector3(0.14f, 0.08f, 0.14f), new Color(0.43f, 0.22f, 0.16f), shader);
                }
                break;

            case HexBuildingType.Arsenal:
                AddBuildingPart(root, PrimitiveType.Cube, "ArsenalWarehouse",
                    new Vector3(0f, 0.18f, 0f), new Vector3(0.58f, 0.36f, 0.42f), industrial, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "ArsenalRoof",
                    new Vector3(0f, 0.39f, 0f), new Vector3(0.62f, 0.08f, 0.46f), roof, shader);
                for (int i = 0; i < 2; i++)
                    AddBuildingPart(root, PrimitiveType.Cube, "AmmoCrate",
                        new Vector3(-0.22f + i * 0.42f, 0.08f, 0.27f), new Vector3(0.15f, 0.16f, 0.12f), wall, shader);
                break;

            case HexBuildingType.TankFactory:
                AddBuildingPart(root, PrimitiveType.Cube, "TankFactoryHall",
                    new Vector3(0f, 0.20f, 0f), new Vector3(0.62f, 0.40f, 0.46f), industrial, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "TankFactoryRoof",
                    new Vector3(0f, 0.43f, 0f), new Vector3(0.66f, 0.08f, 0.50f), roof, shader);
                AddBuildingPart(root, PrimitiveType.Cylinder, "FactoryStack",
                    new Vector3(0.20f, 0.58f, -0.10f), new Vector3(0.06f, 0.24f, 0.06f), metal, shader);
                AddBuildingPart(root, PrimitiveType.Cube, "TankSilhouette",
                    new Vector3(-0.02f, 0.08f, 0.31f), new Vector3(0.34f, 0.10f, 0.13f), new Color(0.20f, 0.28f, 0.18f), shader);
                break;

            case HexBuildingType.ArtilleryFactory:
                AddBuildingPart(root, PrimitiveType.Cube, "ArtilleryHall",
                    new Vector3(-0.05f, 0.18f, 0f), new Vector3(0.53f, 0.36f, 0.42f), industrial, shader);
                AddBuildingPart(root, PrimitiveType.Cylinder, "ArtilleryBarrel",
                    new Vector3(0.23f, 0.16f, 0.18f), new Vector3(0.055f, 0.28f, 0.055f), metal, shader, new Vector3(0f, 0f, -65f));
                break;

            case HexBuildingType.Shipyard:
                AddBuildingPart(root, PrimitiveType.Cube, "ShipyardDock",
                    new Vector3(0f, 0.06f, -0.10f), new Vector3(0.76f, 0.12f, 0.28f), new Color(0.40f, 0.30f, 0.22f), shader);
                AddBuildingPart(root, PrimitiveType.Cube, "ShipHull",
                    new Vector3(0.06f, 0.16f, 0.18f), new Vector3(0.50f, 0.14f, 0.18f), new Color(0.20f, 0.31f, 0.36f), shader, new Vector3(0f, 0f, -12f));
                AddBuildingPart(root, PrimitiveType.Cube, "ShipBridge",
                    new Vector3(-0.04f, 0.28f, 0.18f), new Vector3(0.16f, 0.14f, 0.14f), concrete, shader);
                break;

            case HexBuildingType.Airport:
                AddBuildingPart(root, PrimitiveType.Cube, "Runway",
                    new Vector3(0f, 0.035f, 0f), new Vector3(0.82f, 0.055f, 0.28f), roof, shader, new Vector3(0f, 0f, -18f));
                AddBuildingPart(root, PrimitiveType.Cube, "RunwayMarking",
                    new Vector3(0f, 0.065f, 0f), new Vector3(0.58f, 0.008f, 0.025f), metal, shader, new Vector3(0f, 0f, -18f));
                AddBuildingPart(root, PrimitiveType.Cube, "AircraftBody",
                    new Vector3(0.04f, 0.16f, 0.20f), new Vector3(0.12f, 0.08f, 0.42f), metal, shader, new Vector3(0f, 0f, 15f));
                AddBuildingPart(root, PrimitiveType.Cube, "AircraftWings",
                    new Vector3(0.04f, 0.16f, 0.20f), new Vector3(0.40f, 0.035f, 0.10f), concrete, shader);
                break;

            case HexBuildingType.IronMine:
                AddBuildingPart(root, PrimitiveType.Sphere, "IronOreA",
                    new Vector3(-0.17f, 0.13f, 0.03f), new Vector3(0.32f, 0.26f, 0.29f), new Color(0.27f, 0.29f, 0.30f), shader);
                AddBuildingPart(root, PrimitiveType.Sphere, "IronOreB",
                    new Vector3(0.11f, 0.11f, 0.14f), new Vector3(0.26f, 0.22f, 0.25f), new Color(0.39f, 0.31f, 0.27f), shader);
                AddBuildingPart(root, PrimitiveType.Cube, "MineFrame",
                    new Vector3(0.22f, 0.32f, -0.18f), new Vector3(0.06f, 0.45f, 0.06f), metal, shader);
                break;

            case HexBuildingType.AluminiumMine:
                AddBuildingPart(root, PrimitiveType.Sphere, "AluminiumOreA",
                    new Vector3(-0.14f, 0.12f, 0.03f), new Vector3(0.30f, 0.24f, 0.28f), new Color(0.65f, 0.70f, 0.74f), shader);
                AddBuildingPart(root, PrimitiveType.Sphere, "AluminiumOreB",
                    new Vector3(0.12f, 0.11f, 0.14f), new Vector3(0.25f, 0.22f, 0.23f), new Color(0.80f, 0.82f, 0.83f), shader);
                AddBuildingPart(root, PrimitiveType.Cube, "OreConveyor",
                    new Vector3(0.23f, 0.25f, -0.16f), new Vector3(0.07f, 0.28f, 0.07f), roof, shader);
                break;
        }
    }

    private void CreateMarker(BuildingState building)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) { Debug.LogError("HexBuildingEconomy: No compatible shader."); return; }

        GameObject marker = new GameObject($"Building_{building.type}_{building.q}_{building.r}");
        marker.transform.SetParent(transform, false);
        marker.transform.localPosition = TilePosition(building.q, building.r);
        marker.transform.localScale = Vector3.one;
        building.marker = marker;

        building.factionBaseRenderer = AddBuildingPart(marker.transform, PrimitiveType.Cylinder, "OwnershipBase",
            new Vector3(0f, 0.025f, 0f), new Vector3(0.70f, 0.035f, 0.70f), OwnerColor(building.ownerTeam), shader);
        if (building.factionBaseRenderer != null)
            building.factionBaseRenderer.material = new Material(building.factionBaseRenderer.sharedMaterial);
        CreateBuildingSilhouette(building, shader);

        AddBuildingPart(marker.transform, PrimitiveType.Cylinder, "FactionFlagPole",
            new Vector3(0.39f, 0.40f, -0.20f), new Vector3(0.018f, 0.40f, 0.018f), new Color(0.24f, 0.25f, 0.25f), shader);
        building.factionBannerRenderer = AddBuildingPart(marker.transform, PrimitiveType.Cube, "FactionBanner",
            new Vector3(0.51f, 0.62f, -0.20f), new Vector3(0.24f, 0.13f, 0.035f), OwnerColor(building.ownerTeam), shader);
        if (building.factionBannerRenderer != null)
            building.factionBannerRenderer.material = new Material(building.factionBannerRenderer.sharedMaterial);

        GameObject labelObject = new GameObject("BuildingLabel");
        labelObject.transform.SetParent(marker.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 1.06f, 0f);
        labelObject.transform.localScale = Vector3.one * 0.09f;
        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = ShortName(building.type);
        label.fontSize = 64;
        label.characterSize = 0.8f;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.color = OwnerColor(building.ownerTeam);
        building.label = label;

        GameObject barRoot = new GameObject("BuildingProductionProgress");
        barRoot.transform.SetParent(marker.transform, false);
        barRoot.transform.localPosition = new Vector3(0f, 1.48f, 0f);
        barRoot.transform.localScale = Vector3.one;
        building.productionBarRoot = barRoot;
        AddBuildingPart(barRoot.transform, PrimitiveType.Cube, "ProgressBackground",
            Vector3.zero, new Vector3(0.90f, 0.085f, 0.03f), new Color(0.07f, 0.08f, 0.09f), shader);
        Renderer fill = AddBuildingPart(barRoot.transform, PrimitiveType.Cube, "ProgressFill",
            new Vector3(-0.45f, 0f, -0.018f), new Vector3(0.90f, 0.065f, 0.028f),
            new Color(0.15f, 0.80f, 0.90f), shader);
        building.productionBarFill = fill;
        barRoot.SetActive(false);
    }

    public void SetFactionColors(Color playerColor, Color opponentColor)
    {
        playerFactionColor = playerColor;
        opponentFactionColor = opponentColor;
        foreach (BuildingState building in buildings) RefreshMarker(building);
    }

    private Color OwnerColor(int owner)
    {
        switch (owner)
        {
            case 0: return playerFactionColor;
            case 1: return opponentFactionColor;
            case 2: return new Color(0.13f, 0.14f, 0.17f); // Black
            case 3: return new Color(0.19f, 0.58f, 0.31f); // Green
            case 4: return new Color(0.88f, 0.68f, 0.20f); // Yellow
            case 5: return new Color(0.52f, 0.32f, 0.72f); // Purple
            case -1: return new Color(0.72f, 0.72f, 0.68f); // Neutral
            default:
                // Deterministic fallback for additional factions.
                float hue = Mathf.Repeat(owner * 0.173f, 1f);
                return Color.HSVToRGB(hue, 0.65f, 0.9f);
        }
    }

    private string ShortName(HexBuildingType type)
    {
        switch (type)
        {
            case HexBuildingType.Headquarters: return "大本营";
            case HexBuildingType.LargeCity: return "大城市";
            case HexBuildingType.SmallCity: return "小城市";
            case HexBuildingType.Village: return "乡村";
            case HexBuildingType.Arsenal: return "兵营";
            case HexBuildingType.TankFactory: return "战车厂";
            case HexBuildingType.ArtilleryFactory: return "火炮厂";
            case HexBuildingType.Shipyard: return "造船厂";
            case HexBuildingType.Airport: return "机场";
            case HexBuildingType.IronMine: return "铁矿";
            case HexBuildingType.AluminiumMine: return "铝矿";
            default: return type.ToString();
        }
    }

    public string GetBuildingName(HexBuildingType type) => ShortName(type);

    public bool TryGetBuildingAt(int q, int r, out string name, out int owner, out HexBuildingType type)
    {
        if (buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building))
        {
            name = ShortName(building.type);
            owner = building.ownerTeam;
            type = building.type;
            return true;
        }
        name = string.Empty;
        owner = -1;
        type = default;
        return false;
    }

    public bool TryGetBuildingAt(int q, int r, out string name, out int owner,
        out HexBuildingType type, out float nextReadyTime)
    {
        if (buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building))
        {
            name = ShortName(building.type);
            owner = building.ownerTeam;
            type = building.type;
            nextReadyTime = building.nextReadyTime;
            return true;
        }
        name = string.Empty;
        owner = -1;
        type = default;
        nextReadyTime = 0f;
        return false;
    }

    public bool CanCaptureAt(int q, int r, int team)
    {
        return buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) &&
               building.ownerTeam != team;
    }

    public bool TryCaptureBuildingAt(int q, int r, int newOwner, HexUnitDemo driver, out string message)
    {
        message = string.Empty;
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building))
            return false;
        if (building.ownerTeam == newOwner)
        {
            message = "该建筑已经属于当前阵营。";
            return true;
        }

        int previousOwner = building.ownerTeam;
        bool cancelledProduction = false;
        if (building.activeOrder != null && building.activeOrder.team != newOwner)
        {
            ReleaseProductionReservation(building.activeOrder);
            building.activeOrder = null;
            cancelledProduction = true;
        }
        building.ownerTeam = newOwner;
        if (newOwner >= 0)
        {
            EnsureWallet(newOwner);
            if (IsActionQueueBuilding(building.type) && driver != null)
                ReserveBuildingNextActionNode(q, r, driver.CurrentBattleAP + 1f, driver);
        }
        if (newOwner >= 0) GrantCaptureLabour(newOwner, building.type);

        if ((building.type == HexBuildingType.LargeCity || building.type == HexBuildingType.SmallCity) &&
            battlefield.IsCity(q, r))
            battlefield.CaptureCity(q, r, newOwner);

        RefreshMarker(building);
        message = $"已占领{ShortName(building.type)}。" + (cancelledProduction ? " 原生产项目已取消。" : "");

        if (building.type == HexBuildingType.Headquarters && previousOwner >= 0 && previousOwner != newOwner)
        {
            HeadquartersCaptured?.Invoke(newOwner, previousOwner, building.type);
            if (driver != null) driver.OnHeadquartersCaptured(newOwner, previousOwner);
        }
        return true;
    }

    private void GrantCaptureLabour(int team, HexBuildingType type)
    {
        int amount = 0;
        switch (type)
        {
            case HexBuildingType.Headquarters: amount = 2; break;
            case HexBuildingType.LargeCity:
            case HexBuildingType.SmallCity:
            case HexBuildingType.Village: amount = 1; break;
        }
        if (amount > 0) EnsureWallet(team).labour += amount;
    }

    private void RefreshMarker(BuildingState building)
    {
        Color ownerColor = OwnerColor(building.ownerTeam);
        if (building.factionBaseRenderer != null && building.factionBaseRenderer.sharedMaterial != null)
            building.factionBaseRenderer.sharedMaterial.color = ownerColor;
        if (building.factionBannerRenderer != null && building.factionBannerRenderer.sharedMaterial != null)
            building.factionBannerRenderer.sharedMaterial.color = ownerColor;
        if (building.label != null)
            building.label.color = ownerColor;
    }

    private int CountOwned(int team, HexBuildingType type)
    {
        int count = 0;
        foreach (BuildingState building in buildings)
            if (building.ownerTeam == team && building.type == type) count++;
        return count;
    }

    public int GetMoney(int team) => EnsureWallet(team).money;
    public int GetLabourAvailable(int team)
    {
        TeamWallet wallet = EnsureWallet(team);
        return Mathf.Max(0, wallet.labour - wallet.reservedLabour);
    }
    public int GetIronCapacity(int team) => CountOwned(team, HexBuildingType.IronMine);
    public int GetAluminiumCapacity(int team) => CountOwned(team, HexBuildingType.AluminiumMine);
    public int GetIronAvailable(int team) => Mathf.Max(0, GetIronCapacity(team) - EnsureWallet(team).reservedIron);
    public int GetAluminiumAvailable(int team) => Mathf.Max(0, GetAluminiumCapacity(team) - EnsureWallet(team).reservedAluminium);

    public string GetResourceSummary(int team)
    {
        TeamWallet wallet = EnsureWallet(team);
        return $"金钱：{wallet.money}　劳动力：{GetLabourAvailable(team)}　铁矿：{GetIronAvailable(team)}/{GetIronCapacity(team)}　铝矿：{GetAluminiumAvailable(team)}/{GetAluminiumCapacity(team)}";
    }

    public void AdvanceAP(float oldAP, float newAP, HexUnitDemo driver)
    {
        if (!initialized || newAP <= oldAP) return;
        int start = Mathf.FloorToInt(oldAP + 0.001f) + 1;
        int end = Mathf.FloorToInt(newAP + 0.001f);
        for (int ap = start; ap <= end; ap++)
        {
            foreach (BuildingState building in buildings)
            {
                if (building.ownerTeam < 0) continue;

                int income = GetIncomePerAP(building.type);
                if (income > 0) EnsureWallet(building.ownerTeam).money += income;

                int interval = GetRepairInterval(building.type);
                if (interval > 0 && ap % interval == 0 && driver != null)
                    driver.ApplyBuildingRestorationAt(building.q, building.r, building.ownerTeam, building.type);
            }
            TickProduction(driver);
        }
    }

    private int GetIncomePerAP(HexBuildingType type)
    {
        switch (type)
        {
            case HexBuildingType.Headquarters:
            case HexBuildingType.Village: return 1;
            case HexBuildingType.LargeCity: return 3;
            case HexBuildingType.SmallCity: return 2;
            default: return 0;
        }
    }

    private int GetRepairInterval(HexBuildingType type)
    {
        switch (type)
        {
            case HexBuildingType.Headquarters: return 20;
            case HexBuildingType.LargeCity:
            case HexBuildingType.SmallCity:
            case HexBuildingType.Village: return 40;
            case HexBuildingType.Arsenal:
            case HexBuildingType.TankFactory:
            case HexBuildingType.ArtilleryFactory:
            case HexBuildingType.Shipyard:
            case HexBuildingType.Airport: return 10;
            default: return 0;
        }
    }

    public void DrawTileControls(int viewingTeam, int q, int r, HexUnitDemo driver)
    {
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building))
            return;

        GUILayout.Space(6);
        GUILayout.Label("建筑");
        GUILayout.Label($"{ShortName(building.type)}　归属：{TeamName(building.ownerTeam)}");
        int income = GetIncomePerAP(building.type);
        if (income > 0) GUILayout.Label($"收入：每 AP +{income} 金钱");
        if (driver != null && building.ownerTeam == 0 && IsActionQueueBuilding(building.type))
        {
            if (GUILayout.Button("选择建筑行动节点"))
                driver.SelectBuildingAt(q, r);
        }
        if (building.type == HexBuildingType.Headquarters)
            GUILayout.Label("驻扎恢复：每 20 AP +10 HP、+1 弹药、+2 燃料；占领时 +2 劳动力");
        else if (building.type == HexBuildingType.LargeCity || building.type == HexBuildingType.SmallCity)
            GUILayout.Label("驻扎恢复：每 40 AP +1 弹药、+2 燃料；占领时 +1 劳动力");
        else if (building.type == HexBuildingType.Village)
            GUILayout.Label("驻扎恢复：每 40 AP +1 燃料；占领时 +1 劳动力");
        else if (GetRepairInterval(building.type) > 0)
            GUILayout.Label("对应类型部队：每 10 AP +10 HP、+1 弹药、+2 燃料");
        else if (building.type == HexBuildingType.IronMine)
            GUILayout.Label($"铁矿容量：{GetIronCapacity(viewingTeam)}；资源在制造时临时占用");
        else if (building.type == HexBuildingType.AluminiumMine)
            GUILayout.Label($"铝矿容量：{GetAluminiumCapacity(viewingTeam)}；资源在制造时临时占用");

        if (!IsFactory(building.type)) return;

        int unitTeam = -1;
        string unitName = string.Empty;
        string unitCategory = string.Empty;
        bool occupied = driver != null &&
            driver.TryGetUnitAtTile(q, r, out unitTeam, out unitName, out unitCategory);
        if (occupied)
        {
            GUILayout.Label($"驻扎单位：{unitName}（{TeamName(unitTeam)}）");
            if (unitTeam != building.ownerTeam)
                GUILayout.Label("建筑归属与驻扎单位阵营不一致，不能在此建筑补给。");
            else if (!FactoryCategoryMatches(building.type, unitCategory))
                GUILayout.Label("驻扎单位兵种不匹配；此建筑不会为该单位提供自动补给。");
            else
                GUILayout.Label("自动补给已启用：每 10 AP 恢复 10 HP、补充 1 发弹药与 2 点燃料（受单位容量限制）。");
            return; // A unit occupies the tile: production choices are intentionally hidden.
        }

        if (viewingTeam != 0 || building.ownerTeam != viewingTeam)
        {
            GUILayout.Label("此生产建筑地块为空；只有控制该建筑的我方才能生产。");
            return;
        }

        GUILayout.Space(4);
        if (building.activeOrder != null && building.activeOrder.profile != null)
        {
            ProductionOrder order = building.activeOrder;
            bool shortage = HasResourceShortage(order.team);
            float pct = Mathf.Clamp01(order.progress / Mathf.Max(1, order.profile.productionAP));
            GUILayout.Label($"当前生产：{order.profile.displayName}　{pct * 100f:0}%");
            GUILayout.Label($"进度：{order.progress:0.0}/{order.profile.productionAP} AP　{(shortage ? "资源不足，速度 -50%" : "正常速度")}");
        }
        else
        {
            GUILayout.Label("生产命令需等待该工厂的行动节点就绪后，在建筑行动菜单中选择单位。");
        }
    }

    private bool FactoryCategoryMatches(HexBuildingType type, string category)
    {
        switch (type)
        {
            case HexBuildingType.Arsenal: return category == "infantry";
            case HexBuildingType.TankFactory: return category == "ground_vehicle";
            case HexBuildingType.ArtilleryFactory: return category == "artillery";
            case HexBuildingType.Shipyard: return category == "naval";
            case HexBuildingType.Airport: return category == "air";
            default: return false;
        }
    }

    private string TeamName(int team)
    {
        switch (team)
        {
            case -1: return "无主";
            case 0: return "我方";
            case 1: return "敌方";
            case 2: return "黑方";
            case 3: return "绿方";
            case 4: return "黄方";
            case 5: return "紫方";
            default: return "阵营 " + team;
        }
    }

    private bool IsFactory(HexBuildingType type)
    {
        return type == HexBuildingType.Arsenal || type == HexBuildingType.TankFactory ||
               type == HexBuildingType.ArtilleryFactory || type == HexBuildingType.Shipyard ||
               type == HexBuildingType.Airport;
    }

    private bool ProfileAllows(UnitProductionProfile profile, HexBuildingType type)
    {
        if (profile.allowedBuildings == null || profile.allowedBuildings.Length == 0) return false;
        foreach (HexBuildingType allowed in profile.allowedBuildings)
            if (allowed == type) return true;
        return false;
    }

    private bool CanStartProduction(BuildingState building, int team, UnitProductionProfile profile, out string reason, HexUnitDemo driver)
    {
        reason = string.Empty;
        if (building.ownerTeam != team) { reason = "没有控制该工厂。"; return false; }
        if (building.activeOrder != null) { reason = "该工厂正在生产其他单位。"; return false; }
        if (profile == null || string.IsNullOrWhiteSpace(profile.unitId)) { reason = "单位 ID 未配置。"; return false; }
        if (driver == null || !driver.HasUnitProfile(profile.unitId)) { reason = "单位数据库中找不到该单位 ID。"; return false; }
        TeamWallet wallet = EnsureWallet(team);
        float costMultiplier = team == PlayerTeamId && driver != null ? driver.GetSelectedSkinProductionCostMultiplier() : 1f;
        int effectiveMoneyCost = Mathf.CeilToInt(profile.moneyCost * Mathf.Max(0.01f, costMultiplier));
        if (wallet.money < effectiveMoneyCost) { reason = "当前皮肤下的制造费用不足。"; return false; }
        if (GetLabourAvailable(team) < profile.labourRequired) { reason = "劳动力不足。"; return false; }
        if (GetIronAvailable(team) < profile.ironRequired) { reason = "铁矿不足。"; return false; }
        if (GetAluminiumAvailable(team) < profile.aluminiumRequired) { reason = "铝矿不足。"; return false; }
        return true;
    }

    private bool StartProduction(BuildingState building, int team, UnitProductionProfile profile, HexUnitDemo driver, out string result)
    {
        if (!CanStartProduction(building, team, profile, out result, driver)) return false;
        TeamWallet wallet = EnsureWallet(team);
        float costMultiplier = team == PlayerTeamId && driver != null ? driver.GetSelectedSkinProductionCostMultiplier() : 1f;
        int actualCost = Mathf.CeilToInt(profile.moneyCost * Mathf.Max(0.01f, costMultiplier));
        if (wallet.money < actualCost) { result = "当前皮肤下的制造费用不足。"; return false; }

        wallet.money -= actualCost;
        wallet.reservedLabour += profile.labourRequired;
        wallet.reservedIron += profile.ironRequired;
        wallet.reservedAluminium += profile.aluminiumRequired;
        building.activeOrder = new ProductionOrder
        {
            profile = profile,
            team = team,
            progress = 0f,
            appliedMoneyCost = actualCost
        };
        result = $"已开始制造 {profile.displayName}。";
        return true;
    }

    private bool HasResourceShortage(int team)
    {
        TeamWallet wallet = EnsureWallet(team);
        return wallet.reservedLabour > wallet.labour ||
               wallet.reservedIron > GetIronCapacity(team) ||
               wallet.reservedAluminium > GetAluminiumCapacity(team);
    }

    private void ReleaseProductionReservation(ProductionOrder order)
    {
        if (order == null || order.profile == null) return;
        TeamWallet wallet = EnsureWallet(order.team);
        wallet.reservedLabour = Mathf.Max(0, wallet.reservedLabour - order.profile.labourRequired);
        wallet.reservedIron = Mathf.Max(0, wallet.reservedIron - order.profile.ironRequired);
        wallet.reservedAluminium = Mathf.Max(0, wallet.reservedAluminium - order.profile.aluminiumRequired);
    }

    private void TickProduction(HexUnitDemo driver)
    {
        foreach (BuildingState building in buildings)
        {
            ProductionOrder order = building.activeOrder;
            if (order == null) continue;
            // Capturing a factory pauses the previous owner's active order; it does not transfer the unit.
            if (building.ownerTeam != order.team) continue;
            if (HasResourceShortage(order.team))
                order.progress += 0.5f;
            else
                order.progress += 1f;

            if (order.progress + 0.001f < Mathf.Max(1, order.profile.productionAP))
                continue;

            if (driver == null || !driver.TrySpawnProducedUnit(order.profile.unitId, order.team, building.q, building.r))
                continue; // Keep the completed order and resource reservation until a spawn tile is free.

            ReleaseProductionReservation(order);
            building.activeOrder = null;
        }
    }

    public static bool IsActionQueueBuilding(HexBuildingType type)
    {
        return type == HexBuildingType.Headquarters ||
               type == HexBuildingType.Arsenal ||
               type == HexBuildingType.TankFactory ||
               type == HexBuildingType.ArtilleryFactory ||
               type == HexBuildingType.Shipyard ||
               type == HexBuildingType.Airport;
    }

    public Vector2Int[] GetQueuedBuildingCoordinates()
    {
        var result = new List<Vector2Int>();
        foreach (BuildingState building in buildings)
            if (building != null && building.ownerTeam >= 0 && IsActionQueueBuilding(building.type))
                result.Add(new Vector2Int(building.q, building.r));
        return result.ToArray();
    }

    public bool IsBuildingReady(int q, int r, int team, float ap)
    {
        return buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) &&
               building.ownerTeam == team && IsActionQueueBuilding(building.type) &&
               building.nextReadyTime <= ap + 0.001f &&
               Mathf.Abs(building.zeroCostActedAtTime - ap) > 0.001f;
    }

    public bool HasReadyBuilding(int team, float ap)
    {
        foreach (BuildingState building in buildings)
            if (building != null && building.ownerTeam == team &&
                IsActionQueueBuilding(building.type) &&
                building.nextReadyTime <= ap + 0.001f &&
                Mathf.Abs(building.zeroCostActedAtTime - ap) > 0.001f)
                return true;
        return false;
    }

    public bool IsBuildingActionNodeOccupied(float candidate)
    {
        foreach (BuildingState building in buildings)
            if (building != null && building.ownerTeam >= 0 &&
                IsActionQueueBuilding(building.type) &&
                Mathf.Abs(building.nextReadyTime - candidate) < 0.001f)
                return true;
        return false;
    }

    public void SetBuildingNextReadyTime(int q, int r, float ap)
    {
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) ||
            building.ownerTeam < 0 || !IsActionQueueBuilding(building.type)) return;
        building.nextReadyTime = ap;
        building.zeroCostActedAtTime = -1f;
    }

    public float ReserveBuildingNextActionNode(int q, int r, float proposedTime, HexUnitDemo driver)
    {
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) ||
            building.ownerTeam < 0 || !IsActionQueueBuilding(building.type)) return proposedTime;

        int candidate = Mathf.CeilToInt(proposedTime - 0.001f);
        int currentNode = Mathf.FloorToInt((driver != null ? driver.CurrentBattleAP : 0f) + 0.001f);
        if (candidate <= currentNode) candidate = currentNode + 1;

        bool occupied;
        do
        {
            occupied = driver != null && driver.IsUnitActionNodeOccupied(candidate);
            if (!occupied)
            {
                foreach (BuildingState other in buildings)
                {
                    if (other == null || other == building || other.ownerTeam < 0 ||
                        !IsActionQueueBuilding(other.type)) continue;
                    if (Mathf.Abs(other.nextReadyTime - candidate) < 0.001f)
                    {
                        occupied = true;
                        break;
                    }
                }
            }
            if (occupied) candidate++;
        } while (occupied);

        building.nextReadyTime = candidate;
        building.zeroCostActedAtTime = -1f;
        return building.nextReadyTime;
    }

    public bool TryGetBuildingTypeAt(int q, int r, out HexBuildingType type)
    {
        if (buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building))
        {
            type = building.type;
            return true;
        }
        type = default;
        return false;
    }

    public bool HasActiveProductionAt(int q, int r, out string unitName, out float progress, out float required)
    {
        if (buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) &&
            building.activeOrder != null && building.activeOrder.profile != null)
        {
            unitName = building.activeOrder.profile.displayName;
            progress = building.activeOrder.progress;
            required = Mathf.Max(1, building.activeOrder.profile.productionAP);
            return true;
        }
        unitName = string.Empty;
        progress = required = 0f;
        return false;
    }

    public bool StartProductionAt(int q, int r, int team, string unitId,
        HexUnitDemo driver, out int actionCost, out string result)
    {
        actionCost = 0;
        result = string.Empty;
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) ||
            !IsFactory(building.type))
        {
            result = "该建筑不能生产单位。";
            return false;
        }
        if (building.ownerTeam != team)
        {
            result = "没有控制该工厂。";
            return false;
        }
        int occupiedTeam;
        string ignoredName, ignoredCategory;
        if (driver != null && driver.TryGetUnitAtTile(q, r, out occupiedTeam, out ignoredName, out ignoredCategory))
        {
            result = "工厂地块被单位占用，暂时不能开始生产。";
            return false;
        }

        UnitProductionProfile selected = null;
        if (productionProfiles != null)
            foreach (UnitProductionProfile profile in productionProfiles)
                if (profile != null && profile.unitId == unitId && ProfileAllows(profile, building.type))
                { selected = profile; break; }

        if (selected == null)
        {
            result = "该工厂没有配置此单位的生产方案。";
            return false;
        }
        if (!CanStartProduction(building, team, selected, out result, driver)) return false;
        if (!StartProduction(building, team, selected, driver, out result)) return false;
        actionCost = Mathf.Max(1, selected.productionAP);
        return true;
    }

    public void DrawProductionActionChoices(int viewingTeam, int q, int r, HexUnitDemo driver, bool canAct)
    {
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building) ||
            !IsFactory(building.type)) return;

        int occupiedTeam;
        string occupiedName, occupiedCategory;
        bool occupied = driver != null &&
            driver.TryGetUnitAtTile(q, r, out occupiedTeam, out occupiedName, out occupiedCategory);
        if (occupied)
        {
            GUILayout.Label("驻扎单位：" + occupiedName + "；工厂被占用，不能发起新生产。");
            return;
        }
        if (building.activeOrder != null && building.activeOrder.profile != null)
        {
            ProductionOrder order = building.activeOrder;
            GUILayout.Label($"当前订单：{order.profile.displayName}　{100f * Mathf.Clamp01(order.progress / Mathf.Max(1, order.profile.productionAP)):0}%");
            GUILayout.Label("订单完成前该工厂不能开始另一个生产命令。");
            return;
        }
        if (viewingTeam != 0 || building.ownerTeam != viewingTeam)
        {
            GUILayout.Label("仅建筑控制方可以生产。");
            return;
        }

        GUILayout.Label("生产单位");
        bool oldEnabled = GUI.enabled;
        if (productionProfiles != null)
        {
            foreach (UnitProductionProfile profile in productionProfiles)
            {
                if (profile == null || !ProfileAllows(profile, building.type)) continue;
                bool canStart = CanStartProduction(building, viewingTeam, profile, out string reason, driver);
                GUI.enabled = oldEnabled && canAct && canStart;
                if (GUILayout.Button($"生产：{profile.displayName}（{profile.productionAP} AP）"))
                {
                    if (driver != null) driver.TryStartBuildingProductionFromMenu(q, r, profile.unitId);
                }
                if (!string.IsNullOrEmpty(reason)) GUILayout.Label(reason);
            }
        }
        GUI.enabled = oldEnabled;
    }

    public string GetDescriptionAt(int q, int r)
    {
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building)) return string.Empty;
        return $"{ShortName(building.type)}（{TeamName(building.ownerTeam)}）";
    }
}
