using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using UnityEngine;

namespace CombatAugmentationSystem.Client.Core;

public enum TargetKind
{
    Pmc,
    Scav,
    Boss,
}

/// <summary>扫描仪目标条目：基础快照 + 扫描进度 / 标记状态。</summary>
public sealed class ScannedTarget
{
    public Player Player = null!;
    public TargetKind Kind;
    public bool IsAlive = true;
    public float HealthRatio = 1f;
    public Vector3 Position;

    // ── 扫描仪运行时状态 ──
    public float Distance;          // 相机 → 目标头顶（米）
    public float AimOffsetDeg;      // 瞄准偏差角：镜头朝向与目标方向的夹角（度）
    public Vector2 ScreenPoint;     // 目标头顶的屏幕坐标（像素）
    public bool InBox;              // 是否在扫描框内
    public bool Occluded;           // 视线是否被地形/实体几何遮挡（窗户/围栏等透明层不算）
    public bool Eligible;           // 综合门限：在框内 且 距离 ≤（遮挡 ? 遮挡近限 : 通畅远限）
    public float Progress;          // 已累积扫描时间（秒）
    public float Required;          // 当前距离所需扫描时间（秒）
    public bool InSlot;             // 是否占用扫描进程
    public bool Marked;             // 是否已标记
    public float MarkRemaining;     // 标记剩余（秒）；在框内冻结，离开框才递减
}

/// <summary>目标枚举 / 分类 / 血量读取（扫描仪：无距离与角度限制，框选在技能侧完成）。</summary>
internal static class TargetService
{
    private static readonly EBodyPart[] HealthParts =
    {
        EBodyPart.Head,
        EBodyPart.Chest,
        EBodyPart.Stomach,
        EBodyPart.LeftArm,
        EBodyPart.RightArm,
        EBodyPart.LeftLeg,
        EBodyPart.RightLeg,
    };

    /// <summary>收集全部存活敌人（扫描仪用：距离无限、无锥角）。</summary>
    internal static void CollectEnemies(GameWorld? world, Player? self, List<ScannedTarget> result)
    {
        result.Clear();
        if (world == null || self == null)
            return;

        var players = world.RegisteredPlayers;
        if (players == null)
            return;

        foreach (var person in players)
        {
            try
            {
                if (person is not Player player || player == null || player.IsYourPlayer)
                    continue;

                if (!(player.HealthController?.IsAlive ?? false))
                    continue;

                result.Add(new ScannedTarget
                {
                    Player = player,
                    Kind = Classify(player),
                    IsAlive = true,
                    HealthRatio = GetHealthRatio(player),
                    Position = player.Transform.position,
                });
            }
            catch (System.Exception ex)
            {
                Plugin.Dbg($"target skip: {ex.Message}");
            }
        }
    }

    internal static TargetKind Classify(Player player)
    {
        bool isAi = player.AIData != null && player.AIData.IsAI;

        if (!isAi &&
            (player.Profile.Info.Side == EPlayerSide.Bear ||
             player.Profile.Info.Side == EPlayerSide.Usec))
            return TargetKind.Pmc;

        try
        {
            WildSpawnType role = player.Profile.Info.Settings.Role;
            switch (role)
            {
                case WildSpawnType.assault:
                case WildSpawnType.marksman:
                case WildSpawnType.cursedAssault:
                case WildSpawnType.assaultGroup:
                case WildSpawnType.crazyAssaultEvent:
                    return TargetKind.Scav;
                default:
                    return TargetKind.Boss;
            }
        }
        catch
        {
            return TargetKind.Scav;
        }
    }

    internal static float GetHealthRatio(Player player)
    {
        var hc = player.HealthController;
        if (hc == null)
            return 1f;

        float current = 0f;
        float maximum = 0f;
        for (int i = 0; i < HealthParts.Length; i++)
        {
            var part = hc.GetBodyPartHealth(HealthParts[i], false);
            current += Mathf.Max(0f, part.Current);
            maximum += Mathf.Max(0f, part.Maximum);
        }

        if (maximum <= 0f)
            return 1f;

        return Mathf.Clamp01(current / maximum);
    }
}
