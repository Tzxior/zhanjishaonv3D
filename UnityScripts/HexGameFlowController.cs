using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Front-end, persistent skin progression, pause/surrender and battle-result UI.
/// Attach beside HexUnitDemo and HexBuildingEconomy; HexUnitDemo also creates it if missing.
/// </summary>
public class HexGameFlowController : MonoBehaviour
{
    public enum ScreenState
    {
        MainMenu, LevelSelect, SkinSelect, UnitCodex, SkinCodex, Settings,
        Battle, Pause, SurrenderConfirm, Result
    }

    [Serializable]
    public class SkinDefinition
    {
        public string id = "base";
        public string displayName = "基础皮肤";
        [TextArea(2, 4)] public string description = "标准外观与标准属性。";
        public bool unlockedByDefault;
        [Min(0.01f)] public float attackMultiplier = 1f;
        [Min(0.01f)] public float maxHPMultiplier = 1f;
        [Min(0.01f)] public float bulletDefenseMultiplier = 1f;
        [Min(0.01f)] public float explosionDefenseMultiplier = 1f;
        [Min(0.01f)] public float movementMultiplier = 1f;
        [Min(0.01f)] public float productionCostMultiplier = 1f;
    }

    [Serializable]
    public class SkinChestPlacement
    {
        public Vector2Int coordinate;
        public string skinId;
        [Min(1)] public int maxHP = 60;
        public string displayName = "皮肤宝箱";
    }

    [Header("References")]
    public HexUnitDemo battle;
    public HexBuildingEconomy buildings;

    [Header("Front end")]
    public bool startInMainMenu = true;
    public SkinDefinition[] skinCatalog =
    {
        new SkinDefinition
        {
            id = "base", displayName = "基础皮肤",
            description = "标准外观与标准属性。", unlockedByDefault = true
        }
    };
    [Tooltip("奖励皮肤需要同时存在于 Skin Catalog，并在此配置对应坐标。")]
    public SkinChestPlacement[] skinChests = new SkinChestPlacement[0];

    private ScreenState screen = ScreenState.MainMenu;
    private ScreenState settingsReturnScreen = ScreenState.MainMenu;
    private readonly HashSet<string> unlockedSkinIds = new HashSet<string>();
    private string selectedSkinId = "base";
    private Vector2 unitScroll;
    private Vector2 skinScroll;
    private float masterVolume = 1f;
    private bool fullscreenEnabled;
    private bool playerWon;
    private string resultReason = "";
    private string notice = "";
    private bool initialized;

    private void Start()
    {
        if (battle == null) battle = GetComponent<HexUnitDemo>();
        if (buildings == null) buildings = GetComponent<HexBuildingEconomy>();
        if (battle == null)
        {
            Debug.LogError("HexGameFlowController requires HexUnitDemo on the same GameObject.");
            enabled = false;
            return;
        }

        EnsureSkinCatalog();
        LoadMeta();
        battle.SkinChestDefeated += HandleSkinChestDefeated;
        battle.SetGameplayPaused(startInMainMenu);
        battle.showBattleHUD = !startInMainMenu;
        screen = startInMainMenu ? ScreenState.MainMenu : ScreenState.Battle;
        initialized = true;
    }

    private void OnDestroy()
    {
        if (battle != null) battle.SkinChestDefeated -= HandleSkinChestDefeated;
    }

    private void EnsureSkinCatalog()
    {
        if (skinCatalog == null || skinCatalog.Length == 0)
        {
            skinCatalog = new[]
            {
                new SkinDefinition { id = "base", displayName = "基础皮肤", description = "标准外观与标准属性。", unlockedByDefault = true }
            };
        }

        bool hasBase = false;
        foreach (SkinDefinition skin in skinCatalog)
            if (skin != null && skin.id == "base") { hasBase = true; break; }
        if (!hasBase)
        {
            var list = new List<SkinDefinition>(skinCatalog);
            list.Insert(0, new SkinDefinition { id = "base", displayName = "基础皮肤", description = "标准外观与标准属性。", unlockedByDefault = true });
            skinCatalog = list.ToArray();
        }
    }

