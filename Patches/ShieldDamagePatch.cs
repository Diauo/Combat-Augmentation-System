using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using RZSkills.Client.Skills;
using SPT.Reflection.Patching;

namespace RZSkills.Client.Patches;

/// <summary>
/// 护盾伤害拦截补丁：主玩家即将承受伤害时，先过一遍立场盾判定——
/// 命中盾面（线段与盾面圆盘求交）则把伤害字段清零（保留视听反馈，不再实际扣血）。
/// 只作用于主玩家（LocalPlayer 未覆写 ApplyDamageInfo，基类补丁即可全覆盖）。
/// </summary>
public class ShieldDamagePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo));

    [PatchPrefix]
    private static void Prefix(Player __instance, ref DamageInfo damageInfo)
    {
        var shield = ShieldGate.Active;
        if (shield == null)
            return;

        shield.TryAbsorb(__instance, ref damageInfo);
    }
}
