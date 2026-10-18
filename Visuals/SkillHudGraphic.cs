using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CombatAugmentationSystem.Client.Visuals;

/// <summary>
/// 技能 HUD 单网格绘制器：一次 OnPopulateMesh 画完
///   · 目标标记（倒三角 / 扫描进度环）
///   · 扫描框（正方形取景框）
///   · 技能轮盘（环形扇区 + 中心圆）
///   · 底部技能条（冷却进度）
/// 全部几何程序生成，无资源包。OverlayService 每帧 Clear→填充→Apply。
/// </summary>
public sealed class SkillHudGraphic : Graphic
{
    public struct BoxCmd
    {
        public Rect Rect;
        public Color Color;
        public float Thickness;
    }

    public struct QuadCmd
    {
        public Rect Rect;
        public Color Color;
    }

    public struct SectorCmd
    {
        public Vector2 Center;
        public float StartDeg;
        public float EndDeg;
        public float InnerRadius;
        public float OuterRadius;
        public Color Color;
    }

    public struct CircleCmd
    {
        public Vector2 Center;
        public float Radius;
        public Color Color;
    }

    public struct TriCmd
    {
        public Vector2 A;
        public Vector2 B;
        public Vector2 C;
        public Color Color;
    }

    private readonly List<BoxCmd> _boxes = new();
    private readonly List<QuadCmd> _quads = new();
    private readonly List<SectorCmd> _sectors = new();
    private readonly List<CircleCmd> _circles = new();
    private readonly List<TriCmd> _tris = new();

    public void Clear()
    {
        _boxes.Clear();
        _quads.Clear();
        _sectors.Clear();
        _circles.Clear();
        _tris.Clear();
    }

    public void AddBox(Rect rect, Color color, float thickness) =>
        _boxes.Add(new BoxCmd { Rect = rect, Color = color, Thickness = thickness });

    public void AddQuad(Rect rect, Color color) =>
        _quads.Add(new QuadCmd { Rect = rect, Color = color });

    public void AddSector(float startDeg, float endDeg, float innerR, float outerR, Color color) =>
        AddSectorAt(Vector2.zero, startDeg, endDeg, innerR, outerR, color);

    public void AddSectorAt(Vector2 center, float startDeg, float endDeg, float innerR, float outerR, Color color) =>
        _sectors.Add(new SectorCmd
        {
            Center = center,
            StartDeg = startDeg,
            EndDeg = endDeg,
            InnerRadius = innerR,
            OuterRadius = outerR,
            Color = color,
        });

    public void AddCircle(Vector2 center, float radius, Color color) =>
        _circles.Add(new CircleCmd { Center = center, Radius = radius, Color = color });

    public void AddTriangle(Vector2 a, Vector2 b, Vector2 c, Color color) =>
        _tris.Add(new TriCmd { A = a, B = b, C = c, Color = color });

    public void Apply() => SetVerticesDirty();

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        for (int i = 0; i < _quads.Count; i++)
            AddQuad(vh, _quads[i].Rect, _quads[i].Color);

        for (int i = 0; i < _boxes.Count; i++)
        {
            var b = _boxes[i];
            var r = b.Rect;
            float t = Mathf.Max(1f, b.Thickness);
            AddQuad(vh, new Rect(r.xMin, r.yMin, r.width, t), b.Color);
            AddQuad(vh, new Rect(r.xMin, r.yMax - t, r.width, t), b.Color);
            AddQuad(vh, new Rect(r.xMin, r.yMin, t, r.height), b.Color);
            AddQuad(vh, new Rect(r.xMax - t, r.yMin, t, r.height), b.Color);
        }

        for (int i = 0; i < _sectors.Count; i++)
            AddSector(vh, _sectors[i]);

        for (int i = 0; i < _circles.Count; i++)
            AddCircle(vh, _circles[i]);

        for (int i = 0; i < _tris.Count; i++)
            AddTriangle(vh, _tris[i]);
    }

    private static void AddQuad(VertexHelper vh, Rect rect, Color color)
    {
        if (rect.width <= 0f || rect.height <= 0f)
            return;

        int start = vh.currentVertCount;
        vh.AddVert(new Vector3(rect.xMin, rect.yMin), color, Vector2.zero);
        vh.AddVert(new Vector3(rect.xMin, rect.yMax), color, Vector2.zero);
        vh.AddVert(new Vector3(rect.xMax, rect.yMax), color, Vector2.zero);
        vh.AddVert(new Vector3(rect.xMax, rect.yMin), color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }

    private static void AddTriangle(VertexHelper vh, TriCmd cmd)
    {
        int start = vh.currentVertCount;
        vh.AddVert(cmd.A, cmd.Color, Vector2.zero);
        vh.AddVert(cmd.B, cmd.Color, Vector2.zero);
        vh.AddVert(cmd.C, cmd.Color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
    }

    private static void AddSector(VertexHelper vh, SectorCmd cmd)
    {
        float span = Mathf.Abs(cmd.EndDeg - cmd.StartDeg);
        int segments = Mathf.Max(2, Mathf.CeilToInt(span / 6f));
        float step = (cmd.EndDeg - cmd.StartDeg) / segments;

        Vector2 prevOuter = Vector2.zero;
        Vector2 prevInner = Vector2.zero;
        for (int i = 0; i <= segments; i++)
        {
            float rad = (cmd.StartDeg + step * i) * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector2 outer = cmd.Center + dir * cmd.OuterRadius;
            Vector2 inner = cmd.Center + dir * cmd.InnerRadius;

            if (i > 0)
            {
                int v = vh.currentVertCount;
                vh.AddVert(prevInner, cmd.Color, Vector2.zero);
                vh.AddVert(prevOuter, cmd.Color, Vector2.zero);
                vh.AddVert(outer, cmd.Color, Vector2.zero);
                vh.AddVert(inner, cmd.Color, Vector2.zero);
                vh.AddTriangle(v, v + 1, v + 2);
                vh.AddTriangle(v, v + 2, v + 3);
            }

            prevOuter = outer;
            prevInner = inner;
        }
    }

    private static void AddCircle(VertexHelper vh, CircleCmd cmd)
    {
        const int segments = 32;
        int center = vh.currentVertCount;
        vh.AddVert(cmd.Center, cmd.Color, Vector2.zero);
        for (int i = 0; i <= segments; i++)
        {
            float rad = i * Mathf.PI * 2f / segments;
            vh.AddVert(
                cmd.Center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * cmd.Radius,
                cmd.Color,
                Vector2.zero);
        }

        for (int i = 0; i < segments; i++)
            vh.AddTriangle(center, center + 1 + i, center + 2 + i);
    }
}
