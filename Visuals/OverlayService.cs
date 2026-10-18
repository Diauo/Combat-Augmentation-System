using System.Collections.Generic;
using EFT;
using CombatAugmentationSystem.Client.Core;
using CombatAugmentationSystem.Client.Inputs;
using CombatAugmentationSystem.Client.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace CombatAugmentationSystem.Client.Visuals;

/// <summary>
/// 战局内 HUD 覆盖层（sortingOrder 32000 的运行时 Canvas）：
///   · 扫描目标：头顶红色倒三角 + 距离（极简风格，无边框/血条/名字）
///   · 技能轮盘：环形扇区 + 高亮 + 选中标记 + 技能名与状态
///   · 底部技能条：当前选中技能 + 冷却进度（任意技能通用）
/// 全部程序绘制，无资源包。
/// 显示门控：仅在【战局内 + 活着 + 可操作】时显示。
/// </summary>
public sealed class OverlayService : System.IDisposable
{
    private const float WheelInnerRadius = 88f;
    private const float WheelOuterRadius = 168f;
    private const float LabelEdgeMarginX = 70f;

    private static readonly Color MarkerRed = new(1f, 0.25f, 0.25f, 0.95f);

    private readonly RaidManager _raid;

    private Canvas? _canvas;
    private RectTransform? _canvasRect;
    private SkillHudGraphic? _graphic;
    private Font? _font;
    private Camera? _cam;
    private bool? _hudShown;

    private readonly List<Text> _labels = new();
    private readonly List<string> _pendingTexts = new();
    private readonly List<Vector2> _pendingPositions = new();
    private readonly List<Color> _pendingColors = new();

    public OverlayService(RaidManager raid)
    {
        _raid = raid;
    }

    public void Update()
    {
        if (!Ensure())
            return;

        bool show = ShouldShow(out string reason);
        if (_hudShown != show)
        {
            _hudShown = show;
            Plugin.Log.LogInfo($"[CAS] HUD {(show ? "shown" : "hidden")} ({reason})");
            _canvas!.gameObject.SetActive(show);
        }

        if (!show)
            return;

        var scan = _raid.Scan;
        var wheel = _raid.Wheel;
        _cam = ResolveCamera();

        _graphic!.Clear();
        _pendingTexts.Clear();
        _pendingPositions.Clear();
        _pendingColors.Clear();

        DrawMarkers(scan);
        DrawWheel(wheel);
        DrawSkillStrip(wheel);

        _graphic.Apply();
        FlushLabels();
    }

    // ── 显示门控：战局 + 活着 + 可操作 ──────────────────────

    private bool ShouldShow(out string reason)
    {
        reason = "in-raid";

        // ① 战局操作者：加载界面 / 主菜单没有 GamePlayerOwner
        Player? owner;
        try
        {
            owner = GamePlayerOwner.MyPlayer;
        }
        catch
        {
            owner = null;
        }

        if (owner == null || owner != _raid.MainPlayer)
        {
            reason = "not-operable";
            return false;
        }

        // ② 活着
        bool alive;
        try
        {
            alive = owner.HealthController != null && owner.HealthController.IsAlive;
        }
        catch
        {
            alive = false;
        }

        if (!alive)
        {
            reason = "dead";
            return false;
        }

        // ③ 可操作：轮盘打开（我们自己接管鼠标）或游戏处于锁鼠标操作态
        if (_raid.Wheel.IsOpen)
        {
            reason = "wheel";
            return true;
        }

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            reason = "cursor-free";
            return false;
        }

