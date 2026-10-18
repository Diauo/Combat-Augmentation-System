using System.Reflection;
using EFT;
using CombatAugmentationSystem.Client.Core;
using SPT.Reflection.Patching;

namespace CombatAugmentationSystem.Client.Patches;

/// <summary>进战局后在 GameWorld 上挂载 RaidManager（一次/每局）。</summary>
public class GameStartPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(GameWorld).GetMethod(
            "OnGameStarted",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    [PatchPostfix]
    private static void Postfix(GameWorld __instance)
    {
        if (__instance == null || __instance.gameObject == null)
            return;

        if (__instance.gameObject.GetComponent<RaidManager>() != null)
            return;

        Plugin.Log.LogInfo("[CAS] Game started — attaching RaidManager");
        __instance.gameObject.AddComponent<RaidManager>();
    }
}
