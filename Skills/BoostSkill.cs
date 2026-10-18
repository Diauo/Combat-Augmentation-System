using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using RZSkills.Client.Core;
using UnityEngine;

namespace RZSkills.Client.Skills;

/// <summary>
/// 「超频 | COMBAT OVERDRIVE」绝境最后一搏（能量驱动的战斗针剂）：
///  · 激活不耗能量；生效期每秒消耗能量（默认 1/s），能量见底自动关闭；再按技能键手动关闭；触发冷却。
///  · 增益包：生命回复（默认 6/s 分摊到受伤部位，不救黑肢）、止痛（**仅管理自己施加的那一份**，不碰原有止痛）、
///    耐力恢复+上限加成、减伤（默认 -20%）、负重上限加成（默认 +15%）、防眩晕（持续清除 Contusion）、
///    技能临时强化（默认 +10 级 ×11 项；力量额外加成为原版移动速度通道）。
///  · 所有副作用在手动关闭 / 断能 / 战局结束时精确还原。
/// </summary>
public sealed class BoostSkill : SkillBase
{
    private static readonly EBodyPart[] HealParts =
    {
        EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
        EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg,
    };

    private static readonly float[] MissingBuffer = new float[HealParts.Length];

    private const float PainkillerRefresh = 40f;
    private const float PurgeInterval = 0.25f;

    private readonly List<(Skill Skill, int Amount)> _buffedSkills = new();

    private float _nextPainkiller;
    private float _nextPurge;

    // 快照（用于精确还原）
    private bool _staminaApplied;
    private float _origRestoreRateBuff;
    private float _origCapacityBuff;
    private bool _damageApplied;
    private float _damageFactor = 1f;
    private bool _weightApplied;
    private float _origWeightRelative;

    private PropertyInfo? _weightProperty;
    private FieldInfo? _weightField;
    private bool _weightMemberResolved;

    /// <summary>我们自己施加的那一份止痛效果（只清理它，绝不动玩家原有止痛）。</summary>
    private ActiveHealthController.PainKiller? _ourPainkiller;

    public BoostSkill(RaidManager raid)
        : base(raid, "boost", "超频", "Overdrive")
    {
    }

    protected override float EnergyCostValue => 0f;

    public override float CooldownTotal => RzConfig.BoostCooldown.Value;

    protected override float ActiveDuration => 99999f;

    public override string StatusText =>
        IsActive ? Loc.T("生效中", "Active")
        : CooldownRemaining > 0f ? $"{Loc.T("冷却", "Cooldown")} {CooldownRemaining:0}s"
        : Loc.T("就绪", "Ready");

