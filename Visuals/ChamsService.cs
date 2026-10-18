using System;
using System.Collections.Generic;
using EFT;
using RZSkills.Client.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RZSkills.Client.Visuals;

/// <summary>
/// 穿墙剪影（v7：材质副本识别修复）。
///  · 关键教训：游戏在死亡/尸体化时会把材质【实例化成副本】——视觉相同、对象不同。
///    旧版按实例比对全部漏判。现在一律按【材质名】识别（"RZSkills" 前缀，副本同名）。
///  · 真·身体网格：PlayerBody.BodySkins × LoddedSkin.GetRenderers()（含全部 LOD）。
///  · 死亡事件订阅 + Player 销毁容忍 + 尸体扫描 + 全局副本清扫（按网格名/物件名档案恢复原貌）。
///  · 防护：状态先行注册 / Maintain / 双时点复查（1.5s + 5s）/ 计数日志。
/// </summary>
public sealed class ChamsService : System.IDisposable
{
    private const float VerifySweepDelay = 1.5f;
    private const float VerifySweepDelayLong = 5f;
    private const string ChamMaterialPrefix = "RZSkills";

    private sealed class TargetState
    {
        public Material? Material;
        public string ProfileId = string.Empty;
        public Action<IPlayer>? OnDead;
        public readonly List<Renderer> Renderers = new();
        public readonly List<Material[]?> Originals = new();
        public readonly List<bool> OriginalOcclusion = new();
        public readonly Dictionary<string, Material[]?> NameOriginals = new(StringComparer.Ordinal);
    }

    private sealed class PendingVerify
    {
        public Player Player = null!;
        public TargetState State = null!;
        public float DueTime;
    }

    private readonly Dictionary<Player, TargetState> _active = new();
    private readonly List<PendingVerify> _pending = new();
    private readonly List<Renderer> _tmpRenderers = new();
    private readonly List<Renderer> _candidates = new();

    // 全局档案：跨目标按 物件名 / 网格名 找回原始外观（用于恢复孤儿副本）
    private readonly Dictionary<string, Material[]?> _nameArchive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Material[]?> _meshArchive = new(StringComparer.Ordinal);

    private Material? _pmc;
    private Material? _scav;
    private Material? _boss;

    /// <summary>每次扫描激活前调用：确保 3 个阵营材质存在并按当前配置刷新颜色。</summary>
    internal void RebuildMaterials()
    {
        if (_pmc == null)
            _pmc = CreateMaterial("RZSkills PMC");
        if (_scav == null)
            _scav = CreateMaterial("RZSkills Scav");
        if (_boss == null)
            _boss = CreateMaterial("RZSkills Boss");

        ApplyColor(_pmc, RzConfig.PmcChamColor.Value);
        ApplyColor(_scav, RzConfig.ScavChamColor.Value);
        ApplyColor(_boss, RzConfig.BossChamColor.Value);
    }

    internal void Apply(Player player, TargetKind kind)
    {
        if (player == null || _active.ContainsKey(player))
            return;

        Material? material = kind switch
        {
            TargetKind.Pmc => _pmc,
            TargetKind.Boss => _boss,
            _ => _scav,
        };

        if (material == null || player.PlayerBody == null)
            return;

        if (!FetchRenderers(player, out var renderers) || renderers.Count == 0)
            return;

        var state = new TargetState { Material = material };
        try
        {
            state.ProfileId = player.ProfileId ?? string.Empty;
        }
        catch
        {
            state.ProfileId = string.Empty;
        }

        _active[player] = state; // 先注册：中途任何异常都不丢恢复记录

        // 死亡事件：死亡瞬间立即摘剪影（早于尸体构建/对象销毁）
        try
        {
            state.OnDead = OnPlayerDeadOrUnspawn;
            player.OnIPlayerDeadOrUnspawn += state.OnDead;
        }
        catch (Exception ex)
        {
            state.OnDead = null;
            Plugin.Dbg($"death-event subscribe failed: {ex.Message}");
        }

        for (int i = 0; i < renderers.Count; i++)
            RegisterRenderer(state, renderers[i]);

        Plugin.Dbg($"chams applied: {state.Renderers.Count}/{renderers.Count} renderers");
    }

