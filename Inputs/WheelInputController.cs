using System.Collections.Generic;
using CombatAugmentationSystem.Client.Core;
using CombatAugmentationSystem.Client.Skills;
using UnityEngine;

namespace CombatAugmentationSystem.Client.Inputs;

/// <summary>轮盘展开的状态标志（供输入冻结补丁查询）。</summary>
internal static class WheelGate
{
    internal static bool IsOpen;
}

/// <summary>
/// 技能轮盘输入控制器（v2 语义）：
///  · 单击技能键（&lt; 阈值）→ 触发当前选中的技能；
///  · 按住技能键（≥ 阈值）→ 弹出轮盘，仅用于【选择】技能：
///      鼠标悬停高亮，松开或左键点击 = 确认选择（不触发）；松开时未高亮 = 取消。
/// 视觉由 HUD 覆盖层绘制。
/// </summary>
internal sealed class WheelInputController
{
    private enum Phase
    {
        Idle,
        Hold,
        Open,
    }

    private const float SelectionDeadzonePx = 40f;

    private readonly RaidManager _raid;
    private readonly List<ISkill> _skills = new();

    private Phase _phase = Phase.Idle;
    private float _pressTime;
    private int _highlighted = -1;
    private int _selectedIndex;

    /// <summary>点击确认选择后仍按住技能键时：等松开再允许新一轮（防止轮盘自动重开）。</summary>
    private bool _awaitRelease;

    internal WheelInputController(RaidManager raid)
    {
        _raid = raid;
        RefreshSkills();
    }

    internal bool IsOpen => _phase == Phase.Open;

    internal int HighlightedIndex => _highlighted;

    internal int SelectedIndex => _selectedIndex;

    internal ISkill? SelectedSkill
    {
        get
        {
            if (_skills.Count == 0)
                return null;
            return _skills[Mathf.Clamp(_selectedIndex, 0, _skills.Count - 1)];
        }
    }

    internal IReadOnlyList<ISkill> Skills => _skills;

    internal void RefreshSkills()
    {
        _skills.Clear();
        _skills.Add(_raid.Scan);
        _skills.Add(_raid.Shield);
        _skills.Add(_raid.Boost);
        _selectedIndex = Mathf.Clamp(_selectedIndex, 0, Mathf.Max(0, _skills.Count - 1));
    }

    internal void Tick(float now)
    {
        bool keyHeld = SkillKeyHeldNow();

        if (_awaitRelease)
        {
            if (!keyHeld)
                _awaitRelease = false;
            return;
        }

        switch (_phase)
        {
            case Phase.Idle:
                if (keyHeld)
                {
                    _phase = Phase.Hold;
                    _pressTime = now;
                }
                break;

            case Phase.Hold:
                if (!keyHeld)
                {
                    if (now - _pressTime < RzConfig.WheelHoldMs.Value / 1000f)
                        TriggerSelected();
                    _phase = Phase.Idle;
                }
                else if (now - _pressTime >= RzConfig.WheelHoldMs.Value / 1000f)
                {
                    OpenWheel();
                }
                break;

            case Phase.Open:
                UpdateHighlight();

                if (UnityEngine.Input.GetMouseButtonDown(0) && _highlighted >= 0)
                {
                    CommitSelection();
                    CloseWheel();
                    break;
                }

                if (!keyHeld)
                {
                    if (_highlighted >= 0)
                        CommitSelection();
                    CloseWheel();
                }
                break;
        }
    }

    /// <summary>强制收起（战局结束 / 清理时调用）。</summary>
    internal void ForceClose()
    {
        if (_phase == Phase.Open)
            CloseWheel();

        _phase = Phase.Idle;
        _awaitRelease = false;
    }

    private void TriggerSelected()
    {
        var skill = SelectedSkill;
        if (skill == null)
            return;

        // 冷却中按技能键 = 切换“加速冷却”（默认冷却不烧水，只有这里才会加速）
        if (!skill.Ready && skill.CooldownRemaining > 0f)
        {
            ToggleCooldownBoost();
            return;
        }

        bool activated = skill.TryActivate();
        Plugin.Log.LogInfo($"[CAS] cast '{skill.Id}': {(activated ? "activated" : "refused")}");
    }

    /// <summary>切换冷却加速（需有技能正在冷却、且水分高于阈值）。</summary>
    private void ToggleCooldownBoost()
    {
        bool anyCooling = false;
        for (int i = 0; i < _skills.Count; i++)
        {
            if (_skills[i].CooldownRemaining > 0f)
            {
                anyCooling = true;
                break;
            }
        }

        if (!anyCooling)
            return;

        if (SkillBase.CooldownBoostActive)
        {
            SkillBase.CooldownBoostActive = false;
            Plugin.Log.LogInfo("[CAS] cooldown boost: OFF");
            return;
        }

        float hydration = -1f;
        try
        {
            var hc = _raid.MainPlayer?.ActiveHealthController;
            if (hc != null)
                hydration = hc.Hydration.Current;
        }
        catch
        {
        }

        if (hydration <= RzConfig.WaterThreshold.Value)
        {
            Plugin.Log.LogInfo(
                $"[CAS] cooldown boost refused: hydration {hydration:0} <= threshold {RzConfig.WaterThreshold.Value:0}");
            return;
        }

        SkillBase.CooldownBoostActive = true;
        Plugin.Log.LogInfo("[CAS] cooldown boost: ON");
    }

    private void CommitSelection()
    {
        if (_highlighted < 0 || _highlighted >= _skills.Count)
            return;

        _selectedIndex = _highlighted;
        Plugin.Log.LogInfo($"[CAS] selected '{_skills[_selectedIndex].Id}'");
    }

    private void OpenWheel()
    {
        _phase = Phase.Open;
        _highlighted = -1;
        WheelGate.IsOpen = true;
        CursorGate.Enter();
        Plugin.Log.LogInfo("[CAS] wheel opened");
    }

    private void CloseWheel()
    {
        _phase = Phase.Idle;
        _awaitRelease = true;
        WheelGate.IsOpen = false;
        CursorGate.Exit();
        Plugin.Log.LogInfo("[CAS] wheel closed");
    }

    private void UpdateHighlight()
    {
        if (_skills.Count == 0)
        {
            _highlighted = -1;
            return;
        }

        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var delta = (Vector2)UnityEngine.Input.mousePosition - center;
        if (delta.magnitude < SelectionDeadzonePx)
        {
            _highlighted = -1;
            return;
        }

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        if (angle < 0f)
            angle += 360f;

        float sector = 360f / _skills.Count;
        int index = Mathf.FloorToInt(((90f - angle + 360f) % 360f) / sector);
        _highlighted = Mathf.Clamp(index, 0, _skills.Count - 1);
    }

    private static bool SkillKeyHeldNow()
    {
        var key = RzConfig.SkillKey.Value;
        if (key == KeyCode.CapsLock && CapsLockHook.Installed)
            return CapsLockHook.IsCapsHeld;
        return UnityEngine.Input.GetKey(key);
    }
}
