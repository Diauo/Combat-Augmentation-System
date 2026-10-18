using BepInEx.Configuration;
using UnityEngine;

namespace RZSkills.Client;

/// <summary>
/// Combat Augmentation System 全部 BepInEx 配置项（F12 菜单）。
/// 布局规范：节名 "序号. English | 中文"，条目 "English (unit) | 中文（单位）"，描述"中文在前、英文在后"。
/// 所有条目直接绑定；F12 修改即时生效，文件由 BepInEx 自动保存。
/// </summary>
internal static class RzConfig
{
    // ── 节名（当前版本） ────────────────────────────────────
    private const string SecGeneral = "1. General | 通用";
    private const string SecInput = "2. Wheel & Input | 轮盘与输入";
    private const string SecResources = "3. Resources | 资源";
    private const string SecScan = "4. Scan | 扫描";
    private const string SecShield = "5. Shield | 立场盾";
    private const string SecBoost = "6. Overdrive | 超频";

    internal static void Bind(ConfigFile config)
    {
        // ── 通用 ─────────────────────────────────────────────
        DebugLog = config.Bind(SecGeneral,
            "Debug Logging | 调试日志",
            false,
            "输出技能细节日志（扫描目标数/能量/冷却/轮盘开关等）。\n" +
            "Verbose skill logging for troubleshooting.");

        // ── 轮盘 & 输入 ──────────────────────────────────────
        SkillKey = config.Bind(SecInput,
            "Skill Key | 技能键",
            KeyCode.V,
            "单击 = 触发当前选中的技能；按住 = 弹出选择轮盘（松开或点击 = 确认选择，不触发）。\n" +
            "Tap to cast the selected skill; hold to open the selection wheel (release/click confirms the selection only).\n" +
            "注意：选 CapsLock 时插件会拦截它的大写切换（且该键常被游戏占作他用，推荐 V）。");

        WheelHoldMs = config.Bind(SecInput,
            "Wheel Hold Threshold (ms) | 长按判定阈值",
            180,
            new ConfigDescription(
                "按住技能键超过该时长弹出轮盘；短于该时长视为「单击」（触发技能）。\n" +
                "Hold longer than this (ms) to open the wheel; shorter counts as a tap (cast).",
                new AcceptableValueRange<int>(80, 600)));

        SwallowCapsToggle = config.Bind(SecInput,
            "Swallow CapsLock Toggle | 拦截大写切换",
            true,
            "仅当技能键 = CapsLock 时生效：在游戏前台拦截其大写锁定切换。\n" +
            "Only applies when the skill key is CapsLock: swallows its toggle while the game is focused.");

        // ── 扫描仪 ───────────────────────────────────────────
        ScanBoxSize = config.Bind(SecScan,
            "Scan Box Size (%H) | 扫描框大小（屏高%）",
            45f,
            new ConfigDescription("扫描框高度（屏幕高度的百分比），宽度 = 高度 × 宽高比。\nScan box height as a percentage of screen height; width = height x aspect ratio.",
                new AcceptableValueRange<float>(10f, 90f)));

        ScanBoxAspect = config.Bind(SecScan,
            "Scan Box Aspect | 扫描框宽高比",
            16f / 9f,
            new ConfigDescription("扫描框宽高比（16:9 ≈ 1.78，正方形 = 1.0）。\nAspect ratio of the scan box (16:9 ≈ 1.78, square = 1.0).",
                new AcceptableValueRange<float>(0.5f, 2.5f)));

        ScanBaseTime = config.Bind(SecScan,
            "Base Scan Time (s) | 基准扫描时间（秒）",
            1.5f,
            new ConfigDescription("近距离目标的基准成像时间。\nBaseline time-to-image at close range.",
                new AcceptableValueRange<float>(0.5f, 20f)));

        ScanMaxTime = config.Bind(SecScan,
            "Max Scan Time (s) | 最长扫描时间（秒）",
            10f,
            new ConfigDescription("远距离目标的成像时间上限。\nCap for the time-to-image at long range.",
                new AcceptableValueRange<float>(1f, 60f)));

        ScanFarDistance = config.Bind(SecScan,
            "Max-Time Distance (m) | 最长耗时距离（米）",
            300f,
            new ConfigDescription("达到最长扫描时间的距离（此后不再增加）。\nDistance at which the max scan time is reached.",
                new AcceptableValueRange<float>(30f, 1000f)));

        ScanMaxVisibleDistance = config.Bind(SecScan,
            "Max Visible Distance (m) | 视线通畅最大距离（米）",
            600f,
            new ConfigDescription("视线通畅（无遮挡）目标的最大可扫描距离。\nMax scan distance for targets with clear line of sight.",
                new AcceptableValueRange<float>(100f, 2000f)));

        ScanMaxOccludedDistance = config.Bind(SecScan,
            "Max Occluded Distance (m) | 被遮挡最大距离（米）",
            50f,
            new ConfigDescription("被墙/地形等遮挡的目标的最大可扫描距离（超出即无法扫描）。\nMax scan distance for occluded (behind walls/terrain) targets.",
                new AcceptableValueRange<float>(10f, 200f)));

        ScanOccludedTimePenalty = config.Bind(SecScan,
            "Occluded Time Penalty (%) | 遮挡扫描时间加成（%）",
            25f,
            new ConfigDescription("被遮挡目标的扫描时间加成（25 = +25%）。\nScan-time penalty for occluded targets (25 = +25%).",
                new AcceptableValueRange<float>(0f, 100f)));

        ScanProcesses = config.Bind(SecScan,
            "Scan Processes | 扫描进程数（并行）",
            1,
            new ConfigDescription("同时扫描的目标数：1 = 排队逐个扫描；调大可同时扫描多个。\nSimultaneous scan slots: 1 = one at a time (queue); higher = parallel scans.",
                new AcceptableValueRange<int>(1, 6)));

        ScanSwitchMargin = config.Bind(SecScan,
            "Switch Hysteresis Margin | 换标滞回系数",
            0.7f,
            new ConfigDescription(
                "抢占换标阈值：挑战者的评分须低于当前目标评分的该比例才会切换（越小越难切、越稳定；1.0 = 只要更优就切）。\n" +
                "Preemption hysteresis: a challenger must score below this fraction of the current target to steal the slot (lower = stickier).",
                new AcceptableValueRange<float>(0.3f, 1f)));

        ScanDistanceWeight = config.Bind(SecScan,
            "Distance Weight (deg/100m) | 距离权重（度/百米）",
            3f,
            new ConfigDescription(
                "换标评分中：目标距离每 100 米折算的等效瞄准偏差度数（越大越优先近处目标）。\n" +
                "Switch score: equivalent aim-offset degrees per 100 m of distance (higher = prefer closer targets).",
                new AcceptableValueRange<float>(0f, 10f)));

        ScanMarkDuration = config.Bind(SecScan,
            "Mark Duration (s) | 标记持续（秒）",
            15f,
            new ConfigDescription("离开扫描框后的标记存留时间（在框内不倒数）。\nMark lifetime after leaving the box (countdown is frozen while inside).",
                new AcceptableValueRange<float>(1f, 120f)));

        ScanEnergyDrain = config.Bind(SecScan,
            "Energy Drain (per s) | 能量消耗（每秒）",
            0.5f,
            new ConfigDescription("扫描运行期间每秒消耗的能量；能量见底自动关闭。\nEnergy drained per second while scanning; auto-off at zero.",
                new AcceptableValueRange<float>(0f, 5f)));

        ScanMinEnergy = config.Bind(SecScan,
            "Min Energy to Activate | 激活门槛",
            5f,
            new ConfigDescription("开启扫描仪所需的最低能量。\nMinimum energy required to turn the scanner on.",
                new AcceptableValueRange<float>(0f, 50f)));

        ScanOffCooldown = config.Bind(SecScan,
            "Off Cooldown (s) | 关闭后冷却（秒）",
            15f,
            new ConfigDescription("关闭扫描仪后的冷却时间（防连点）。\nCooldown after switching the scanner off.",
                new AcceptableValueRange<float>(0f, 60f)));

        ScanShowDistance = config.Bind(SecScan,
            "Show Distance | 显示距离",
            true,
            "倒三角上方显示距离。\nShow the distance above the marker triangle.");

        ChamsOpacity = config.Bind(SecScan,
            "Silhouette Opacity | 剪影透明度",
            0.65f,
            new ConfigDescription("穿墙剪影的不透明度。\nThrough-wall silhouette opacity.",
                new AcceptableValueRange<float>(0.1f, 1f)));

        PmcChamColor = config.Bind(SecScan,
            "PMC Silhouette Color | PMC 剪影颜色",
            new Color(1f, 0.25f, 0.25f),
            "PMC 剪影颜色（RGBA）。\nPMC silhouette colour.");

        ScavChamColor = config.Bind(SecScan,
            "Scav Silhouette Color | Scav 剪影颜色",
            new Color(1f, 0.85f, 0.1f),
            "Scav 剪影颜色（RGBA）。\nScav silhouette colour.");

        BossChamColor = config.Bind(SecScan,
            "Boss Silhouette Color | Boss 剪影颜色",
            new Color(1f, 0.15f, 0.9f),
            "Boss 剪影颜色（RGBA）。\nBoss silhouette colour.");

        // ── 资源 ─────────────────────────────────────────────
        EnergyCostEnabled = config.Bind(SecResources,
            "Energy Cost | 消耗能量",
            true,
            "技能消耗人物能量（食物恢复的能量条）。\nSkills consume character Energy.");

        WaterThreshold = config.Bind(
            SecResources,
            "Water Threshold | 水分阈值",
            10f,
            new ConfigDescription("水分低于等于该值时：无法加速冷却，且冷却耗时翻倍（期间不消耗水分）。\nBelow this threshold: boosting is unavailable and cooldowns take twice as long (no hydration consumed).",
                new AcceptableValueRange<float>(0f, 100f)));

        CooldownWaterDrain = config.Bind(
            SecResources,
            "Cooldown Water Drain (per s) | 冷却水分消耗（每秒）",
            0.2f,
            new ConfigDescription("默认冷却期间，每个冷却中的技能每秒各自消耗的水分（多技能叠加）。\nHydration drained per second per cooling skill during normal cooldown (stacks per skill).",
                new AcceptableValueRange<float>(0f, 5f)));

        BoostWaterDrain = config.Bind(
            SecResources,
            "Boost Water Drain (per s) | 加速水分消耗（每秒）",
            2f,
            new ConfigDescription("按 V 加速期间，每个冷却中的技能每秒各自消耗的水分（多技能叠加）。\nHydration drained per second per cooling skill while boosting with V (stacks per skill).",
                new AcceptableValueRange<float>(0f, 10f)));

        BoostSpeedMult = config.Bind(
            SecResources,
            "Boost Speed Multiplier | 加速倍率",
            3f,
            new ConfigDescription("按 V 加速时的冷却速度倍率（3 = 三倍速）。\nCooldown speed multiplier while boosting (3 = 3x).",
                new AcceptableValueRange<float>(1f, 10f)));

        EnergyLockout = config.Bind(SecResources,
            "Low Energy Lockout | 低能量封锁阈值",
            10f,
            new ConfigDescription("能量低于等于该值时：结束所有技能，且无法再使用技能（进食恢复后解除）。\nWhen Energy <= this value: all skills are ended and disabled until energy recovers.",
                new AcceptableValueRange<float>(0f, 50f)));

        // ── 立场盾 ───────────────────────────────────────────
        ShieldEnergyCost = config.Bind(SecShield,
            "Energy Cost | 能量消耗",
            10f,
            new ConfigDescription("激活立场盾消耗的能量。\nEnergy cost to raise the shield.",
                new AcceptableValueRange<float>(0f, 50f)));

        ShieldCooldown = config.Bind(SecShield,
            "Cooldown (s) | 冷却（秒）",
            30f,
            new ConfigDescription("护盾结束（消散或破碎）后的冷却时间。\nCooldown after the shield ends (expired or broken).",
                new AcceptableValueRange<float>(5f, 180f)));

        ShieldEnergyDrain = config.Bind(SecShield,
            "Energy Drain (per s) | 能量消耗（每秒）",
            0.5f,
            new ConfigDescription("维持护盾每秒消耗的能量；护盾被摧毁、能量耗尽或手动关闭后进入冷却。\nEnergy drained per second to maintain the shield; cooldown starts when broken, depleted or switched off.",
                new AcceptableValueRange<float>(0f, 5f)));

        ShieldDurability = config.Bind(SecShield,
            "Durability | 耐久",
            2000f,
            new ConfigDescription("可吸收的总伤害量；归零即碎盾。\nTotal damage the shield can absorb; breaks at zero.",
                new AcceptableValueRange<float>(10f, 2000f)));

        ShieldPanelSize = config.Bind(SecShield,
            "Panel Size (m) | 面板尺寸（米）",
            1f,
            new ConfigDescription("六角蜂窝盾面的尺寸（米，外接直径）。\nHex honeycomb shield panel size in metres (bounding diameter).",
                new AcceptableValueRange<float>(0.6f, 2.2f)));

        ShieldArmorClass = config.Bind(SecShield,
            "Armor Class | 防护等级",
            2,
            new ConfigDescription(
                "盾面的等效防弹等级（1~6）：高等级下低穿透（肉伤）弹对其耐久损耗大幅降低，穿甲弹仍是主要威胁。\n" +
                "Equivalent ballistic protection class (1-6): low-penetration (flesh) rounds barely scratch the durability; AP rounds remain the threat.",
                new AcceptableValueRange<int>(1, 6)));

        ShieldOpacity = config.Bind(SecShield,
            "Opacity | 不透明度",
            0.05f,
            new ConfigDescription("盾面的整体不透明度（越低越透明，越不挡视线）。\nOverall shield opacity (lower = more transparent).",
                new AcceptableValueRange<float>(0.05f, 0.8f)));

        ShieldBlockExplosions = config.Bind(SecShield,
            "Block Explosions | 拦截爆炸伤害",
            false,
            "开启后爆炸/地雷/炮击伤害也走盾面判定（默认只拦子弹与破片）。\n" +
            "Also block explosions/mines/artillery through the shield (default: bullets & fragments only).");

        // ── 超频（COMBAT OVERDRIVE：绝境最后一搏） ─────────────
        BoostEnergyDrain = config.Bind(SecBoost,
            "Energy Drain (per s) | 能量消耗（每秒）",
            1f,
            new ConfigDescription("生效期间每秒消耗的能量；能量见底自动关闭。\nEnergy drained per second while active; auto-off at zero.",
                new AcceptableValueRange<float>(0f, 5f)));

        BoostCooldown = config.Bind(SecBoost,
            "Cooldown (s) | 冷却（秒）",
            30f,
            new ConfigDescription("关闭后的冷却时间（防无限切换）。\nCooldown after deactivation.",
                new AcceptableValueRange<float>(3f, 60f)));

        BoostMinEnergy = config.Bind(SecBoost,
            "Min Energy to Activate | 激活门槛",
            10f,
            new ConfigDescription("开启所需的最低能量（防 1 秒白开）。\nMinimum energy required to activate.",
                new AcceptableValueRange<float>(0f, 50f)));

        BoostHealthRegen = config.Bind(SecBoost,
            "Health Regen (per s) | 生命回复（每秒）",
            6f,
            new ConfigDescription("每秒回复的生命值（分摊到受伤部位，不救黑肢）。\nHP restored per second (spread across injured parts; destroyed parts are not healed).",
                new AcceptableValueRange<float>(0f, 20f)));

        BoostStaminaRestore = config.Bind(SecBoost,
            "Stamina Restoration Bonus | 耐力恢复加成",
            5f,
            new ConfigDescription("耐力恢复速度加成（与官方针剂同字段）。\nStamina restoration rate bonus (native stim field).",
                new AcceptableValueRange<float>(0f, 30f)));

        BoostStaminaCapacity = config.Bind(SecBoost,
            "Stamina Capacity Bonus | 耐力上限加成",
            15f,
            new ConfigDescription("耐力上限加成点数。\nStamina capacity bonus points.",
                new AcceptableValueRange<float>(0f, 60f)));

        BoostDamageReduction = config.Bind(SecBoost,
            "Damage Reduction (%) | 减伤（%）",
            20f,
            new ConfigDescription("受到伤害的减免百分比。\nIncoming damage reduction percentage.",
                new AcceptableValueRange<float>(0f, 80f)));

        BoostWeightBonus = config.Bind(SecBoost,
            "Weight Limit Bonus (%) | 负重上限加成（%）",
            15f,
            new ConfigDescription("负重上限的百分比加成。\nCarrying weight limit bonus percentage.",
                new AcceptableValueRange<float>(0f, 60f)));

        BoostSkillBonus = config.Bind(SecBoost,
            "Skill Bonus (levels) | 技能加成（级）",
            10,
            new ConfigDescription("临时技能等级加成量（游戏战斗针剂通常在 +20 档）。\nTemporary skill level bonus (game combat stims are around +20).",
                new AcceptableValueRange<int>(0, 30)));

        BoostStrengthExtra = config.Bind(SecBoost,
            "Speed Boost (Strength levels) | 速度加成（力量等级）",
            15,
            new ConfigDescription(
                "额外提升的力量技能等级——原版移动速度（走/跑）由力量等级驱动；同时附带负重/近战/投掷加成。\n" +
                "Extra Strength skill levels — native movement speed (walk & sprint) scales with the Strength skill.",
                new AcceptableValueRange<int>(0, 40)));

        BoostSkillList = config.Bind(SecBoost,
            "Skill List | 技能列表",
            "Endurance,Vitality,StressResistance,RecoilControl,AimMaster,Health,MagDrills,DrawMaster,TroubleShooting,Strength,CovertMovement",
            "生效期间强化的技能列表（英文名，逗号分隔；游戏 ESkillId 名）。\n" +
            "Skills boosted while active (comma-separated ESkillId names).");

    }