        return true;
    }

    // ── 目标标记：扫描框 + 倒三角 + 扫描进度环 ─────────────

    private void DrawMarkers(ScanSkill scan)
    {
        if (!scan.IsActive || _cam == null || _canvasRect == null)
            return;

        DrawScanBox(scan, _cam);

        var targets = scan.Targets;
        if (targets.Count == 0)
            return;

        var canvas = _canvasRect.rect;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.Player == null || !target.IsAlive)
                continue;

            if (!TryGetHeadScreen(target, _cam, canvas, out var headPoint, out float distance))
                continue;

            if (target.Marked)
            {
                // 已标记：红色倒三角 + 距离（尖朝下，悬于头顶上方）
                const float halfWidth = 7f;
                const float topOffset = 5f;
                const float tipOffset = -6f;
                _graphic!.AddTriangle(
                    new Vector2(headPoint.x - halfWidth, headPoint.y + topOffset),
                    new Vector2(headPoint.x + halfWidth, headPoint.y + topOffset),
                    new Vector2(headPoint.x, headPoint.y + tipOffset),
                    MarkerRed);

                if (RzConfig.ScanShowDistance.Value)
                {
                    _pendingTexts.Add($"{distance:0}m");
                    _pendingPositions.Add(new Vector2(headPoint.x, headPoint.y + 18f));
                    _pendingColors.Add(new Color(0.96f, 0.96f, 0.96f, 0.95f));
                }
            }
            else if (target.Progress > 0.001f)
            {
                // 扫描中：头顶下方的进度环
                var ringCenter = new Vector2(headPoint.x, headPoint.y - 14f);
                float ratio = Mathf.Clamp01(target.Progress / Mathf.Max(0.01f, target.Required));

                _graphic!.AddSectorAt(ringCenter, 0f, 360f, 7f, 9.5f, new Color(0.08f, 0.16f, 0.22f, 0.55f));
                if (ratio > 0.01f)
                {
                    _graphic.AddSectorAt(ringCenter, 90f, 90f + 360f * ratio, 7f, 9.5f,
                        new Color(0.25f, 0.9f, 1f, 0.9f));
                }
            }
        }
    }

    // ── 扫描框（角度域 → 按当前 FOV 还原屏幕尺寸：开镜时框会自动变大） ──

    private void DrawScanBox(ScanSkill scan, Camera cam)
    {
        if (_canvasRect == null)
            return;

        float fovTan = Mathf.Tan(Mathf.Clamp(cam.fieldOfView, 5f, 140f) * 0.5f * Mathf.Deg2Rad);
        if (fovTan < 0.001f)
            return;

        var canvas = _canvasRect.rect;
        float halfH = canvas.height * 0.5f * scan.BoxTanHalfY / fovTan;
        float halfW = canvas.height * 0.5f * scan.BoxTanHalfX / fovTan;
        var rect = new Rect(-halfW, -halfH, halfW * 2f, halfH * 2f);

        _graphic!.AddBox(rect, new Color(0.25f, 0.9f, 1f, 0.30f), 1.5f);

        // 四角亮角标
        const float len = 16f;
        const float thick = 2.5f;
        var bright = new Color(0.4f, 1f, 1f, 0.8f);

        _graphic.AddQuad(new Rect(rect.xMin, rect.yMax - len, thick, len), bright);
        _graphic.AddQuad(new Rect(rect.xMin, rect.yMax - thick, len, thick), bright);
        _graphic.AddQuad(new Rect(rect.xMax - thick, rect.yMax - len, thick, len), bright);
        _graphic.AddQuad(new Rect(rect.xMax - len, rect.yMax - thick, len, thick), bright);
        _graphic.AddQuad(new Rect(rect.xMin, rect.yMin, thick, len), bright);
        _graphic.AddQuad(new Rect(rect.xMin, rect.yMin, len, thick), bright);
        _graphic.AddQuad(new Rect(rect.xMax - thick, rect.yMin, thick, len), bright);
        _graphic.AddQuad(new Rect(rect.xMax - len, rect.yMin, len, thick), bright);
    }

    private bool TryGetHeadScreen(
        ScannedTarget target,
        Camera cam,
        Rect canvas,
        out Vector2 screenPos,
        out float distance)
    {
        screenPos = default;
        distance = 0f;

        Vector3 head;
        try
        {
            var player = target.Player;
            var bones = player.PlayerBones;
            head = bones != null
                ? bones.Head.position + Vector3.up * 0.35f
                : player.Transform.position + Vector3.up * 1.95f;
        }
        catch
        {
            return false;
        }

        distance = Vector3.Distance(cam.transform.position, head);

        return WorldToCanvas(cam, canvas, head, out screenPos);
    }

    private static bool WorldToCanvas(Camera cam, Rect canvas, Vector3 world, out Vector2 local)
    {
        local = default;

        var cameraSpace = cam.transform.InverseTransformPoint(world);
        if (cameraSpace.z <= Mathf.Max(0.01f, cam.nearClipPlane + 0.01f))
            return false;

        var viewport = cam.WorldToViewportPoint(world);
        if (float.IsNaN(viewport.x) || float.IsInfinity(viewport.x) ||
            float.IsNaN(viewport.y) || float.IsInfinity(viewport.y))
            return false;

        local = new Vector2(
            canvas.xMin + viewport.x * canvas.width,
            canvas.yMin + viewport.y * canvas.height);
        return true;
    }

    // ── 技能轮盘（仅选择，不触发） ──────────────────────────

    private void DrawWheel(WheelInputController wheel)
    {
        if (!wheel.IsOpen)
            return;

        var skills = wheel.Skills;
        int count = skills.Count;
        if (count == 0)
            return;

        float sector = 360f / count;
        for (int i = 0; i < count; i++)
        {
            float start = 90f - i * sector;
            float end = 90f - (i + 1) * sector;
            bool hot = wheel.HighlightedIndex == i;
            bool selected = wheel.SelectedIndex == i;

            var fill = hot
                ? new Color(0.12f, 0.75f, 0.95f, 0.45f)
                : new Color(0.07f, 0.09f, 0.13f, 0.66f);
            _graphic!.AddSector(start, end, WheelInnerRadius, WheelOuterRadius, fill);

            if (selected)
            {
                _graphic.AddSector(
                    start, end,
                    WheelOuterRadius + 3f, WheelOuterRadius + 7f,
                    new Color(0.15f, 0.9f, 1f, 0.85f));
            }

            float mid = (start + end) * 0.5f;
            float rad = mid * Mathf.Deg2Rad;
            var direction = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            var position = direction * ((WheelInnerRadius + WheelOuterRadius) * 0.5f);

            _pendingTexts.Add(
                $"{(selected ? "● " : string.Empty)}{skills[i].DisplayName}\n{skills[i].StatusText}");
            _pendingPositions.Add(position);
            _pendingColors.Add(hot ? Color.white : new Color(0.85f, 0.88f, 0.92f, 0.85f));
        }

        _graphic!.AddCircle(Vector2.zero, WheelInnerRadius - 6f, new Color(0.05f, 0.06f, 0.08f, 0.88f));
        _pendingTexts.Add(wheel.HighlightedIndex >= 0
            ? Loc.T("松开或点击 = 选择", "Release or click to select")
            : Loc.T("移动鼠标选择", "Move to select"));
        _pendingPositions.Add(Vector2.zero);
        _pendingColors.Add(new Color(0.9f, 0.93f, 1f, 0.9f));
    }

    // ── 底部技能条（任意选中技能通用） ──────────────────────

    private void DrawSkillStrip(WheelInputController wheel)
    {
        if (_canvasRect == null)
            return;

        var canvas = _canvasRect.rect;
        const float chipWidth = 260f;
        const float chipHeight = 26f;
        float centerX = 0f;
        float centerY = canvas.yMin + 46f;

        var background = new Rect(
            centerX - chipWidth * 0.5f,
            centerY - chipHeight * 0.5f,
            chipWidth,
            chipHeight);
        _graphic!.AddQuad(background, new Color(0.04f, 0.05f, 0.07f, 0.55f));

        var selected = wheel.SelectedSkill;
        bool burning = selected != null && selected.IsBurning;

        float progress;
        Color fillColor;
        if (selected != null && selected.IsActive)
        {
            progress = 1f;
            fillColor = new Color(0.1f, 0.8f, 1f, 0.5f);
        }
        else if (selected != null && selected.CooldownRemaining > 0f)
        {
            progress = 1f - Mathf.Clamp01(
                selected.CooldownRemaining / Mathf.Max(0.01f, selected.CooldownTotal));
            fillColor = new Color(0.95f, 0.6f, 0.12f, 0.5f);
        }
        else
        {
            progress = 1f;
            fillColor = new Color(0.15f, 0.9f, 0.4f, 0.35f);
        }

        if (burning)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
            fillColor = new Color(0.2f, 0.85f, 1f, 0.35f + 0.3f * pulse);
            _graphic.AddBox(background, new Color(0.25f, 0.9f, 1f, 0.45f + 0.45f * pulse), 1.5f);
        }

        _graphic.AddQuad(
            new Rect(
                background.xMin + 1f,
                background.yMin + 1f,
                Mathf.Max(0f, (background.width - 2f) * progress),
                background.height - 2f),
            fillColor);

        string name = selected != null ? selected.DisplayName : "—";
        string label = $"[{RzConfig.SkillKey.Value}] {name}";
        if (selected != null && !selected.Ready)
            label += $" | {selected.StatusText}";
        _pendingTexts.Add(label);
        _pendingPositions.Add(new Vector2(centerX, centerY));
        _pendingColors.Add(burning ? new Color(0.6f, 0.95f, 1f) : new Color(0.92f, 0.95f, 1f));
    }

    // ── 标签池 ──────────────────────────────────────────────

    private void FlushLabels()
    {
        while (_labels.Count < _pendingTexts.Count)
            _labels.Add(CreateLabel());

        for (int i = 0; i < _labels.Count; i++)
        {
            var label = _labels[i];
            bool used = i < _pendingTexts.Count;

            if (label.gameObject.activeSelf != used)
                label.gameObject.SetActive(used);
            if (!used)
                continue;

            if (label.text != _pendingTexts[i])
                label.text = _pendingTexts[i];
            label.color = _pendingColors[i];

            var rect = (RectTransform)label.transform;
            var position = _pendingPositions[i];
            if (_canvasRect != null)
            {
                var bounds = _canvasRect.rect;
                position.x = Mathf.Clamp(position.x, bounds.xMin + LabelEdgeMarginX, bounds.xMax - LabelEdgeMarginX);
                position.y = Mathf.Clamp(position.y, bounds.yMin + 14f, bounds.yMax - 14f);
            }

            rect.anchoredPosition = position;
        }
    }

    private Text CreateLabel()
    {
        var go = new GameObject("CombatAugmentationSystem Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
        go.transform.SetParent(_canvasRect, false);

        var label = go.GetComponent<Text>();
        label.font = _font;
        label.fontSize = 13;
        label.alignment = TextAnchor.MiddleCenter;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        label.raycastTarget = false;

        var outline = go.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
        outline.effectDistance = new Vector2(1f, -1f);

        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(380f, 56f);
        return label;
    }

    // ── 基建 ────────────────────────────────────────────────

    private bool Ensure()
    {
        if (_canvas != null && _graphic != null)
            return true;

        try
        {
            var canvasGo = new GameObject("CombatAugmentationSystem Overlay", typeof(RectTransform), typeof(Canvas));
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 32000;
            _canvasRect = (RectTransform)canvasGo.transform;

            var meshGo = new GameObject("HUD Mesh", typeof(RectTransform), typeof(CanvasRenderer), typeof(SkillHudGraphic));
            meshGo.transform.SetParent(_canvasRect, false);
            var meshRect = (RectTransform)meshGo.transform;
            meshRect.anchorMin = Vector2.zero;
            meshRect.anchorMax = Vector2.one;
            meshRect.offsetMin = Vector2.zero;
            meshRect.offsetMax = Vector2.zero;

            _graphic = meshGo.GetComponent<SkillHudGraphic>();
            _graphic.raycastTarget = false;

            _font = LoadFont();
            Plugin.Log.LogInfo("[CAS] HUD overlay created");
            return true;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[CAS] overlay init failed: {ex}");
            return false;
        }
    }

    private Camera? ResolveCamera()
    {
        _cam = CameraResolver.Resolve();
        return _cam;
    }

    private static Font? LoadFont()
    {
        Font? font = null;
        try
        {
            font = Font.CreateDynamicFontFromOSFont("Segoe UI", 14);
        }
        catch
        {
            // ignore
        }

        if (font == null)
        {
            try
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            catch
            {
                // ignore
            }
        }

        if (font == null)
        {
            try
            {
                font = Font.CreateDynamicFontFromOSFont("Arial", 14);
            }
            catch
            {
                // ignore
            }
        }

        return font;
    }

    public void Dispose()
    {
        try
        {
            if (_canvas != null)
                UnityEngine.Object.Destroy(_canvas.gameObject);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CAS] overlay dispose failed: {ex.Message}");
        }

        _canvas = null;
        _canvasRect = null;
        _graphic = null;
        _labels.Clear();
    }
}