    private void LoadMeta()
    {
        masterVolume = PlayerPrefs.GetFloat("ZhanjiShouNv.MasterVolume", 1f);
        fullscreenEnabled = PlayerPrefs.GetInt("ZhanjiShouNv.Fullscreen", Screen.fullScreen ? 1 : 0) != 0;
        AudioListener.volume = masterVolume;
        Screen.fullScreen = fullscreenEnabled;
        selectedSkinId = PlayerPrefs.GetString("ZhanjiShouNv.SelectedSkin", "base");

        unlockedSkinIds.Clear();
        string stored = PlayerPrefs.GetString("ZhanjiShouNv.UnlockedSkins", "base");
        foreach (string id in stored.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            unlockedSkinIds.Add(id.Trim());
        unlockedSkinIds.Add("base");

        if (GetSkin(selectedSkinId) == null || !IsUnlocked(selectedSkinId))
            selectedSkinId = "base";
        SaveMeta();
    }

    private void SaveMeta()
    {
        PlayerPrefs.SetString("ZhanjiShouNv.SelectedSkin", selectedSkinId);
        var ids = new List<string>(unlockedSkinIds);
        ids.Sort(StringComparer.Ordinal);
        PlayerPrefs.SetString("ZhanjiShouNv.UnlockedSkins", string.Join(",", ids));
        PlayerPrefs.SetFloat("ZhanjiShouNv.MasterVolume", masterVolume);
        PlayerPrefs.SetInt("ZhanjiShouNv.Fullscreen", fullscreenEnabled ? 1 : 0);
        PlayerPrefs.Save();
    }

    private SkinDefinition GetSkin(string id)
    {
        if (skinCatalog == null) return null;
        foreach (SkinDefinition skin in skinCatalog)
            if (skin != null && string.Equals(skin.id, id, StringComparison.Ordinal)) return skin;
        return null;
    }

    private bool IsUnlocked(string id)
    {
        SkinDefinition skin = GetSkin(id);
        return id == "base" || unlockedSkinIds.Contains(id) || (skin != null && skin.unlockedByDefault);
    }

    private void SetSelectedSkin(string id)
    {
        if (!IsUnlocked(id) || GetSkin(id) == null) return;
        selectedSkinId = id;
        SaveMeta();
    }

    private void HandleSkinChestDefeated(string skinId)
    {
        SkinDefinition skin = GetSkin(skinId);
        if (skin == null)
        {
            notice = "宝箱奖励配置有误：皮肤 ID 不存在于 Skin Catalog。";
            return;
        }
        if (IsUnlocked(skinId))
        {
            notice = "已打开皮肤宝箱；对应皮肤此前已经解锁。";
            return;
        }
        unlockedSkinIds.Add(skinId);
        SaveMeta();
        notice = "新皮肤已解锁：" + skin.displayName;
    }

    public void ShowResult(bool won, string reason)
    {
        playerWon = won;
        resultReason = reason;
        screen = ScreenState.Result;
        if (battle != null) battle.SetGameplayPaused(true);
    }

    private void StartTest001()
    {
        SkinDefinition skin = GetSkin(selectedSkinId);
        if (skin == null) skin = GetSkin("base");
        if (skin != null)
        {
            battle.ConfigureSkinModifiers(
                skin.attackMultiplier, skin.maxHPMultiplier,
                skin.bulletDefenseMultiplier, skin.explosionDefenseMultiplier,
                skin.movementMultiplier, skin.productionCostMultiplier);
        }
        battle.ResetForNewMatch();
        if (skinChests != null)
        {
            foreach (SkinChestPlacement chest in skinChests)
            {
                if (chest == null || GetSkin(chest.skinId) == null) continue;
                battle.SpawnSkinRewardChest(chest.coordinate, chest.skinId, chest.displayName, chest.maxHP);
            }
        }
        battle.showBattleHUD = true;
        battle.SetGameplayPaused(false);
        screen = ScreenState.Battle;
        notice = "";
    }

    private void ReturnToMainMenu()
    {
        screen = ScreenState.MainMenu;
        if (battle != null)
        {
            battle.showBattleHUD = false;
            battle.SetGameplayPaused(true);
        }
    }

    private void ResumeBattle()
    {
        screen = ScreenState.Battle;
        if (battle != null) battle.SetGameplayPaused(false);
    }

    public void OpenPauseMenuFromHUD()
    {
        if (screen != ScreenState.Battle) return;
        screen = ScreenState.Pause;
        if (battle != null) battle.SetGameplayPaused(true);
    }

    private void OpenSettings()
    {
        settingsReturnScreen = screen;
        screen = ScreenState.Settings;
        if (battle != null) battle.SetGameplayPaused(true);
    }

    private void Update()
    {
        if (!initialized || battle == null) return;
        bool escape = false;
#if ENABLE_INPUT_SYSTEM
        escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
        escape = Input.GetKeyDown(KeyCode.Escape);
#endif
        if (!escape) return;

        if (screen == ScreenState.Battle) screen = ScreenState.Pause;
        else if (screen == ScreenState.Pause) { ResumeBattle(); return; }
        else if (screen == ScreenState.SurrenderConfirm) { screen = ScreenState.Pause; return; }
        else if (screen == ScreenState.Result) { ReturnToMainMenu(); return; }
        else if (screen == ScreenState.Settings) { screen = settingsReturnScreen; return; }
        else if (screen != ScreenState.MainMenu) { screen = ScreenState.MainMenu; return; }

        if (screen == ScreenState.Pause) battle.SetGameplayPaused(true);
    }

    private void OnGUI()
    {
        if (screen == ScreenState.Battle || screen == ScreenState.Settings)
        {
            if (screen == ScreenState.Settings) DrawPanel();
            return;
        }
        DrawPanel();
    }

    private void DrawPanel()
    {
        float width = Mathf.Min(560f, Screen.width - 28f);
        float height = Mathf.Min(680f, Screen.height - 28f);
        Rect panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        GUI.Box(panel, GUIContent.none);
        GUILayout.BeginArea(new Rect(panel.x + 18f, panel.y + 14f, panel.width - 36f, panel.height - 28f));
        GUILayout.BeginVertical();

        switch (screen)
        {
            case ScreenState.MainMenu: DrawMainMenu(); break;
            case ScreenState.LevelSelect: DrawLevelSelect(); break;
            case ScreenState.SkinSelect: DrawSkinSelect(); break;
            case ScreenState.UnitCodex: DrawUnitCodex(); break;
            case ScreenState.SkinCodex: DrawSkinCodex(); break;
            case ScreenState.Settings: DrawSettings(); break;
            case ScreenState.Pause: DrawPause(); break;
            case ScreenState.SurrenderConfirm: DrawSurrenderConfirm(); break;
            case ScreenState.Result: DrawResult(); break;
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    private GUIStyle TitleStyle()
    {
        return new GUIStyle(GUI.skin.label) { fontSize = 25, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
    }

    private GUIStyle SubtitleStyle()
    {
        return new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = true };
    }

    private string SelectedSkinName()
    {
        SkinDefinition skin = GetSkin(selectedSkinId);
        return skin != null ? skin.displayName : "基础皮肤";
    }

    private void DrawMainMenu()
    {
        GUILayout.Space(12);
        GUILayout.Label("战棋少女 3D", TitleStyle());
        GUILayout.Label("六角格战术原型 · Test001", SubtitleStyle());
        GUILayout.Space(28);
        if (GUILayout.Button("选择关卡", GUILayout.Height(42))) screen = ScreenState.LevelSelect;
        if (GUILayout.Button("查看单位", GUILayout.Height(38))) screen = ScreenState.UnitCodex;
        if (GUILayout.Button("查看皮肤", GUILayout.Height(38))) screen = ScreenState.SkinCodex;
        if (GUILayout.Button("设置", GUILayout.Height(38))) OpenSettings();
        GUILayout.FlexibleSpace();
        GUILayout.Label("当前皮肤：" + SelectedSkinName());
        if (!string.IsNullOrEmpty(notice)) GUILayout.Label(notice);
        if (GUILayout.Button("退出游戏", GUILayout.Height(34))) Application.Quit();
    }

    private void DrawLevelSelect()
    {
        GUILayout.Label("选择关卡", TitleStyle());
        GUILayout.Space(8);
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Test001");
        GUILayout.Label("当前可用测试关卡：六角格战场、建筑占领、资源和生产原型。");
        if (GUILayout.Button("选择 Test001", GUILayout.Height(42))) screen = ScreenState.SkinSelect;
        GUILayout.EndVertical();
        GUI.enabled = false;
        GUILayout.Button("更多关卡（尚未配置）", GUILayout.Height(36));
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("返回主菜单")) screen = ScreenState.MainMenu;
    }

    private void DrawSkinSelect()
    {
        GUILayout.Label("选择本关皮肤", TitleStyle());
        GUILayout.Label("当前选择写入全局存档，其他关卡会继续沿用。");
        skinScroll = GUILayout.BeginScrollView(skinScroll, GUILayout.Height(440f));
        foreach (SkinDefinition skin in skinCatalog)
        {
            if (skin == null) continue;
            bool unlocked = IsUnlocked(skin.id);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(skin.displayName + (selectedSkinId == skin.id ? "（当前选择）" : ""));
            GUILayout.Label(unlocked ? "已解锁" : "未解锁");
            GUILayout.Label(skin.description);
            GUILayout.Label($"攻击 ×{skin.attackMultiplier:0.##}　生命 ×{skin.maxHPMultiplier:0.##}　移动 ×{skin.movementMultiplier:0.##}");
            GUILayout.Label($"子弹防御 ×{skin.bulletDefenseMultiplier:0.##}　爆炸防御 ×{skin.explosionDefenseMultiplier:0.##}　制造费用 ×{skin.productionCostMultiplier:0.##}");
            GUI.enabled = unlocked;
            if (GUILayout.Button(selectedSkinId == skin.id ? "已选择" : "选择该皮肤")) SetSelectedSkin(skin.id);
            GUI.enabled = true;
            GUILayout.EndVertical();
        }
        GUILayout.EndScrollView();
        if (GUILayout.Button("开始 Test001", GUILayout.Height(42))) StartTest001();
        if (GUILayout.Button("返回选关")) screen = ScreenState.LevelSelect;
    }

    private void DrawUnitCodex()
    {
        GUILayout.Label("单位图鉴", TitleStyle());
        string[] entries = battle != null ? battle.GetUnitCodexEntries() : new string[0];
        GUILayout.Label($"已读取单位数据：{entries.Length} 种");
        unitScroll = GUILayout.BeginScrollView(unitScroll, GUILayout.Height(490f));
        foreach (string entry in entries)
            GUILayout.Label(entry, GUI.skin.box);
        GUILayout.EndScrollView();
        if (GUILayout.Button("返回主菜单")) screen = ScreenState.MainMenu;
    }

    private void DrawSkinCodex()
    {
        GUILayout.Label("皮肤图鉴", TitleStyle());
        GUILayout.Label("皮肤解锁会写入本地存档；宝箱的坐标和奖励 ID 可在 Inspector 设置。");
        skinScroll = GUILayout.BeginScrollView(skinScroll, GUILayout.Height(470f));
        foreach (SkinDefinition skin in skinCatalog)
        {
            if (skin == null) continue;
            GUILayout.BeginVertical(GUI.skin.box);
            bool unlocked = IsUnlocked(skin.id);
            GUILayout.Label(skin.displayName + (unlocked ? "（已解锁）" : "（未解锁）"));
            GUILayout.Label(skin.description);
            GUILayout.Label($"攻击 ×{skin.attackMultiplier:0.##}　生命 ×{skin.maxHPMultiplier:0.##}　移动 ×{skin.movementMultiplier:0.##}");
            if (!unlocked) GUILayout.Label("解锁方式：击败地图上绑定该皮肤 ID 的特殊宝箱单位。");
            GUILayout.EndVertical();
        }
        GUILayout.EndScrollView();
        if (GUILayout.Button("选择皮肤")) screen = ScreenState.SkinSelect;
        if (GUILayout.Button("返回主菜单")) screen = ScreenState.MainMenu;
    }

    private void DrawSettings()
    {
        GUILayout.Label("设置", TitleStyle());
        GUILayout.Space(12);
        GUILayout.Label($"主音量：{masterVolume * 100f:0}%");
        masterVolume = GUI.HorizontalSlider(GUILayoutUtility.GetRect(300f, 26f), masterVolume, 0f, 1f);
        AudioListener.volume = masterVolume;
        if (GUILayout.Button(fullscreenEnabled ? "切换为窗口模式" : "切换为全屏模式"))
        {
            fullscreenEnabled = !fullscreenEnabled;
            Screen.fullScreen = fullscreenEnabled;
            SaveMeta();
        }
        GUILayout.Label(fullscreenEnabled ? "当前：全屏" : "当前：窗口模式");
        GUILayout.Label("更多画面与操作设置将在后续完善。");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("保存并返回", GUILayout.Height(38f)))
        {
            SaveMeta();
            screen = settingsReturnScreen;
        }
    }

    private void DrawPause()
    {
        GUILayout.Label("战场菜单", TitleStyle());
        GUILayout.Label("Test001");
        GUILayout.Space(12);
        if (GUILayout.Button("继续游戏", GUILayout.Height(40f))) ResumeBattle();
        if (GUILayout.Button("重新开始本关", GUILayout.Height(38f))) StartTest001();
        if (GUILayout.Button("设置", GUILayout.Height(38f))) OpenSettings();
        if (GUILayout.Button("投降", GUILayout.Height(38f))) screen = ScreenState.SurrenderConfirm;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("返回主菜单", GUILayout.Height(36f))) ReturnToMainMenu();
    }

