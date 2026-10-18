using EFT.HealthSystem;
using RZSkills.Client.Core;
using UnityEngine;

namespace RZSkills.Client.Skills;

/// <summary>
/// 技能公共底座：能量消耗 / 冷却（含水分加速）/ 状态文本 / 激活与生命周期流程。
/// 子类实现 OnActivate（返回 false = 拒绝且不消耗资源）与可选的生效阶段钩子。
/// </summary>
public abstract class SkillBase : ISkill
{
    private readonly RaidManager _raid;
    private readonly string _nameZh;
    private readonly string _nameEn;

    /// <summary>冷却加速开关：技能冷却中按技能键 = 切换；全技能共享，缺水/无冷却时自动解除。</summary>
    internal static bool CooldownBoostActive;

    protected SkillBase(RaidManager raid, string id, string nameZh, string nameEn)
    {
        _raid = raid;
        Id = id;
        _nameZh = nameZh;
        _nameEn = nameEn;
    }

    public string Id { get; }

    public string DisplayName => Loc.T(_nameZh, _nameEn);

    public bool Ready => !IsActive && CooldownRemaining <= 0f;

    public bool IsActive => ActiveRemaining > 0f;

    public float ActiveRemaining { get; private set; }

    public float CooldownRemaining { get; private set; }

    public bool IsBurning { get; private set; }

    /// <summary>冷却总时长（秒）。</summary>
    public abstract float CooldownTotal { get; }

    public virtual string StatusText
    {
        get
        {
            if (IsActive)
                return $"{Loc.T("生效中", "Active")} {ActiveRemaining:0.0}s";
            if (CooldownRemaining > 0f)
                return $"{Loc.T("冷却", "Cooldown")} {CooldownRemaining:0}s";
            return Loc.T("就绪", "Ready");
        }
    }

    protected RaidManager Raid => _raid;

    /// <summary>单次能量消耗（各技能独立配置）。</summary>
    protected abstract float EnergyCostValue { get; }

    /// <summary>是否有「持续生效」阶段（即时型技能为 false）。</summary>
    protected virtual bool HasActivePhase => true;

    /// <summary>生效阶段时长（秒）。</summary>
    protected virtual float ActiveDuration => 0f;

    public virtual bool TryActivate()
    {
        if (!Ready)
            return false;

        var player = _raid.MainPlayer;
        if (player == null)
            return false;

        ActiveHealthController? health = null;
        try
        {
            health = player.ActiveHealthController;
        }
        catch
        {
        }

        if (RzConfig.EnergyCostEnabled.Value)
        {
            float energy = 0f;
            if (health != null)
            {
                try
                {
                    energy = health.Energy.Current;
                }
                catch
                {
                }
            }

            // 低能量封锁：能量 ≤ 阈值时任何技能都无法使用
            float lockout = RzConfig.EnergyLockout.Value;
            if (health != null && energy <= lockout)
            {
                Plugin.Log.LogInfo($"[RZSkills] {Id} refused: low-energy lockout ({energy:0.#} <= {lockout:0.#})");
                return false;
            }

            float cost = EnergyCostValue;
            if (health == null || energy < cost)
            {
                Plugin.Log.LogInfo($"[RZSkills] {Id} refused: energy insufficient");
                return false;
            }
        }

        if (!OnActivate())
        {
            Plugin.Log.LogInfo($"[RZSkills] {Id} refused: constraint");
            return false;
        }

        if (health != null && RzConfig.EnergyCostEnabled.Value)
        {
            float cost = EnergyCostValue;
            if (cost > 0f)
                health.ChangeEnergy(-cost);
        }

        if (HasActivePhase)
        {
            ActiveRemaining = ActiveDuration;
            OnActiveStart();
        }
        else
        {
            StartCooldown();
        }

        return true;
    }

    /// <summary>外部强制结束（低能量封锁等场景）：若在生效中则进入冷却。</summary>
    public void ForceEnd()
    {
        if (ActiveRemaining > 0f)
            EndNow();
    }

    public void Tick(float now, float deltaTime)
    {
        if (IsActive)
        {
            ActiveRemaining -= deltaTime;
            OnActiveTick(now, deltaTime);

            if (ActiveRemaining <= 0f)
            {
                ActiveRemaining = 0f;
                EndActive();
            }

            return;
        }

        if (CooldownRemaining > 0f)
            TickCooldown(deltaTime);
        else
            IsBurning = false;
    }

    /// <summary>提前结束生效阶段（例如护盾破碎），进入冷却。</summary>
    protected void EndNow()
    {
        if (!IsActive)
            return;

        ActiveRemaining = 0f;
        EndActive();
    }

    protected void StartCooldown()
    {
        CooldownRemaining = CooldownTotal;
    }

    private void EndActive()
    {
        OnEndActive();
        StartCooldown();
    }

    private void TickCooldown(float deltaTime)
    {
        float threshold = Mathf.Max(0f, RzConfig.WaterThreshold.Value);

        float hydration = -1f;
        ActiveHealthController? hc = null;
        try
        {
            var player = _raid.MainPlayer;
            if (player != null)
            {
                hc = player.ActiveHealthController;
                if (hc != null)
                    hydration = hc.Hydration.Current;
            }
        }
        catch
        {
        }

        float speed;
        float drain = 0f;
        bool boosted = false;

        if (hydration <= threshold)
        {
            // 缺水冷却：不消耗水分，耗时 ×2（速度 ×0.5）
            speed = 0.5f;
        }
        else if (CooldownBoostActive)
        {
            // 加速冷却：×倍率（默认 3），每个冷却中的技能各自扣水
            speed = Mathf.Max(1f, RzConfig.BoostSpeedMult.Value);
            drain = Mathf.Max(0f, RzConfig.BoostWaterDrain.Value);
            boosted = true;
        }
        else
        {
            // 默认冷却：×1，每个冷却中的技能各自扣水
            speed = 1f;
            drain = Mathf.Max(0f, RzConfig.CooldownWaterDrain.Value);
        }

        if (drain > 0f && hc != null)
        {
            try
            {
                hc.ChangeHydration(-drain * deltaTime);
            }
            catch
            {
            }
        }

        IsBurning = boosted;
        CooldownRemaining -= deltaTime * speed;
        if (CooldownRemaining <= 0f)
        {
            CooldownRemaining = 0f;
            IsBurning = false;
        }
    }

    /// <summary>执行技能主体。返回 false = 拒绝（不扣能量、不进冷却）。</summary>
    protected abstract bool OnActivate();

    protected virtual void OnActiveStart()
    {
    }

    protected virtual void OnActiveTick(float now, float deltaTime)
    {
    }

    protected virtual void OnEndActive()
    {
    }

    public virtual void Dispose()
    {
        ActiveRemaining = 0f;
        IsBurning = false;
        CooldownRemaining = 0f;
    }
}
