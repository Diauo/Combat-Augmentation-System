using System.Collections.Generic;
using EFT;
using RZSkills.Client.Core;
using RZSkills.Client.Visuals;
using UnityEngine;

namespace RZSkills.Client.Skills;

/// <summary>
/// 「扫描仪」（v3.3：遮挡/距离平衡）：
///  · 轻点 V 开关；免费开启，生效期每秒消耗能量（默认 0.5/s），能量见底自动关闭；关闭后冷却 5s。
///  · 屏幕中央扫描框（默认 16:9 长方形）；**视线通畅 ≤600m、被遮挡仅 ≤50m**（均可配）；
///    被遮挡目标的扫描时间 +25%（可配）。距离越远成像越慢：基准(3s)→线性→封顶(10s)。
///  · **角度域判定**：以激活瞬间的视野锁定固定世界视角区域——开镜/收镜不打断扫描。
///  · 扫描进程（默认 1，可调并行）：**评分制 + 滞回抢占**——
///    评分 = 瞄准偏差角(°) + 距离/100米 × 权重(默认 3°)；满槽时挑战者评分须低于持有者的 70%
///    才抢占换标（1s 冷却防抖）；被抢目标进度保留。
///  · 目标须**连续**在框内（且符合距离门限），离开/超限立即清零。完成 → 标记（倒三角 + 距离 + 剪影）；
///    在框内不倒计时，离开框才开始倒计时，回到框内暂停。
/// </summary>
public sealed class ScanSkill : SkillBase
{
    private const float RefreshInterval = 0.15f;
    private const float SwitchCooldown = 1f;

    private static readonly RaycastHit[] OcclusionBuffer = new RaycastHit[16];
    private static int _occlusionMask = -1;

    private readonly ChamsService _chams;
    private readonly List<ScannedTarget> _targets = new();
    private readonly List<ScannedTarget> _fresh = new();
    private readonly List<ScannedTarget> _candidates = new();
    private readonly List<ScannedTarget> _marked = new();
    private readonly HashSet<Player> _seen = new();

    private float _tanHalfX = 0.35f;
    private float _tanHalfY = 0.20f;
    private float _nextRefresh;
    private float _nextSwitchTime;

    public ScanSkill(RaidManager raid, ChamsService chams)
        : base(raid, "scan", "扫描", "Scan")
    {
        _chams = chams;
    }

    protected override float EnergyCostValue => 0f; // 免费开启

    public override float CooldownTotal => RzConfig.ScanOffCooldown.Value;

    protected override float ActiveDuration => 99999f;

    public override string StatusText =>
        IsActive ? Loc.T("扫描中", "Scanning")
        : CooldownRemaining > 0f ? $"{Loc.T("冷却", "Cooldown")} {CooldownRemaining:0}s"
        : Loc.T("就绪", "Ready");

    public IReadOnlyList<ScannedTarget> Targets => _targets;

    /// <summary>激活时锁定的扫描框（角度域，正切值）——供 HUD 按当前 FOV 还原屏幕尺寸。</summary>
    internal float BoxTanHalfX => _tanHalfX;

    internal float BoxTanHalfY => _tanHalfY;