    private void DrawSurrenderConfirm()
    {
        GUILayout.Label("确认投降？", TitleStyle());
        GUILayout.Label("投降将立即判定本局失败并进入结算。");
        GUILayout.Space(18);
        if (GUILayout.Button("确认投降", GUILayout.Height(42f)))
        {
            battle.RequestSurrender();
            screen = ScreenState.Result;
            playerWon = false;
            resultReason = "玩家选择投降。";
        }
        if (GUILayout.Button("返回战场菜单", GUILayout.Height(36f))) screen = ScreenState.Pause;
    }

    private void DrawResult()
    {
        GUILayout.Space(18);
        GUILayout.Label(playerWon ? "任务胜利" : "任务失败", TitleStyle());
        GUILayout.Label(resultReason, SubtitleStyle());
        GUILayout.Space(16);
        GUILayout.Label("最终战场 AP：" + (battle != null ? battle.CurrentBattleAP.ToString("0") : "—"));
        GUILayout.Label("当前皮肤：" + SelectedSkinName());
        if (!string.IsNullOrEmpty(notice)) GUILayout.Label(notice);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("重玩 Test001", GUILayout.Height(42f))) StartTest001();
        if (GUILayout.Button("返回主菜单", GUILayout.Height(38f))) ReturnToMainMenu();
    }
}
