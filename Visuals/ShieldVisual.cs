using System;
using EFT.Ballistics;
using EFT.NetworkPackets;
using CombatAugmentationSystem.Client.Core;
using UnityEngine;

namespace CombatAugmentationSystem.Client.Visuals;

/// <summary>
/// 世界空间六角蜂窝盾面（v6：1:3 三面结构）：
///  · 结构 = 主盾（正前）+ 左右两片 45° 斜翼（铰接于主盾两侧边缘、向后 45° 收拢）——
///    防御前方与侧向射弹，形成半包围护罩；
///  · 每片 = 程序生成蜂窝网格（亮边环 + 半透明内芯）+ 实体填充碰撞体（外扩 30%，无缝）；
///  · 基础尺寸 1m，配置缩放（root 缩放，整个结构同步）；材质共享（受击闪白/耐久变色统一）；
///  · 跟随为瞬时（无延迟）。
/// </summary>
internal sealed class ShieldVisual : IDisposable
{
    internal const float BasePanelSize = 1f;

    private const float CurveRadius = 2.6f;
    private const float CellRadius = 0.088f;
    private const float VisualCellScale = 0.9f;
    private const float ColliderCellScale = 1.02f;
    private const float ColliderClusterScale = 1.3f;
    private const float WingOffset = 0.7071f; // sin/cos 45°

    private static readonly Color BaseColor = new(0.28f, 0.82f, 1f, 0.5f);
    private static readonly Color AlertColor = new(1f, 0.35f, 0.30f, 0.54f);

    private struct PanelRef
    {
        public Transform Transform;
        public MeshCollider Collider;
        public RZShieldCollider Ballistic;
    }

    private readonly System.Collections.Generic.List<PanelRef> _panels = new();

    private GameObject? _root;
    private Transform? _transform;
    private Material? _material;
    private Mesh? _visualMesh;
    private Mesh? _colliderMesh;

    private float _flash;
    private float _durabilityRatio = 1f;

    internal int PanelCount => _panels.Count;

    internal void Ensure()
    {
        if (_root != null)
            return;

        _root = new GameObject("CombatAugmentationSystem Shield");
        _root.layer = ShieldLayers.Layer >= 0 ? ShieldLayers.Layer : 0;
        _transform = _root.transform;
        _panels.Clear();

        _visualMesh = BuildHoneycombMesh(BasePanelSize * 0.5f, VisualCellScale, detailed: true);
        _colliderMesh = BuildHoneycombMesh(BasePanelSize * 0.5f * ColliderClusterScale, ColliderCellScale, detailed: false);
        _material = BuildMaterial();

        float w = BasePanelSize * 0.5f;

        // 主盾：正前方
        CreatePanel("RZ Shield C", Vector3.zero, Vector3.forward);

        // 左翼：铰接于主盾左边缘，向后 45°（法线指向左前 45°）
        CreatePanel(
            "RZ Shield L",
            new Vector3(-w, 0f, 0f) + new Vector3(-WingOffset, 0f, -WingOffset) * w,
            new Vector3(-WingOffset, 0f, WingOffset));

        // 右翼：铰接于主盾右边缘，向后 45°（法线指向右前 45°）
        CreatePanel(
            "RZ Shield R",
            new Vector3(w, 0f, 0f) + new Vector3(WingOffset, 0f, -WingOffset) * w,
            new Vector3(WingOffset, 0f, WingOffset));
    }

    private void CreatePanel(string name, Vector3 localPosition, Vector3 forward)
    {
        var go = new GameObject(name);
        go.layer = _root!.layer;
        go.transform.SetParent(_transform, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);

        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = _visualMesh;

        var meshCollider = go.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = _colliderMesh;
        meshCollider.convex = false;

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.sharedMaterial = _material;

        var ballistic = go.AddComponent<RZShieldCollider>();
        ballistic.TypeOfMaterial = MaterialType.MetalThick;
        ballistic.HitType = EHitType.Default;
        ballistic.PenetrationLevel = 99999f;
        ballistic.PenetrationChance = 0f;
        ballistic.RicochetChance = 0f;
        ballistic.FragmentationChance = 0f;
        ballistic.TrajectoryDeviationChance = 0f;
        ballistic.TrajectoryDeviation = 0f;
        ballistic.Associate(MaterialType.MetalThick);

        _panels.Add(new PanelRef { Transform = go.transform, Collider = meshCollider, Ballistic = ballistic });
    }

