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
        public ProductionOrder activeOrder;
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
    [Tooltip("Unit costs and manufacturing times were not specified yet. Add entries here; empty profiles are not producible.")]
    public UnitProductionProfile[] productionProfiles = new UnitProductionProfile[0];

    private readonly List<BuildingState> buildings = new List<BuildingState>();
    private readonly Dictionary<Vector2Int, BuildingState> buildingsByCoordinate = new Dictionary<Vector2Int, BuildingState>();
    private readonly Dictionary<int, TeamWallet> wallets = new Dictionary<int, TeamWallet>();
    private HexBattlefieldDemo battlefield;
    private HexUnitDemo unitController;
    private bool initialized;
    private bool resultWasReported;

    public int PlayerTeamId => 0;
    public event Action<int, int, HexBuildingType> HeadquartersCaptured;

    public void InitializeForBattlefield(HexBattlefieldDemo map, HexUnitDemo units)
    {
        if (initialized)
        {
            battlefield = map != null ? map : battlefield;
            unitController = units != null ? units : unitController;
            return;
        }

        battlefield = map;
        unitController = units;
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

    private void OnDestroy()
    {
        foreach (BuildingState building in buildings)
            if (building != null && building.marker != null)
                Destroy(building.marker);
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

    public void ResetForNewBattle(HexUnitDemo driver)
    {
        if (driver != null) unitController = driver;
        foreach (BuildingState building in buildings)
            if (building != null && building.marker != null)
                Destroy(building.marker);
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

    private Vector3 TilePosition(int q, int r)
    {
        float size = battlefield.hexSize;
        return new Vector3(
            1.5f * size * q,
            battlefield.tileThickness * 0.5f + 0.26f,
            Mathf.Sqrt(3f) * size * (r + q * 0.5f));
    }

    private void CreateMarker(BuildingState building)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = $"Building_{building.type}_{building.q}_{building.r}";
        marker.transform.SetParent(transform, false);
        marker.transform.localPosition = TilePosition(building.q, building.r);
        marker.transform.localScale = new Vector3(0.34f, 0.16f, 0.34f);
        Collider collider = marker.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader != null)
        {
            Material material = new Material(shader);
            material.color = OwnerColor(building.ownerTeam);
            marker.GetComponent<Renderer>().sharedMaterial = material;
        }

        GameObject labelObject = new GameObject("BuildingLabel");
        labelObject.transform.SetParent(marker.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 1.35f, 0f);
        labelObject.transform.localScale = Vector3.one * 0.18f;
        TextMesh label = labelObject.AddComponent<TextMesh>();
        label.text = ShortName(building.type);
        label.fontSize = 64;
        label.characterSize = 0.8f;
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.color = OwnerColor(building.ownerTeam);

        building.marker = marker;
        building.label = label;
    }

    private Color OwnerColor(int owner)
    {
        if (owner == 0) return new Color(0.22f, 0.55f, 0.95f);
        if (owner == 1) return new Color(0.88f, 0.27f, 0.23f);
        return new Color(0.72f, 0.72f, 0.68f);
    }

    private string ShortName(HexBuildingType type)
    {
        switch (type)
        {
            case HexBuildingType.Headquarters: return "大本营";
            case HexBuildingType.LargeCity: return "大城市";
            case HexBuildingType.SmallCity: return "小城市";
            case HexBuildingType.Village: return "乡村";
            case HexBuildingType.Arsenal: return "兵工厂";
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
        building.ownerTeam = newOwner;
        if (newOwner >= 0) EnsureWallet(newOwner);
        if (newOwner >= 0) GrantCaptureLabour(newOwner, building.type);

        if ((building.type == HexBuildingType.LargeCity || building.type == HexBuildingType.SmallCity) &&
            battlefield.IsCity(q, r))
            battlefield.CaptureCity(q, r, newOwner);

        RefreshMarker(building);
        message = $"已占领{ShortName(building.type)}。";

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
        if (building.marker != null)
        {
            Renderer renderer = building.marker.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
                renderer.sharedMaterial.color = OwnerColor(building.ownerTeam);
        }
        if (building.label != null)
            building.label.color = OwnerColor(building.ownerTeam);
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
        return wallet.labour - wallet.reservedLabour;
    }
    public int GetIronCapacity(int team) => CountOwned(team, HexBuildingType.IronMine);
    public int GetAluminiumCapacity(int team) => CountOwned(team, HexBuildingType.AluminiumMine);
    public int GetIronAvailable(int team) => GetIronCapacity(team) - EnsureWallet(team).reservedIron;
    public int GetAluminiumAvailable(int team) => GetAluminiumCapacity(team) - EnsureWallet(team).reservedAluminium;

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

        if (viewingTeam != 0 || building.ownerTeam != viewingTeam || !IsFactory(building.type))
            return;

        GUILayout.Space(4);
        GUILayout.Label("生产");
        if (building.activeOrder != null)
        {
            ProductionOrder order = building.activeOrder;
            bool shortage = HasResourceShortage(order.team);
            float pct = Mathf.Clamp01(order.progress / Mathf.Max(1, order.profile.productionAP));
            GUILayout.Label($"{order.profile.displayName}：{pct * 100f:0}%　{(shortage ? "资源不足，速度 -50%" : "正常速度")}");
            GUILayout.Label($"进度：{order.progress:0.0}/{order.profile.productionAP} AP");
        }
        else
        {
            bool any = false;
            if (productionProfiles != null)
            {
                foreach (UnitProductionProfile profile in productionProfiles)
                {
                    if (profile == null || !ProfileAllows(profile, building.type)) continue;
                    any = true;
                    bool canStart = CanStartProduction(building, viewingTeam, profile, out string reason, driver);
                    GUI.enabled = canStart;
                    string label = $"{profile.displayName}（{profile.moneyCost} 金钱 / {profile.productionAP} AP）";
                    if (GUILayout.Button(label))
                    {
                        if (StartProduction(building, viewingTeam, profile, driver, out string result))
                            reason = result;
                        else
                            reason = result;
                    }
                    GUI.enabled = true;
                    if (!string.IsNullOrEmpty(reason)) GUILayout.Label(reason);
                }
            }
            if (!any)
                GUILayout.Label("尚未配置该工厂的单位生产参数；请在 Inspector 的 Production Profiles 中添加。");
        }
    }

    private string TeamName(int team)
    {
        if (team == 0) return "我方";
        if (team == 1) return "敌方";
        return team < 0 ? "无主" : "阵营 " + team;
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
        if (wallet.money < profile.moneyCost) { reason = "金钱不足。"; return false; }
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

            TeamWallet wallet = EnsureWallet(order.team);
            wallet.reservedLabour = Mathf.Max(0, wallet.reservedLabour - order.profile.labourRequired);
            wallet.reservedIron = Mathf.Max(0, wallet.reservedIron - order.profile.ironRequired);
            wallet.reservedAluminium = Mathf.Max(0, wallet.reservedAluminium - order.profile.aluminiumRequired);
            building.activeOrder = null;
        }
    }

    public string GetDescriptionAt(int q, int r)
    {
        if (!buildingsByCoordinate.TryGetValue(new Vector2Int(q, r), out BuildingState building)) return string.Empty;
        return $"{ShortName(building.type)}（{TeamName(building.ownerTeam)}）";
    }
}