    public override bool TryActivate()
    {
        if (IsActive)
        {
            Plugin.Log.LogInfo("[RZSkills] scanner off (manual)");
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

        if (RzConfig.EnergyCostEnabled.Value)
        {
            float min = RzConfig.ScanMinEnergy.Value;
            try
            {
                if (hc.Energy.Current < min)
                {
                    Plugin.Log.LogInfo($"[RZSkills] scanner refused: energy {hc.Energy.Current:0.#} < {min:0.#}");
                    return false;
                }
            }
            catch
            {
            }
        }

        var cam = CameraResolver.Resolve();
        if (cam == null)
            return false;

        // 以激活瞬间的视野为基准，锁定扫描框的角度域大小
        float frac = Mathf.Clamp(RzConfig.ScanBoxSize.Value, 5f, 95f) / 100f;
        float aspect = Mathf.Clamp(RzConfig.ScanBoxAspect.Value, 0.5f, 2.5f);
        float fov = Mathf.Clamp(cam.fieldOfView, 5f, 140f);
        float fovTan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        _tanHalfY = frac * fovTan;
        _tanHalfX = frac * aspect * fovTan;

        _chams.RebuildMaterials();
        _targets.Clear();
        _nextRefresh = 0f;
        _nextSwitchTime = 0f;

        Plugin.Log.LogInfo(
            $"[RZSkills] scanner on: box={RzConfig.ScanBoxSize.Value:0}% x{RzConfig.ScanBoxAspect.Value:0.00}, " +
            $"fov={fov:0}, processes={RzConfig.ScanProcesses.Value}, drain={RzConfig.ScanEnergyDrain.Value:0.#}/s");
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
                hc.ChangeEnergy(-RzConfig.ScanEnergyDrain.Value * deltaTime);
                if (hc.Energy.Current <= 0f)
                {
                    Plugin.Log.LogInfo("[RZSkills] scanner off: energy depleted");
                    EndNow();
                    return;
                }
            }
            catch
            {
            }
        }

        if (now >= _nextRefresh)
        {
            _nextRefresh = now + RefreshInterval;
            RefreshWorld(player);
        }