    /// <summary>扫描期间周期调用：确保已登记渲染器仍穿剪影，并把新渲染器纳入管理。</summary>
    internal void Maintain(IReadOnlyList<ScannedTarget> targets)
    {
        for (int t = 0; t < targets.Count; t++)
        {
            try
            {
                var player = targets[t].Player;
                if (player == null || !_active.TryGetValue(player, out var state) || state.Material == null)
                    continue;

                if (!FetchRenderers(player, out var current))
                    continue;

                for (int i = 0; i < current.Count; i++)
                {
                    var renderer = current[i];
                    if (renderer == null)
                        continue;

                    if (state.Renderers.Contains(renderer))
                    {
                        if (!IsWearingCham(renderer))
                            SetCham(renderer, state.Material); // 被游戏覆盖 → 补上
                    }
                    else
                    {
                        RegisterRenderer(state, renderer); // 新渲染器 → 捕获并纳入
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Dbg($"chams maintain failed: {ex.Message}");
            }
        }
    }

    /// <summary>每帧调用：处理延迟复查队列（1.5s / 5s 两次）。</summary>
    internal void Tick(float now)
    {
        if (_pending.Count == 0)
            return;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (now < _pending[i].DueTime)
                continue;

            var pending = _pending[i];
            _pending.RemoveAt(i);
            RunVerifySweep(pending);
        }
    }

    internal void Restore(Player player)
    {
        if (!_active.TryGetValue(player, out var state))
            return;

        _active.Remove(player);

        if (player != null && state.OnDead != null)
        {
            try
            {
                player.OnIPlayerDeadOrUnspawn -= state.OnDead;
            }
            catch
            {
                // ignore
            }
        }

        int reverted = 0;
        int guest = 0;
        int guestName = 0;
        int missed = 0;
        int skippedNull = 0;
        int skippedNotWearing = 0;

        try
        {
            // 第一遍：已登记渲染器（不要求 Player 对象存活）
            for (int i = 0; i < state.Renderers.Count; i++)
            {
                var renderer = state.Renderers[i];
                if (renderer == null)
                {
                    skippedNull++;
                    continue;
                }

                if (!IsWearingCham(renderer))
                {
                    skippedNotWearing++;
                    continue;
                }

                var originals = state.Originals[i];
                if (originals != null)
                {
                    renderer.sharedMaterials = originals;
                    renderer.allowOcclusionWhenDynamic = state.OriginalOcclusion[i];
                    reverted++;
                }
                else if (TryRestoreFromArchives(state, renderer))
                {
                    guestName++;
                }
                else
                {
                    missed++;
                }
            }

            // 第二遍：候选 = 当前 Player 渲染器（若存活）+ 尸体对象渲染器（按 ProfileId 匹配）
            _candidates.Clear();
            if (player != null && FetchRenderers(player, out var current))
                _candidates.AddRange(current);
            CollectCorpseRenderers(state.ProfileId, _candidates);

            for (int i = 0; i < _candidates.Count; i++)
            {
                var renderer = _candidates[i];
                if (renderer == null || !IsWearingCham(renderer))
                    continue;

                if (TryRestoreRenderer(state, renderer, out bool viaName))
                {
                    if (viaName)
                        guestName++;
                    else
                        guest++;
                }
                else
                {
                    missed++;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[RZSkills] chams restore failed: {ex.Message}");
        }

        Plugin.Log.LogInfo(
            $"[RZSkills] chams restore: reverted={reverted} guest={guest} guestName={guestName} " +
            $"missed={missed} null={skippedNull} notwearing={skippedNotWearing} " +
            $"playerNull={player == null} corpseScan={_candidates.Count > 0}");

        if (missed > 0)
            Plugin.Log.LogWarning(
                $"[RZSkills] chams restore left {missed} renderer(s) — please report to RZ-99");

        var now = Time.unscaledTime;
        _pending.Add(new PendingVerify { Player = player!, State = state, DueTime = now + VerifySweepDelay });
        _pending.Add(new PendingVerify { Player = player!, State = state, DueTime = now + VerifySweepDelayLong });
    }

    internal void RestoreAll()
    {
        var players = new List<Player>(_active.Keys);
        for (int i = 0; i < players.Count; i++)
            Restore(players[i]);
    }

    private void RunVerifySweep(PendingVerify pending)
    {
        var player = pending.Player;
        var state = pending.State;

        if (state.Material == null)
            return;

        if (player != null && _active.ContainsKey(player))
            return;

        int fixedCount = 0;
        int stillMissing = 0;

        try
        {
            _candidates.Clear();
            if (player != null && FetchRenderers(player, out var current))
                _candidates.AddRange(current);
            CollectCorpseRenderers(state.ProfileId, _candidates);

            for (int i = 0; i < _candidates.Count; i++)
            {
                var renderer = _candidates[i];
                if (renderer == null || !IsWearingCham(renderer))
                    continue;

                if (TryRestoreRenderer(state, renderer, out _))
                    fixedCount++;
                else
                    stillMissing++;
            }

            // 全局副本清扫：任何尸体上的"RZSkills"材质（含实例化副本）一并恢复
            fixedCount += SweepWorldCopies();
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"verify sweep failed: {ex.Message}");
        }

        if (fixedCount > 0)
            Plugin.Log.LogInfo($"[RZSkills] chams verify sweep fixed {fixedCount} late renderer(s)");

        if (stillMissing > 0)
            Plugin.Log.LogWarning(
                $"[RZSkills] chams verify sweep: {stillMissing} renderer(s) still chammed (unrecoverable)");
        else if (fixedCount == 0)
            Plugin.Dbg("chams verify sweep clean");
    }

    /// <summary>全局清扫：遍历所有尸体对象，恢复任何仍带 "RZSkills" 材质（含副本）的渲染器。</summary>
    private int SweepWorldCopies()
    {
        int fixedCount = 0;
        int unknown = 0;

        try
        {
            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            var lootItems = world?.LootItems;
            if (lootItems == null)
                return 0;

            int count = lootItems.Count;
            for (int i = 0; i < count; i++)
            {
                if (lootItems.GetByIndex(i) is not EFT.Interactive.Corpse corpse)
                    continue;

                var renderers = corpse.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var renderer = renderers[r];
                    if (renderer == null || !IsWearingCham(renderer))
                        continue;

                    if (TryRestoreFromArchives(null, renderer))
                    {
                        fixedCount++;
                    }
                    else
                    {
                        unknown++;
                        if (unknown <= 3)
                            Plugin.Log.LogWarning(
                                $"[RZSkills] chams orphan copy on '{renderer.gameObject.name}' could not be restored");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"world copy sweep failed: {ex.Message}");
        }

        return fixedCount;
    }

    /// <summary>按原始外观恢复单个渲染器：注册引用直接查；未登记先按名字回退，再查全局档案。</summary>
    private bool TryRestoreRenderer(TargetState state, Renderer renderer, out bool viaName)
    {
        viaName = false;

        Material[]? originals = null;

        int idx = state.Renderers.IndexOf(renderer);
        if (idx >= 0 && state.Originals[idx] != null)
        {
            originals = state.Originals[idx];
        }
        else
        {
            originals = LookupArchive(state, renderer);
            viaName = originals != null;
        }

        if (originals == null)
            return false;

        renderer.sharedMaterials = originals;
        return true;
    }

    /// <summary>先查该目标的名字档案，再查全局档案（物件名 → 网格名）。</summary>
    private Material[]? LookupArchive(TargetState? state, Renderer renderer)
    {
        Material[]? found = null;

        string name = SafeName(renderer);
        if (!string.IsNullOrEmpty(name))
        {
            if (state != null && state.NameOriginals.TryGetValue(name, out var own) && own != null)
                return own;
            if (_nameArchive.TryGetValue(name, out var named) && named != null)
                return named;

            const string cloneSuffix = " (Clone)";
            if (name.EndsWith(cloneSuffix, StringComparison.Ordinal))
            {
                string trimmed = name.Substring(0, name.Length - cloneSuffix.Length);
                if (state != null && state.NameOriginals.TryGetValue(trimmed, out var own2) && own2 != null)
                    return own2;
                if (_nameArchive.TryGetValue(trimmed, out var named2) && named2 != null)
                    return named2;
            }
        }

        string meshName = GetMeshName(renderer);
        if (!string.IsNullOrEmpty(meshName) && _meshArchive.TryGetValue(meshName, out var meshed) && meshed != null)
            return meshed;

        return found;
    }

    private bool TryRestoreFromArchives(TargetState? state, Renderer renderer)
    {
        var originals = LookupArchive(state, renderer);
        if (originals == null)
            return false;

        renderer.sharedMaterials = originals;
        return true;
    }

    private static string SafeName(Renderer renderer)
    {
        try
        {
            return renderer.gameObject.name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetMeshName(Renderer renderer)
    {
        try
        {
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh != null ? skinned.sharedMesh.name : string.Empty;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>按 ProfileId 找尸体对象并收集其全部渲染器（尸体过渡/移交场景）。</summary>
    private static void CollectCorpseRenderers(string profileId, List<Renderer> into)
    {
        if (string.IsNullOrEmpty(profileId))
            return;

        try
        {
            var world = Comfort.Common.Singleton<GameWorld>.Instance;
            var lootItems = world?.LootItems;
            if (lootItems == null)
                return;

            int count = lootItems.Count;
            for (int i = 0; i < count; i++)
            {
                if (lootItems.GetByIndex(i) is not EFT.Interactive.Corpse corpse)
                    continue;

                if (!string.Equals(corpse.PlayerProfileID, profileId, StringComparison.Ordinal))
                    continue;

                var renderers = corpse.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++)
                {
                    var renderer = renderers[r];
                    if (renderer != null && !into.Contains(renderer))
                        into.Add(renderer);
                }

                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"corpse scan failed: {ex.Message}");
        }
    }

    private void OnPlayerDeadOrUnspawn(IPlayer person)
    {
        try
        {
            if (person is Player deadPlayer)
                Restore(deadPlayer); // 死亡瞬间摘剪影（幂等）
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"death-event restore failed: {ex.Message}");
        }
    }

    private void RegisterRenderer(TargetState state, Renderer renderer)
    {
        if (renderer == null || state.Material == null)
            return;

        try
        {
            if (IsWearingCham(renderer))
            {
                // 已带 RZSkills 材质（残留副本/转移）：不采集为原始外观 → 恢复时走档案
                state.Renderers.Add(renderer);
                state.Originals.Add(null);
                state.OriginalOcclusion.Add(true);
                Plugin.Log.LogWarning("[RZSkills] chams: renderer appeared pre-chammed (name match)");
            }
            else
            {
                var originals = renderer.sharedMaterials;
                state.Renderers.Add(renderer);
                state.Originals.Add(originals);
                state.OriginalOcclusion.Add(renderer.allowOcclusionWhenDynamic);

                string name = SafeName(renderer);
                if (!string.IsNullOrEmpty(name) && !state.NameOriginals.ContainsKey(name))
                    state.NameOriginals[name] = originals;
                if (!string.IsNullOrEmpty(name) && !_nameArchive.ContainsKey(name))
                    _nameArchive[name] = originals;

                string meshName = GetMeshName(renderer);
                if (!string.IsNullOrEmpty(meshName) && !_meshArchive.ContainsKey(meshName))
                    _meshArchive[meshName] = originals;

                SetCham(renderer, state.Material);
            }
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"register renderer failed: {ex.Message}");
        }
    }

    /// <summary>取目标需要描影的渲染器：真·身体网格（BodySkins × LoddedSkin.GetRenderers()，含全部 LOD）。</summary>
    private bool FetchRenderers(Player player, out List<Renderer> renderers)
    {
        renderers = _tmpRenderers;
        _tmpRenderers.Clear();

        try
        {
            if (player == null || player.PlayerBody == null)
                return false;

            var bodySkins = player.PlayerBody.BodySkins;
            if (bodySkins != null)
            {
                foreach (var pair in bodySkins)
                {
                    var skin = pair.Value;
                    if (skin == null)
                        continue;

                    foreach (var renderer in skin.GetRenderers())
                    {
                        if (renderer != null && !_tmpRenderers.Contains(renderer))
                            _tmpRenderers.Add(renderer);
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Dbg($"renderer fetch failed: {ex.Message}");
            return false;
        }
    }

    private static void SetCham(Renderer renderer, Material material)
    {
        int slots = Mathf.Max(1, renderer.sharedMaterials.Length);
        var filled = new Material[slots];
        for (int s = 0; s < slots; s++)
            filled[s] = material;

        renderer.sharedMaterials = filled;
        renderer.allowOcclusionWhenDynamic = false;
    }

    /// <summary>按【材质名】识别剪影（实例化副本同名——这是 v7 的核心修复）。</summary>
    private static bool IsChamNamed(Material? material)
    {
        if (material == null)
            return false;

        try
        {
            string name = material.name;
            return !string.IsNullOrEmpty(name) &&
                   name.StartsWith(ChamMaterialPrefix, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWearingCham(Renderer renderer)
    {
        try
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                if (IsChamNamed(materials[i]))
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static Material? CreateMaterial(string name)
    {
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            Plugin.Log.LogError("[RZSkills] 'Hidden/Internal-Colored' shader not found");
            return null;
        }

        var material = new Material(shader)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = 3000,
        };

        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_Cull", (int)CullMode.Back);
        material.SetInt("_ZWrite", 0);
        material.SetInt("_ZTest", (int)CompareFunction.Always);
        return material;
    }

    private static void ApplyColor(Material? material, Color color)
    {
        if (material == null)
            return;

        material.SetColor(
            "_Color",
            new Color(color.r, color.g, color.b, color.a * RzConfig.ChamsOpacity.Value));
    }

    public void Dispose()
    {
        RestoreAll();
        _pending.Clear();

        if (_pmc != null)
            UnityEngine.Object.Destroy(_pmc);
        if (_scav != null)
            UnityEngine.Object.Destroy(_scav);
        if (_boss != null)
            UnityEngine.Object.Destroy(_boss);

        _pmc = _scav = _boss = null;
        _active.Clear();
    }
}
