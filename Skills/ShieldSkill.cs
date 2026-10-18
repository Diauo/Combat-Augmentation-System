using EFT;
using EFT.Ballistics;
using CombatAugmentationSystem.Client.Core;
using CombatAugmentationSystem.Client.Visuals;
using UnityEngine;

namespace CombatAugmentationSystem.Client.Skills;

/// <summary>全局门面：伤害拦截 / 穿透豁免补丁通过它找到当前战局的护盾（无战局时为空）。</summary>
internal static class ShieldGate
{
    internal static ShieldSkill? Active;
}

/// <summary>
/// 「立场护盾」随身盾（v5：能量制 + 1:3 三面结构）：
///  · 激活耗 15 能量（可配）→ 之后每秒 0.5 能量维持（可配）；能量不足自动关闭；再按技能键手动关闭；
///  · 结构 = 主盾（正前）+ 左右 45° 斜翼（防侧向射弹），三片均为弹道实体（子弹停在盾上）；
///  · **被摧毁（耐久归零）→ 进入冷却**；其余结束方式同样进入冷却；
///  · 单向穿透：主人自己的射击豁免（Shot.IsHitIgnored 补丁，三片都识别）；
///  · 兜底判定层：ApplyDamageInfo 拦截对三片逐一做线段求交。
/// </summary>
public sealed class ShieldSkill : SkillBase
{
    private const float FollowDistance = 1.0f;
    private const float FollowDropY = 0.28f;
    private const float DiscMargin = 0.10f;

    private readonly ShieldVisual _visual;

    private float _durabilityLeft;
    private float _durabilityMax = 1f;
    private float _discRadius = 0.6f;

    private IObserverToPlayerBridge? _mainBridge;
    private bool _bridgeResolved;

    public ShieldSkill(RaidManager raid)
        : base(raid, "shield", "立场盾", "Shield")
    {
        _visual = new ShieldVisual();
    }

    protected override float EnergyCostValue => RzConfig.ShieldEnergyCost.Value;

    public override float CooldownTotal => RzConfig.ShieldCooldown.Value;

    protected override float ActiveDuration => 99999f; // 能量制：手动/摧毁/断能结束

    public override string StatusText =>
        IsActive ? Loc.T("生效中", "Active")
        : CooldownRemaining > 0f ? $"{Loc.T("冷却", "Cooldown")} {CooldownRemaining:0}s"
        : Loc.T("就绪", "Ready");

    public override bool TryActivate()
    {
        if (IsActive)
        {
            Plugin.Log.LogInfo("[CAS] shield off (manual)");
            EndNow();
            return true;
        }

        return base.TryActivate();
    }

    protected override bool OnActivate()
    {
        var player = Raid.MainPlayer;
        if (player == null)
            return false;

        var cam = ResolveCamera();
        if (cam == null)
            return false;

        _durabilityMax = Mathf.Max(1f, RzConfig.ShieldDurability.Value);
        _durabilityLeft = _durabilityMax;

        float panelSize = Mathf.Clamp(RzConfig.ShieldPanelSize.Value, 0.4f, 3f);
        _discRadius = panelSize * 0.5f + DiscMargin;

        var flat = cam.transform.forward;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f)
            flat = Vector3.forward;
        flat.Normalize();

        var targetPos = cam.transform.position + flat * FollowDistance;
        targetPos.y -= FollowDropY;

        _visual.Ensure();
        _visual.SetPanelScale(panelSize);
        _visual.SetImpactHandler(OnShieldBulletImpact);
        _visual.SetOwnerCheck(IsOwnerShot);
        _visual.SnapTo(targetPos, Quaternion.LookRotation(flat, Vector3.up));
        _visual.SetVisible(true);
        _visual.SetDurabilityRatio(1f);
        _visual.Tick(0f);

