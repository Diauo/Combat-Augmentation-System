using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using RZSkills.Client.Skills;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RZSkills.Client.Patches;

/// <summary>
/// 护盾单向穿透（实例路径）：Shot.IsHitIgnored 命中过滤——
/// 主玩家自己的射击忽略盾面碰撞体（敌人子弹正常命中盾面）。
/// </summary>
public class ShieldPassThroughPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Shot), "IsHitIgnored", new[] { typeof(RaycastHit) });

    [PatchPostfix]
    private static void Postfix(Shot __instance, RaycastHit hit, ref bool __result)
    {
        if (__result)
            return;

        var gate = ShieldGate.Active;
        if (gate == null)
            return;

        if (!gate.IsShieldCollider(hit.collider))
            return;

        if (gate.IsShieldCollider(hit.collider) && gate.IsOwnerShot(__instance))
        {
            Plugin.Dbg($"shield predicate ignore (instance): {__instance.PlayerProfileID}");
            __result = true;
        }
    }
}

/// <summary>
/// 护盾单向穿透（终极保险）：Shot.CheckCollision 谓词包裹——
/// 在子弹碰撞扫描入口把主人的射击从"命中过滤"中直接剔除（不依赖 Shot.IsHitIgnored 的委托路径）。
/// 仅在护盾激活期间包裹，其余时间零开销。
/// </summary>
public class ShieldCheckCollisionPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Shot), "CheckCollision",
            new[] { typeof(Vector3), typeof(Vector3), typeof(LayerMask), typeof(LayerMask), typeof(Func<RaycastHit, bool>) });

    [PatchPrefix]
    private static void Prefix(Shot __instance, ref Func<RaycastHit, bool> isHitIgnoredTest)
    {
        var gate = ShieldGate.Active;
        if (gate == null || !gate.PassThroughArmed)
            return;

        var original = isHitIgnoredTest;
        var shot = __instance;

        isHitIgnoredTest = hit =>
        {
            try
            {
                if (original != null && original(hit))
                    return true;
            }
            catch
            {
            }

            try
            {
                return gate.IsShieldCollider(hit.collider) && gate.IsOwnerShot(shot);
            }
            catch
            {
                return false;
            }
        };
    }
}

/// <summary>
/// 护盾单向穿透（静态路径）：同上，通过桥接身份比对（主玩家桥接引用）。
/// </summary>
public class ShieldPassThroughStaticPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Shot), "IsHitIgnored", new[] { typeof(IObserverToPlayerBridge), typeof(RaycastHit), typeof(bool) });

    [PatchPostfix]
    private static void Postfix(IObserverToPlayerBridge player, RaycastHit hit, ref bool __result)
    {
        if (__result)
            return;

        var gate = ShieldGate.Active;
        if (gate == null)
            return;

        if (!gate.IsShieldCollider(hit.collider))
            return;

        if (gate.IsShieldCollider(hit.collider) && gate.IsOwnerShooter(player))
        {
            Plugin.Dbg("shield predicate ignore (static-bridge)");
            __result = true;
        }
    }
}
