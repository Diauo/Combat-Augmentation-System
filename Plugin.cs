using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using CombatAugmentationSystem.Client.Core;
using CombatAugmentationSystem.Client.Patches;

namespace CombatAugmentationSystem.Client;

/// <summary>
/// Combat Augmentation System（Tushonka-800）— 战术增强系统（客户端，SPT 4.1.6 线）。
/// 技能：①扫描仪（屏幕扫描框 + 逐目标成像 + 穿墙剪影标记）②立场盾（弹道实体拦截 + 单向穿透 + 耐久）
/// ③超频 COMBAT OVERDRIVE（绝境战斗增益包）。
/// 资源机制：技能消耗能量（食物回填）；技能冷却消耗水分，冷却中按技能键可切换加速。
/// 交互：按住技能键弹轮盘（选择）→ 单击技能键触发选中技能；技能冷却中单击 = 切换加速冷却。
/// Target: SPT 4.1.6 / hollowed reference
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.rz99.combat-augmentation-system";
    public const string PluginName = "Combat Augmentation System";
    public const string PluginVersion = "1.0.0";

    internal static Plugin Instance = null!;

    internal static ManualLogSource Log = null!;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        RzConfig.Bind(Config);
        ShieldLayers.Setup();

        try
        {
            new WheelCommandBlockPatch().Enable();
            new WheelAxesBlockPatch().Enable();
            Log.LogInfo($"[CAS] {PluginVersion} loaded (wheel input block enabled)");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"[CAS] Failed to enable input-block patches: {ex}");
        }

        try
        {
            new GameStartPatch().Enable();
            Log.LogInfo("[CAS] GameStart patch enabled");
        }
        catch (System.Exception ex)
        {
            // 4.1 命名差异时静默降级：轮询兜底（Update）会负责挂载 RaidManager
            Log.LogInfo($"[CAS] GameStart patch unavailable, poll fallback will attach: {ex.Message}");
        }

        try
        {
            new ShieldDamagePatch().Enable();
            new ShieldPassThroughPatch().Enable();
            new ShieldPassThroughStaticPatch().Enable();
            new ShieldCheckCollisionPatch().Enable();
            Log.LogInfo("[CAS] shield patches enabled");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"[CAS] Failed to enable shield patches: {ex}");
        }
    }

    /// <summary>兜底：OnGameStarted 补丁不可用时，轮询 GameWorld 并把 RaidManager 挂进战局。</summary>
    private void Update()
    {
        try
        {
            if (RaidManager.Instance != null)
                return;
            if (!Singleton<GameWorld>.Instantiated)
                return;

            var world = Singleton<GameWorld>.Instance;
            if (world == null || world.gameObject == null)
                return;
            if (world.gameObject.GetComponent<RaidManager>() != null)
                return;

            world.gameObject.AddComponent<RaidManager>();
            Log.LogInfo("[CAS] RaidManager attached (poll fallback)");
        }
        catch
        {
            // never throw from the polling fallback
        }
    }

    /// <summary>Debug-gated logging (F12 → 0. General).</summary>
    internal static void Dbg(string message)
    {
        if (RzConfig.DebugLog.Value)
            Log.LogInfo("[CAS][dbg] " + message);
    }
}
