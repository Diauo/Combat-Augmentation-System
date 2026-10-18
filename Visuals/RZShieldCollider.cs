using System;
using EFT.Ballistics;
using UnityEngine;

namespace CombatAugmentationSystem.Client.Visuals;

/// <summary>
/// 盾面弹道碰撞体：子弹物理拦截——
///  · 主人射击豁免（双层实现：① Shot.IsHitIgnored 谓词补丁；② 本类 IsPenetrated 主人判定）：
///    主人子弹 IsPenetrated 返回 true → 子弹直接穿行，不扣耐久、不闪光、不拦截；
///  · 其余子弹：永不穿透（IsPenetrated = false）→ 子弹停在盾面，游戏按常规命中流程播特效；
///  · 不跳弹（Deflects = false）；
///  · 每次被命中回调给护盾技能（扣耐久 / 受击闪光）。
/// </summary>
internal sealed class RZShieldCollider : BallisticCollider
{
    /// <summary>被子弹命中回调（shot = 来袭弹丸，point = 命中点）。</summary>
    internal Action<Shot, Vector3>? OnBulletImpact;

    /// <summary>主人射击判定（由护盾技能注入）。</summary>
    internal Func<Shot, bool>? OwnerCheck;

    public override bool IsPenetrated(Shot shot, Vector3 hitPoint)
    {
        try
        {
            if (OwnerCheck != null && OwnerCheck(shot))
            {
                var profile = "?";
                try
                {
                    profile = string.IsNullOrEmpty(shot?.PlayerProfileID) ? "<empty>" : shot.PlayerProfileID;
                }
                catch
                {
                }

                Plugin.Dbg($"shield owner-pass: profile='{profile}'");
                return true; // 主人自己的子弹：直接穿行（不拦截、不扣耐久）
            }

            OnBulletImpact?.Invoke(shot, hitPoint);
        }
        catch
        {
            // 拦截流程绝不抛出
        }

        return false; // 永不穿透：子弹停在盾面
    }

    public override bool Deflects(
        float _hitCosDirectionToNormal,
        Shot shot,
        Vector3 hitPoint,
        Vector3 shotNormal,
        Vector3 shotDirection)
    {
        return false; // 不跳弹
    }
}