        UpdateScanning(now, deltaTime);
        UpdateMarks(deltaTime);
    }

    protected override void OnEndActive()
    {
        ClearAll();
        Plugin.Log.LogInfo("[RZSkills] scanner off — cooldown started");
    }

    public override void Dispose()
    {
        base.Dispose();
        ClearAll();
    }

    // ── 世界刷新：合并存活敌人 + 角度域框内判定 ─────────────

    private void RefreshWorld(Player player)
    {
        var cam = CameraResolver.Resolve();
        if (cam == null)
            return;

        TargetService.CollectEnemies(Raid.World, player, _fresh);

        // 合并：保留旧条目的进度/标记状态
        _seen.Clear();
        foreach (var fresh in _fresh)
        {
            _seen.Add(fresh.Player);
            var entry = Find(fresh.Player);
            if (entry == null)
            {
                entry = fresh;
                _targets.Add(entry);
            }
            else
            {
                entry.Kind = fresh.Kind;
                entry.IsAlive = true;
            }
        }

        // 移除死亡 / 消失的目标（先摘剪影）
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            var t = _targets[i];
            if (!_seen.Contains(t.Player))
            {
                t.IsAlive = false;
                _chams.Restore(t.Player!);
                _targets.RemoveAt(i);
            }
        }

        foreach (var t in _targets)
        {
            try
            {
                var head = GetHeadPosition(t.Player);
                t.Position = t.Player.Transform.position;
                t.Distance = Vector3.Distance(cam.transform.position, head);

                // 角度域判定：与镜头朝向的夹角正切值（与 FOV 无关）
                var local = cam.transform.InverseTransformPoint(head);
                bool inFront = local.z > 0.1f;
                t.InBox = inFront &&
                          Mathf.Abs(local.x) <= _tanHalfX * local.z &&
                          Mathf.Abs(local.y) <= _tanHalfY * local.z;

                // 瞄准偏差角（评分用）：镜头朝向与目标方向的夹角
                t.AimOffsetDeg = Vector3.Angle(cam.transform.forward, head - cam.transform.position);

                // 队列排序用屏幕位置（保留诊断用途）
                var sp = cam.WorldToScreenPoint(head);
                t.ScreenPoint = new Vector2(sp.x, sp.y);

                // 视线遮挡判定（仅框内未标记目标需要）
                t.Occluded = t.InBox && !t.Marked && IsOccluded(player, t.Player, cam, head);
            }
            catch
            {
                // 投影失败：保持上帧状态，不误清零
            }

            // 距离门限：视线通畅 → 远限（默认 600m）；被遮挡 → 近限（默认 50m）
            float maxDistance = t.Occluded
                ? Mathf.Max(1f, RzConfig.ScanMaxOccludedDistance.Value)
                : Mathf.Max(1f, RzConfig.ScanMaxVisibleDistance.Value);
            t.Eligible = t.InBox && t.Distance <= maxDistance;

            ComputeRequired(t);

            if (!t.Eligible && !t.Marked)
            {
                t.Progress = 0f; // 离开框 / 超出距离门限：立即清零（未标记目标）
                t.InSlot = false;
            }
        }

        // 剪影维护（已标记目标：动态纳入换装/重建渲染器）
        _marked.Clear();
        foreach (var t in _targets)
        {
            if (t.Marked)
                _marked.Add(t);
        }

        if (_marked.Count > 0)
            _chams.Maintain(_marked);
    }

    private static void ComputeRequired(ScannedTarget target)
    {
        float baseTime = Mathf.Clamp(RzConfig.ScanBaseTime.Value, 0.5f, 30f);
        float maxTime = Mathf.Max(baseTime, RzConfig.ScanMaxTime.Value);
        float far = Mathf.Max(1f, RzConfig.ScanFarDistance.Value);
        float time = baseTime + (maxTime - baseTime) * Mathf.Clamp01(target.Distance / far);

        if (target.Occluded)
            time *= 1f + Mathf.Clamp(RzConfig.ScanOccludedTimePenalty.Value, 0f, 100f) / 100f;

        target.Required = time;
    }

    /// <summary>视线遮挡判定：相机 → 目标头部的射线是否被地形/实体几何挡住（窗户/围栏等透明层不遮挡）。</summary>
    private static bool IsOccluded(Player self, Player target, Camera cam, Vector3 head)
    {
        var origin = cam.transform.position;
        var delta = head - origin;
        float length = delta.magnitude;
        if (length < 1.5f)
            return false;

        if (_occlusionMask == -1)
        {
            int mask = LayerMask.GetMask("Terrain", "HighPolyCollider");
            _occlusionMask = mask != 0 ? mask : Physics.DefaultRaycastLayers;
        }

        float castLength = length - 0.4f; // 留出目标身位，避免命中目标本体
        int count = Physics.RaycastNonAlloc(
            origin, delta / length, OcclusionBuffer, castLength, _occlusionMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            var collider = OcclusionBuffer[i].collider;
            if (collider == null)
                continue;

            if (IsOwn(self, collider) || IsOwn(target, collider))
                continue;

            return true;
        }

        return false;
    }

    private static bool IsOwn(Player player, Collider collider)
    {
        try
        {
            if (player.HasBodyPartCollider(collider))
                return true;
        }
        catch
        {
        }

        try
        {
            var root = player.gameObject;
            var transform = collider.transform;
            while (transform != null)
            {
                if (transform.gameObject == root)
                    return true;
                transform = transform.parent;
            }
        }
        catch
        {
        }

        return false;
    }

    // ── 扫描进程：评分制 + 滞回抢占的目标切换策略 ───────────

    /// <summary>换标评分：瞄准偏差角(°) + 距离/100米 × 距离权重(默认 3°)。分低者优。</summary>
    private static float ScoreOf(ScannedTarget target)
    {
        float weight = Mathf.Clamp(RzConfig.ScanDistanceWeight.Value, 0f, 10f);
        return target.AimOffsetDeg + target.Distance * 0.01f * weight;
    }

    private void UpdateScanning(float now, float deltaTime)
    {
        int slots = Mathf.Clamp(RzConfig.ScanProcesses.Value, 1, 6);

        // 有效候选：符合距离门限、存活、未标记
        _candidates.Clear();
        int occupied = 0;
        foreach (var t in _targets)
        {
            if (!t.IsAlive || t.Marked || !t.Eligible)
                continue;

            _candidates.Add(t);
            if (t.InSlot)
                occupied++;
        }

        if (_candidates.Count == 0)
            return;

        _candidates.Sort((a, b) => ScoreOf(a).CompareTo(ScoreOf(b)));

        // 满槽竞争（滞回抢占）：挑战者须明显更优（评分 < 持有者 × 滞回系数）才换标；1s 冷却防抖
        if (occupied >= slots && _candidates.Count > occupied && now >= _nextSwitchTime)
        {
            ScannedTarget? worstHolder = null;
            ScannedTarget? bestChallenger = null;

            for (int i = 0; i < _candidates.Count; i++)
            {
                var t = _candidates[i];
                if (t.InSlot)
                {
                    if (worstHolder == null || ScoreOf(t) > ScoreOf(worstHolder))
                        worstHolder = t;
                }
                else if (bestChallenger == null)
                {
                    bestChallenger = t; // 已按评分升序，首个非持有者即最优挑战者
                }
            }

            float margin = Mathf.Clamp(RzConfig.ScanSwitchMargin.Value, 0.3f, 1f);
            if (worstHolder != null && bestChallenger != null &&
                ScoreOf(bestChallenger) < ScoreOf(worstHolder) * margin)
            {
                float oldScore = ScoreOf(worstHolder);
                float newScore = ScoreOf(bestChallenger);

                worstHolder.InSlot = false;
                bestChallenger.InSlot = true;
                _nextSwitchTime = now + SwitchCooldown;

                Plugin.Dbg(
                    $"scan slot switch: '{NameOf(worstHolder)}' ({oldScore:0.0}) -> " +
                    $"'{NameOf(bestChallenger)}' ({newScore:0.0})  [d={bestChallenger.Distance:0}m]");
            }
        }

        // 空槽补位：按评分最优
        if (occupied < slots)
        {
            for (int i = 0; i < _candidates.Count && occupied < slots; i++)
            {
                var t = _candidates[i];
                if (t.InSlot)
                    continue;

                t.InSlot = true;
                occupied++;
            }
        }

        // 进度累积 & 完成
        foreach (var t in _targets)
        {
            if (!t.InSlot || !t.Eligible || t.Marked || !t.IsAlive)
                continue;

            t.Progress += deltaTime;
            if (t.Progress < t.Required)
                continue;

            t.Marked = true;
            t.MarkRemaining = Mathf.Max(1f, RzConfig.ScanMarkDuration.Value);
            t.Progress = 0f;
            t.InSlot = false;
            _chams.Apply(t.Player, t.Kind);

            Plugin.Log.LogInfo(
                $"[RZSkills] marked '{NameOf(t)}' ({t.Kind}) d={t.Distance:0}m in {t.Required:0.0}s" +
                $"{(t.Occluded ? " [occluded]" : string.Empty)}");
        }
    }

    // ── 标记生命周期：在框内冻结，离开框才倒计时 ────────────

    private void UpdateMarks(float deltaTime)
    {
        float duration = Mathf.Max(1f, RzConfig.ScanMarkDuration.Value);

        foreach (var t in _targets)
        {
            if (!t.Marked)
                continue;

            if (t.InBox)
            {
                // 在框内：不倒计时（保持在框内即持续锁定）
                if (t.MarkRemaining > duration)
                    t.MarkRemaining = duration;
                continue;
            }

            t.MarkRemaining -= deltaTime;
            if (t.MarkRemaining <= 0f)
            {
                t.Marked = false;
                t.MarkRemaining = 0f;
                t.InSlot = false;
                _chams.Restore(t.Player!);
                Plugin.Dbg("mark expired (out of box)");
            }
        }
    }

    // ── 工具 ────────────────────────────────────────────────

    private static string NameOf(ScannedTarget target)
    {
        try
        {
            return target.Player?.Profile?.Nickname ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private ScannedTarget? Find(Player player)
    {
        for (int i = 0; i < _targets.Count; i++)
        {
            if (_targets[i].Player == player)
                return _targets[i];
        }

        return null;
    }

    private static Vector3 GetHeadPosition(Player player)
    {
        var bones = player.PlayerBones;
        return bones != null
            ? bones.Head.position
            : player.Transform.position + Vector3.up * 1.6f;
    }

    private void ClearAll()
    {
        _chams.RestoreAll();
        _targets.Clear();
    }
}