        Plugin.Log.LogInfo(
            $"[CAS] Shield activated: durability={_durabilityMax:0}, size={panelSize:0.0}m, " +
            $"panels={_visual.PanelCount}, drain={RzConfig.ShieldEnergyDrain.Value:0.#}/s");
        return true;
    }

    protected override void OnActiveTick(float now, float deltaTime)
    {
        var player = Raid.MainPlayer;
        if (player == null)
            return;

        // 维持能量消耗 & 能量耗尽自动关闭
        if (RzConfig.EnergyCostEnabled.Value)
        {
            try
            {
                var hc = player.ActiveHealthController;
                if (hc != null)
                {
                    hc.ChangeEnergy(-RzConfig.ShieldEnergyDrain.Value * deltaTime);
                    if (hc.Energy.Current <= 0f)
                    {
                        Plugin.Log.LogInfo("[CAS] shield off: energy depleted");
                        EndNow();
                        return;
                    }
                }
            }
            catch
            {
            }
        }

        var cam = ResolveCamera();
        if (cam == null)
            return;

        var flat = cam.transform.forward;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f)
            return;
        flat.Normalize();

        var targetPos = cam.transform.position + flat * FollowDistance;
        targetPos.y -= FollowDropY;

        // 瞬时跟随（无延迟）
        _visual.SnapTo(targetPos, Quaternion.LookRotation(flat, Vector3.up));

        _visual.SetDurabilityRatio(_durabilityLeft / _durabilityMax);
        _visual.Tick(deltaTime);
    }

    protected override void OnEndActive()
    {
        _visual.SetVisible(false);
        Plugin.Log.LogInfo("[CAS] shield ended — cooldown started");
    }

    /// <summary>单向穿透武装状态：护盾激活且未摧毁（供 CheckCollision 谓词包裹快速门控）。</summary>
    internal bool PassThroughArmed => IsActive && _durabilityLeft > 0f;

    public override void Dispose()
    {
        base.Dispose();
        _visual.Dispose();
    }

    // ── 物理拦截（盾面弹道碰撞体命中回调） ──────────────────

    private void OnShieldBulletImpact(Shot shot, Vector3 point)
    {
        if (!IsActive || _durabilityLeft <= 0f)
            return;

        float damage;
        float penetration;
        try
        {
            damage = Mathf.Max(0.5f, shot.Damage);
            penetration = shot.PenetrationPower;
        }
        catch
        {
            damage = 10f;
            penetration = 20f;
        }

        float loss = ComputeDurabilityLoss(damage, penetration);
        _durabilityLeft -= loss;
        _visual.Flash();

        if (_durabilityLeft <= 0f)
        {
            _durabilityLeft = 0f;
            Plugin.Log.LogInfo($"[CAS] Shield broken (last hit dmg={damage:0.#} pen={penetration:0})");
            EndNow();
        }
        else
        {
            Plugin.Dbg($"shield blocked bullet dmg={damage:0.#} pen={penetration:0} drain={loss:0.#} left={_durabilityLeft:0.#}");
        }
    }

    // ── 兜底拦截（ApplyDamageInfo 补丁入口） ────────────────

    /// <summary>
    /// 伤害拦截入口（由 ShieldDamagePatch 调用）。
    /// 返回 true = 命中任意盾面并已把伤害清零；false = 放行原版逻辑。
    /// </summary>
    internal bool TryAbsorb(Player victim, ref DamageInfo damage)
    {
        if (!IsActive || victim != Raid.MainPlayer)
            return false;

        if (_durabilityLeft <= 0f || damage.Damage <= 0f)
            return false;

        if (!IsBlockable(damage.DamageType))
            return false;

        if (!SegmentHitsShield(damage))
            return false;

        float absorbed = damage.Damage;
        float loss = ComputeDurabilityLoss(absorbed, damage.PenetrationPower);
        _durabilityLeft -= loss;

        // 清零伤害字段（方法体仍会执行：保留原有视听反馈，但不产生实际伤害/出血/耐力惩罚）
        damage.Damage = 0f;
        damage.ArmorDamage = 0f;
        damage.PenetrationPower = 0f;
        damage.HeavyBleedingDelta = 0f;
        damage.LightBleedingDelta = 0f;
        damage.StaminaBurnRate = 0f;
        damage.DidBodyDamage = 0f;
        damage.DidArmorDamage = 0f;
        damage.BleedBlock = true;

        _visual.Flash();

        if (_durabilityLeft <= 0f)
        {
            _durabilityLeft = 0f;
            Plugin.Log.LogInfo($"[CAS] Shield broken (absorbed {absorbed:0.#})");
            EndNow();
        }
        else
        {
            Plugin.Dbg($"shield absorb {absorbed:0.#} dmg, drain {loss:0.#}, left {_durabilityLeft:0.#}");
        }

        return true;
    }

    /// <summary>
    /// 等效防弹等级（1~6）的耐久损耗：
    /// 钝伤分量（12%）恒定，穿透分量按 穿透力 /（12+5×等级） 缩放——低穿透弹几乎无损，穿甲弹全额甚至更多。
    /// </summary>
    private static float ComputeDurabilityLoss(float damage, float penetrationPower)
    {
        int armorClass = Mathf.Clamp(RzConfig.ShieldArmorClass.Value, 1, 6);
        float divisor = 12f + 5f * armorClass; // 2级 ≈ 22
        float penFactor = Mathf.Clamp(penetrationPower / divisor, 0f, 1.15f);
        return damage * 0.12f + damage * 0.85f * penFactor;
    }

    private static bool IsBlockable(EDamageType type)
    {
        const EDamageType core = EDamageType.Bullet | EDamageType.Sniper | EDamageType.GrenadeFragment;
        if ((type & core) != 0)
            return true;

        if (RzConfig.ShieldBlockExplosions.Value)
        {
            const EDamageType boom = EDamageType.Explosion | EDamageType.ThermobaricExplosion
                | EDamageType.Landmine | EDamageType.Artillery | EDamageType.Btr;
            if ((type & boom) != 0)
                return true;
        }

        return false;
    }

    /// <summary>线段（MasterOrigin → HitPoint）与三片盾面圆盘逐一求交。</summary>
    private bool SegmentHitsShield(DamageInfo damage)
    {
        var origin = damage.MasterOrigin;
        var point = damage.HitPoint;

        if (origin.sqrMagnitude < 0.01f)
        {
            // 缺失源点：由弹道方向反推
            var dir = damage.Direction;
            if (dir.sqrMagnitude < 0.01f)
                return true; // 信息不足：默认视作正面来袭（保底拦截）
            origin = point - dir * 50f;
        }

        for (int i = 0; i < _visual.PanelCount; i++)
        {
            _visual.GetPanelPlane(i, out var center, out var normal);
            if (normal.sqrMagnitude < 0.01f)
                continue;

            if (SegmentHitsPlane(origin, point, center, normal))
                return true;
        }

        return false;
    }

    private bool SegmentHitsPlane(Vector3 origin, Vector3 point, Vector3 center, Vector3 normal)
    {
        float d1 = Vector3.Dot(normal, origin - center);
        float d2 = Vector3.Dot(normal, point - center);

        if (d1 * d2 > 0f)
            return false; // 同侧不穿盾

        float t = d1 / (d1 - d2);
        var closest = Vector3.Lerp(origin, point, t);

        var rel = closest - center;
        float dx = Vector3.Dot(rel, Vector3.Cross(Vector3.up, normal)); // 盾面右向
        float dy = rel.y;

        return Mathf.Abs(dx) <= _discRadius && Mathf.Abs(dy) <= _discRadius;
    }

    // ── 单向穿透判定（Shot.IsHitIgnored 补丁入口） ──────────

    /// <summary>命中体是否属于本盾（任意一片）。</summary>
    internal bool IsShieldCollider(Collider? collider)
    {
        if (collider == null || !IsActive)
            return false;

        try
        {
            return _visual.MatchesCollider(collider);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 主人射击判定（多层信号，任一命中即认定）：
    ///  ① 档案 ID 比对；② 桥接引用比对；③ 弹丸起点/朝向几何兜底（枪口位于玩家头部附近且大致同向）。
    /// </summary>
    internal bool IsOwnerShot(Shot shot)
    {
        var main = Raid.MainPlayer;
        if (main == null || shot == null)
            return false;

        try
        {
            // ① 档案 ID
            var profileId = shot.PlayerProfileID;
            if (!string.IsNullOrEmpty(profileId) && profileId == main.ProfileId)
                return true;

            // ② 桥接引用
            var mainBridge = ResolveMainBridge(main);
            if (mainBridge != null && shot.Player != null && ReferenceEquals(shot.Player, mainBridge))
                return true;

            // ③ 几何兜底：弹丸起点贴近玩家头部 且 弹道与玩家朝向同向（自己的枪口）
            var history = shot.PositionHistory;
            if (history != null && history.Count > 0)
            {
                var head = GetHeadPosition(main);
                if ((history[0] - head).sqrMagnitude <= 2.25f) // ≤1.5m
                {
                    var direction = shot.Direction;
                    var forward = main.Transform.forward;
                    if (direction.sqrMagnitude > 0.01f &&
                        Vector3.Dot(direction.normalized, forward) > 0.6f)
                        return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static Vector3 GetHeadPosition(Player player)
    {
        var bones = player.PlayerBones;
        return bones != null
            ? bones.Head.position
            : player.Transform.position + Vector3.up * 1.6f;
    }

    private IObserverToPlayerBridge? ResolveMainBridge(Player main)
    {
        if (!_bridgeResolved)
        {
            var world = Raid.World;
            if (world != null)
            {
                _mainBridge = world.GetEverExistedBridgeByProfileID(main.ProfileId);
                _bridgeResolved = true;
            }
        }

        return _mainBridge;
    }

    /// <summary>静态路径：射击者桥接是否为主玩家。</summary>
    internal bool IsOwnerShooter(IObserverToPlayerBridge? shooter)
    {
        var main = Raid.MainPlayer;
        if (main == null || shooter == null)
            return false;

        try
        {
            var mainBridge = ResolveMainBridge(main);
            return mainBridge != null && ReferenceEquals(shooter, mainBridge);
        }
        catch
        {
            return false;
        }
    }

    private Camera? ResolveCamera()
    {
        return CameraResolver.Resolve();
    }
}