    /// <summary>为全部盾面设置弹道命中回调。</summary>
    internal void SetImpactHandler(Action<Shot, Vector3> handler)
    {
        for (int i = 0; i < _panels.Count; i++)
        {
            if (_panels[i].Ballistic != null)
                _panels[i].Ballistic.OnBulletImpact = handler;
        }
    }

    /// <summary>为全部盾面设置主人射击判定（第二层穿透保险）。</summary>
    internal void SetOwnerCheck(Func<Shot, bool> check)
    {
        for (int i = 0; i < _panels.Count; i++)
        {
            if (_panels[i].Ballistic != null)
                _panels[i].Ballistic.OwnerCheck = check;
        }
    }

    /// <summary>命中体是否属于本盾（任意一片）。</summary>
    internal bool MatchesCollider(Collider collider)
    {
        for (int i = 0; i < _panels.Count; i++)
        {
            var panel = _panels[i];
            if (panel.Collider == null || panel.Ballistic == null)
                continue;

            if (collider == panel.Collider)
                return true;

            if (collider.GetComponentInParent<RZShieldCollider>() == panel.Ballistic)
                return true;
        }

        return false;
    }

    /// <summary>取第 index 片盾面的世界平面（中心 + 法线）。</summary>
    internal void GetPanelPlane(int index, out Vector3 center, out Vector3 normal)
    {
        center = Vector3.zero;
        normal = Vector3.forward;

        if (index < 0 || index >= _panels.Count)
            return;

        var transform = _panels[index].Transform;
        if (transform == null)
            return;

        center = transform.position;
        normal = transform.forward;
    }

    /// <summary>整体结构缩放（米，主盾外接直径；整组同步缩放）。</summary>
    internal void SetPanelScale(float sizeMetres)
    {
        if (_transform == null)
            return;

        float s = Mathf.Clamp(sizeMetres, 0.4f, 3f) / BasePanelSize;
        _transform.localScale = new Vector3(s, s, s);
    }

    internal void SetVisible(bool visible)
    {
        if (_root != null && _root.activeSelf != visible)
            _root.SetActive(visible);
    }

    internal void SnapTo(Vector3 position, Quaternion rotation)
    {
        if (_transform != null)
            _transform.SetPositionAndRotation(position, rotation);
    }

    internal void Flash() => _flash = 1f;

    internal void SetDurabilityRatio(float ratio) => _durabilityRatio = Mathf.Clamp01(ratio);

    internal void Tick(float deltaTime)
    {
        if (_material == null)
            return;

        _flash = Mathf.Max(0f, _flash - deltaTime * 3.5f);

        var color = Color.Lerp(BaseColor, AlertColor, Mathf.Clamp01(1f - _durabilityRatio * 1.4f));
        color.a = 1f;
        float alpha = Mathf.Clamp01(RzConfig.ShieldOpacity.Value) * (0.85f + 0.15f * Mathf.Sin(Time.unscaledTime * 5f));

        if (_durabilityRatio < 0.35f)
            alpha *= 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 22f); // 耐久告急：抖动