    public override bool TryActivate()
    {
        if (IsActive)
        {
            Plugin.Log.LogInfo("[RZSkills] Boost deactivated (manual)");
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

        var hc = player.ActiveHealthController;
        if (hc == null)
            return false;

        // 激活门槛：能量不足直接拒绝
        if (RzConfig.EnergyCostEnabled.Value)
        {
            float min = RzConfig.BoostMinEnergy.Value;
            try
            {
                if (hc.Energy.Current < min)
                {
                    Plugin.Log.LogInfo($"[RZSkills] boost refused: energy {hc.Energy.Current:0.#} < {min:0.#}");
                    return false;
                }
            }
            catch
            {
            }
        }

        ResetSnapshots();

        ApplyPainkiller(hc);
        ApplyStamina(player);
        ApplyDamageReduction(hc);
        ApplyWeight(player);
        ApplySkillBuffs(player);

        _nextPainkiller = Time.unscaledTime + PainkillerRefresh;
        _nextPurge = Time.unscaledTime + PurgeInterval;

        Plugin.Log.LogInfo(
            $"[RZSkills] Boost activated: drain={RzConfig.BoostEnergyDrain.Value:0.#}/s, " +
            $"regen={RzConfig.BoostHealthRegen.Value:0.#}/s, dmg-={RzConfig.BoostDamageReduction.Value:0}%");
        return true;
    }

    protected override void OnActiveTick(float now, float deltaTime)
    {
        var player = Raid.MainPlayer;
        if (player == null)
            return;

        var hc = player.ActiveHealthController;
        if (hc == null)
            return;

        // 能量消耗 & 见底自动关闭
        if (RzConfig.EnergyCostEnabled.Value)
        {
            try
            {
                hc.ChangeEnergy(-RzConfig.BoostEnergyDrain.Value * deltaTime);
                if (hc.Energy.Current <= 0f)
                {
                    Plugin.Log.LogInfo("[RZSkills] Boost ended: energy depleted");
                    EndNow();
                    return;
                }
            }
            catch
            {
            }
        }

        // 生命回复（分摊到受伤部位）
        HealDamagedParts(hc, RzConfig.BoostHealthRegen.Value * deltaTime);

        // 耐力 / 负重再断言（防被其它系统覆盖）
        AssertStamina(player);
        AssertWeight(player);

        // 止痛维护：仅当我们的那一针消失时补一针（绝不叠加）
        if (now >= _nextPainkiller)
        {
            _nextPainkiller = now + PainkillerRefresh;
            RefreshPainkiller(hc);
        }

        // 防眩晕：清除 Contusion 效果
        if (now >= _nextPurge)
        {
            _nextPurge = now + PurgeInterval;
            PurgeContusions(hc);
        }
    }

    protected override void OnEndActive()
    {
        RevertAll();
        Plugin.Log.LogInfo("[RZSkills] Boost ended — cooldown started");
    }

    public override void Dispose()
    {
        base.Dispose();
        RevertAll();
    }

    // ── 生命回复 ────────────────────────────────────────────

    private static void HealDamagedParts(ActiveHealthController hc, float budget)
    {
        if (budget <= 0f)
            return;

        float totalMissing = 0f;

        for (int i = 0; i < HealParts.Length; i++)
        {
            MissingBuffer[i] = 0f;

            try
            {
                if (hc.IsBodyPartDestroyed(HealParts[i]))
                    continue; // 不救黑肢

                var health = hc.GetBodyPartHealth(HealParts[i]);
                float miss = health.Maximum - health.Current;
                if (miss <= 0.01f)
                    continue;

                MissingBuffer[i] = miss;
                totalMissing += miss;
            }
            catch
            {
            }
        }

        if (totalMissing <= 0f)
            return;

        for (int i = 0; i < HealParts.Length; i++)
        {
            if (MissingBuffer[i] <= 0f)
                continue;

            float amount = budget * (MissingBuffer[i] / totalMissing);
            try
            {
                hc.ChangeHealth(HealParts[i], amount, default);
            }
            catch
            {
            }
        }
    }

    // ── 止痛（只管理我们自己那一针） ────────────────────────

    private void ApplyPainkiller(ActiveHealthController hc)
    {
        try
        {
            _ourPainkiller = hc.AddEffect<ActiveHealthController.PainKiller>(EBodyPart.Head);
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost painkiller failed: {ex.Message}");
        }
    }

    private void RefreshPainkiller(ActiveHealthController hc)
    {
        try
        {
            if (_ourPainkiller != null && ContainsEffect(hc, _ourPainkiller))
                return; // 我们的那一针仍在，不补、不叠

            _ourPainkiller = hc.AddEffect<ActiveHealthController.PainKiller>(EBodyPart.Head);
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost painkiller refresh failed: {ex.Message}");
        }
    }

    private void RemoveOurPainkiller(ActiveHealthController? hc)
    {
        if (_ourPainkiller == null)
            return;

        try
        {
            if (hc != null && ContainsEffect(hc, _ourPainkiller))
                _ourPainkiller.ForceResidue(); // 只结束我们那针（游戏针剂同款收敛方式）
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost painkiller cleanup failed: {ex.Message}");
        }

        _ourPainkiller = null;
    }

    private static bool ContainsEffect(ActiveHealthController hc, ActiveHealthController.Effect effect)
    {
        try
        {
            var effects = hc.Effects;
            if (effects == null)
                return false;

            for (int i = 0; i < effects.Count; i++)
            {
                if (ReferenceEquals(effects[i], effect))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    // ── 防眩晕 ──────────────────────────────────────────────

    private static void PurgeContusions(ActiveHealthController hc)
    {
        try
        {
            var effects = hc.Effects;
            if (effects == null)
                return;

            for (int i = effects.Count - 1; i >= 0; i--)
            {
                if (effects[i] is ActiveHealthController.Contusion contusion)
                {
                    try
                    {
                        contusion.ForceResidue();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost contusion purge failed: {ex.Message}");
        }
    }

    // ── 耐力 ────────────────────────────────────────────────

    private void ApplyStamina(Player player)
    {
        try
        {
            var physical = player.Physical;
            if (physical == null)
                return;

            _origRestoreRateBuff = physical.RestoreRateBuff;
            _origCapacityBuff = physical.CapacityBuff;
            _staminaApplied = true;

            AssertStamina(player);
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost stamina failed: {ex.Message}");
        }
    }

    private void AssertStamina(Player player)
    {
        if (!_staminaApplied)
            return;

        try
        {
            var physical = player.Physical;
            if (physical == null)
                return;

            physical.RestoreRateBuff = _origRestoreRateBuff + RzConfig.BoostStaminaRestore.Value;
            physical.CapacityBuff = _origCapacityBuff + RzConfig.BoostStaminaCapacity.Value;
        }
        catch
        {
        }
    }

    // ── 减伤 ────────────────────────────────────────────────

    private void ApplyDamageReduction(ActiveHealthController hc)
    {
        try
        {
            float percent = Mathf.Clamp(RzConfig.BoostDamageReduction.Value, 0f, 80f);
            if (percent <= 0f)
                return;

            _damageFactor = 1f - percent / 100f;
            hc.AddDamageMultiplier(_damageFactor);
            _damageApplied = true;
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost damage reduction failed: {ex.Message}");
        }
    }

    // ── 负重 ────────────────────────────────────────────────

    private void ApplyWeight(Player player)
    {
        try
        {
            float bonusPercent = Mathf.Clamp(RzConfig.BoostWeightBonus.Value, 0f, 60f);
            if (bonusPercent <= 0f)
                return;

            var hc = player.ActiveHealthController;
            if (hc == null)
                return;

            ResolveWeightMember(hc);
            if (_weightProperty == null && _weightField == null)
                return;

            _origWeightRelative = ReadWeight(hc);
            WriteWeight(hc, _origWeightRelative * (1f + bonusPercent / 100f));
            _weightApplied = true;
            RefreshWeight(player);
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost weight failed: {ex.Message}");
            _weightApplied = false;
        }
    }

    private void AssertWeight(Player player)
    {
        if (!_weightApplied)
            return;

        try
        {
            var hc = player.ActiveHealthController;
            if (hc == null)
                return;

            float want = _origWeightRelative *
                         (1f + Mathf.Clamp(RzConfig.BoostWeightBonus.Value, 0f, 60f) / 100f);
            if (Mathf.Abs(ReadWeight(hc) - want) > 0.0005f)
            {
                WriteWeight(hc, want);
                RefreshWeight(player);
            }
        }
        catch
        {
        }
    }

    private static void RefreshWeight(Player player)
    {
        try
        {
            var physical = player.Physical;
            physical?.UpdateWeightLimits();
            physical?.OnWeightUpdated();
        }
        catch
        {
        }
    }

    private void ResolveWeightMember(ActiveHealthController hc)
    {
        if (_weightMemberResolved)
            return;
        _weightMemberResolved = true;

        const string memberName = "CarryingWeightRelativeModifier";

        for (var type = hc.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(memberName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null && property.CanWrite && property.PropertyType == typeof(float))
            {
                _weightProperty = property;
                return;
            }

            var field = type.GetField(memberName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null && field.FieldType == typeof(float) && !field.IsInitOnly)
            {
                _weightField = field;
                return;
            }
        }

        Plugin.Dbg("boost weight: member not found (skipped)");
    }

    private float ReadWeight(ActiveHealthController hc)
    {
        if (_weightProperty != null)
            return (float)_weightProperty.GetValue(hc);
        if (_weightField != null)
            return (float)_weightField.GetValue(hc);
        return 0f;
    }

    private void WriteWeight(ActiveHealthController hc, float value)
    {
        if (_weightProperty != null)
            _weightProperty.SetValue(hc, value);
        else if (_weightField != null)
            _weightField.SetValue(hc, value);
    }

    // ── 技能强化（力量额外加成 = 原版移动速度通道） ─────────

    private void ApplySkillBuffs(Player player)
    {
        try
        {
            int baseAmount = Mathf.Clamp(RzConfig.BoostSkillBonus.Value, 0, 30);
            int strengthExtra = Mathf.Clamp(RzConfig.BoostStrengthExtra.Value, 0, 40);

            var manager = player.Skills;
            if (manager == null)
                return;

            foreach (var rawName in RzConfig.BoostSkillList.Value.Split(','))
            {
                var name = rawName.Trim();
                if (name.Length == 0)
                    continue;

                if (!Enum.TryParse<ESkillId>(name, true, out var id))
                {
                    Plugin.Dbg($"boost skill: unknown '{name}'");
                    continue;
                }

                int amount = baseAmount;
                if (id == ESkillId.Strength)
                    amount += strengthExtra; // 原版移动速度（走/跑）由力量等级驱动

                if (amount <= 0)
                    continue;

                Skill? skill = null;
                try
                {
                    skill = manager.GetSkill(id);
                }
                catch
                {
                }

                if (skill == null)
                {
                    Plugin.Dbg($"boost skill: '{name}' unavailable");
                    continue;
                }

                skill.Buff += amount;
                _buffedSkills.Add((skill, amount));
            }

            Plugin.Log.LogInfo(
                $"[RZSkills] Boost skill buffs +{baseAmount} (strength +{baseAmount + strengthExtra}) x {_buffedSkills.Count} skill(s)");
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"boost skills failed: {ex.Message}");
        }
    }

    // ── 还原 ────────────────────────────────────────────────

    private void RevertAll()
    {
        RemoveOurPainkiller(Raid.MainPlayer?.ActiveHealthController);

        foreach (var (skill, amount) in _buffedSkills)
        {
            try
            {
                if (skill != null)
                    skill.Buff -= amount;
            }
            catch
            {
            }
        }

        _buffedSkills.Clear();

        if (_staminaApplied)
        {
            try
            {
                var physical = Raid.MainPlayer?.Physical;
                if (physical != null)
                {
                    physical.RestoreRateBuff = _origRestoreRateBuff;
                    physical.CapacityBuff = _origCapacityBuff;
                }
            }
            catch
            {
            }

            _staminaApplied = false;
        }

        if (_weightApplied)
        {
            try
            {
                var player = Raid.MainPlayer;
                var hc = player?.ActiveHealthController;
                if (player != null && hc != null)
                {
                    WriteWeight(hc, _origWeightRelative);
                    RefreshWeight(player);
                }
            }
            catch
            {
            }

            _weightApplied = false;
        }

        if (_damageApplied)
        {
            try
            {
                Raid.MainPlayer?.ActiveHealthController?.AddDamageMultiplier(1f / _damageFactor);
            }
            catch
            {
            }

            _damageApplied = false;
        }
    }

    private void ResetSnapshots()
    {
        _staminaApplied = false;
        _damageApplied = false;
        _weightApplied = false;
        _ourPainkiller = null;
        _buffedSkills.Clear();
    }
}
