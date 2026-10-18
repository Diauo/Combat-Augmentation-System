using System;
using System.Reflection;
using EFT;
using EFT.InputSystem;
using HarmonyLib;
using CombatAugmentationSystem.Client.Inputs;
using SPT.Reflection.Patching;

namespace CombatAugmentationSystem.Client.Patches;

/// <summary>轮盘展开时：拦掉全部游戏操作指令（防误射 / 误交互）。</summary>
public class WheelCommandBlockPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateCommand));

    [PatchPrefix]
    private static bool Prefix(ref InputNode.ETranslateResult __result)
    {
        if (!WheelGate.IsOpen)
            return true;

        __result = InputNode.ETranslateResult.BlockAll;
        return false;
    }
}

/// <summary>
/// 轮盘展开时：只冻结视角/转向轴（防视角乱转），**保留移动轴**——人物可边开轮盘边移动。
/// 轴语义（源自 Class1728.TranslateAxes）：[0,1]=移动  [2,3]=转向  [4,5]=视角  [6]=侧身。
/// </summary>
public class WheelAxesBlockPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateAxes));

    [PatchPrefix]
    private static bool Prefix(ref float[] axes)
    {
        if (!WheelGate.IsOpen)
            return true;

        if (axes != null)
        {
            for (int i = 2; i <= 5 && i < axes.Length; i++)
                axes[i] = 0f;
        }

        return true; // 不跳过原逻辑：移动（[0,1]）照常生效
    }
}