        alpha += _flash * 0.35f;
        color.a = Mathf.Clamp01(alpha);
        color = Color.Lerp(color, Color.white, _flash * 0.6f);
        _material.color = color;
    }

    // ── 蜂窝网格生成 ────────────────────────────────────────

    private static Mesh BuildHoneycombMesh(float clusterRadius, float cellScale, bool detailed)
    {
        float rowStep = Mathf.Sqrt(3f) * CellRadius;
        float colStep = 1.5f * CellRadius;
        int colRange = Mathf.CeilToInt(clusterRadius / colStep) + 1;
        int rowRange = Mathf.CeilToInt(clusterRadius / rowStep) + 1;

        var vertices = new System.Collections.Generic.List<Vector3>(512);
        var colors = new System.Collections.Generic.List<Color>(512);
        var triangles = new System.Collections.Generic.List<int>(2048);

        for (int col = -colRange; col <= colRange; col++)
        {
            float cx = col * colStep;
            float yOffset = ((col & 1) != 0) ? rowStep * 0.5f : 0f;

            for (int row = -rowRange; row <= rowRange; row++)
            {
                float cy = row * rowStep + yOffset;
                float dist = Mathf.Sqrt(cx * cx + cy * cy);
                if (dist > clusterRadius - CellRadius * 0.2f)
                    continue;

                float ang = cx / CurveRadius;
                float z = (Mathf.Cos(ang) - 1f) * CurveRadius; // 沿弧面排列（轻弧）
                AddCell(vertices, colors, triangles,
                    new Vector3(cx, cy, z), cellScale, col, row, Mathf.Clamp01(dist / clusterRadius), detailed);
            }
        }

        var mesh = new Mesh { name = "RZ Shield Honeycomb Mesh" };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddCell(
        System.Collections.Generic.List<Vector3> vertices,
        System.Collections.Generic.List<Color> colors,
        System.Collections.Generic.List<int> triangles,
        Vector3 center, float scale, int col, int row, float distRatio, bool detailed)
    {
        float outer = CellRadius * scale;
        float tone = Hash01(col, row);

        if (!detailed)
        {
            // 实体格（碰撞体用）：单层六边形填充
            int b = vertices.Count;
            vertices.Add(center);
            colors.Add(Color.white);
            for (int k = 0; k < 6; k++)
            {
                float a = k * 60f * Mathf.Deg2Rad;
                vertices.Add(center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * outer);
                colors.Add(Color.white);
            }

            for (int k = 0; k < 6; k++)
            {
                triangles.Add(b);
                triangles.Add(b + 1 + k);
                triangles.Add(b + 1 + (k + 1) % 6);
            }

            return;
        }

        // 细节格（视觉用）：亮边环 + 半透明内芯
        float inner = outer * 0.84f;
        float edgeAlpha = Mathf.Clamp01(Mathf.Lerp(0.9f, 1f, distRatio));
        float coreAlpha = Mathf.Clamp01((0.32f + 0.3f * tone) * Mathf.Lerp(1.15f, 0.85f, distRatio));

        int b0 = vertices.Count;

        for (int k = 0; k < 6; k++)
        {
            float a = k * 60f * Mathf.Deg2Rad;
            vertices.Add(center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * outer);
            colors.Add(new Color(1f, 1f, 1f, edgeAlpha));
        }

        for (int k = 0; k < 6; k++)
        {
            float a = k * 60f * Mathf.Deg2Rad;
            vertices.Add(center + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * inner);
            colors.Add(new Color(1f, 1f, 1f, edgeAlpha));
        }

        vertices.Add(center);
        colors.Add(new Color(1f, 1f, 1f, coreAlpha));
        int coreIndex = b0 + 12;

        for (int k = 0; k < 6; k++)
        {
            int o0 = b0 + k;
            int o1 = b0 + (k + 1) % 6;
            int i0 = b0 + 6 + k;
            int i1 = b0 + 6 + (k + 1) % 6;

            triangles.Add(o0);
            triangles.Add(i0);
            triangles.Add(o1);
            triangles.Add(o1);
            triangles.Add(i0);
            triangles.Add(i1);
        }

        for (int k = 0; k < 6; k++)
        {
            triangles.Add(coreIndex);
            triangles.Add(b0 + 6 + k);
            triangles.Add(b0 + 6 + (k + 1) % 6);
        }
    }

    private static float Hash01(int a, int b)
    {
        unchecked
        {
            int h = (a * 73856093) ^ (b * 19349663);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }
    }

    // ── 材质 ───────────────────────────────────────────────

    private static Material BuildMaterial()
    {
        Shader? shader = null;
        string[] candidates =
        {
            "Sprites/Default",
            "Unlit/Transparent",
            "Legacy Shaders/Transparent/Diffuse",
            "Hidden/Internal-Colored",
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            shader = Shader.Find(candidates[i]);
            if (shader != null)
                break;
        }

        if (shader == null)
            shader = Shader.Find("Standard");

        var material = new Material(shader) { name = "RZ Shield Field", color = BaseColor };
        if (material.HasProperty("_MainTex"))
            material.mainTexture = Texture2D.whiteTexture;

        return material;
    }

    public void Dispose()
    {
        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root);
            _root = null;
            _transform = null;
        }

        if (_material != null)
        {
            UnityEngine.Object.Destroy(_material);
            _material = null;
        }

        if (_visualMesh != null)
        {
            UnityEngine.Object.Destroy(_visualMesh);
            _visualMesh = null;
        }

        if (_colliderMesh != null)
        {
            UnityEngine.Object.Destroy(_colliderMesh);
            _colliderMesh = null;
        }

        _panels.Clear();
    }
}