    internal static ConfigEntry<bool> DebugLog = null!;
    internal static ConfigEntry<KeyCode> SkillKey = null!;
    internal static ConfigEntry<int> WheelHoldMs = null!;
    internal static ConfigEntry<bool> SwallowCapsToggle = null!;
    internal static ConfigEntry<float> ScanBoxSize = null!;
    internal static ConfigEntry<float> ScanBoxAspect = null!;
    internal static ConfigEntry<float> ScanBaseTime = null!;
    internal static ConfigEntry<float> ScanMaxTime = null!;
    internal static ConfigEntry<float> ScanFarDistance = null!;
    internal static ConfigEntry<float> ScanMaxVisibleDistance = null!;
    internal static ConfigEntry<float> ScanMaxOccludedDistance = null!;
    internal static ConfigEntry<float> ScanOccludedTimePenalty = null!;
    internal static ConfigEntry<int> ScanProcesses = null!;
    internal static ConfigEntry<float> ScanSwitchMargin = null!;
    internal static ConfigEntry<float> ScanDistanceWeight = null!;
    internal static ConfigEntry<float> ScanMarkDuration = null!;
    internal static ConfigEntry<float> ScanEnergyDrain = null!;
    internal static ConfigEntry<float> ScanMinEnergy = null!;
    internal static ConfigEntry<float> ScanOffCooldown = null!;
    internal static ConfigEntry<bool> ScanShowDistance = null!;
    internal static ConfigEntry<float> ChamsOpacity = null!;
    internal static ConfigEntry<Color> PmcChamColor = null!;
    internal static ConfigEntry<Color> ScavChamColor = null!;
    internal static ConfigEntry<Color> BossChamColor = null!;
    internal static ConfigEntry<bool> EnergyCostEnabled = null!;
    internal static ConfigEntry<float> WaterThreshold = null!;
    internal static ConfigEntry<float> CooldownWaterDrain = null!;
    internal static ConfigEntry<float> BoostWaterDrain = null!;
    internal static ConfigEntry<float> BoostSpeedMult = null!;
    internal static ConfigEntry<float> EnergyLockout = null!;
    internal static ConfigEntry<float> ShieldEnergyCost = null!;
    internal static ConfigEntry<float> ShieldCooldown = null!;
    internal static ConfigEntry<float> ShieldEnergyDrain = null!;
    internal static ConfigEntry<float> ShieldDurability = null!;
    internal static ConfigEntry<float> ShieldPanelSize = null!;
    internal static ConfigEntry<int> ShieldArmorClass = null!;
    internal static ConfigEntry<float> ShieldOpacity = null!;
    internal static ConfigEntry<bool> ShieldBlockExplosions = null!;
    internal static ConfigEntry<float> BoostEnergyDrain = null!;
    internal static ConfigEntry<float> BoostCooldown = null!;
    internal static ConfigEntry<float> BoostMinEnergy = null!;
    internal static ConfigEntry<float> BoostHealthRegen = null!;
    internal static ConfigEntry<float> BoostStaminaRestore = null!;
    internal static ConfigEntry<float> BoostStaminaCapacity = null!;
    internal static ConfigEntry<float> BoostDamageReduction = null!;
    internal static ConfigEntry<float> BoostWeightBonus = null!;
    internal static ConfigEntry<int> BoostSkillBonus = null!;
    internal static ConfigEntry<int> BoostStrengthExtra = null!;
    internal static ConfigEntry<string> BoostSkillList = null!;
}
