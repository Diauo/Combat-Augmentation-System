using System;
using System.Reflection;
using UnityEngine;

namespace CombatAugmentationSystem.Client.Core;

/// <summary>
/// 护盾弹道图层：把一个空闲图层注册进子弹的 HitMask（LayerMasksData 的静态字段），
/// 盾面放该图层即可被弹道射线命中；视野/交互掩码不受影响（bot 仍能看见你）。
/// 失败时降级为 HighPolyCollider 图层。
/// </summary>
internal static class ShieldLayers
{
    internal static int Layer { get; private set; } = -1;

    internal static void Setup()
    {
        try
        {
            int layer = FindFreeLayer();
            if (layer < 0)
            {
                Fallback("no free layer");
                return;
            }

            if (!TryFindHitMaskField(out var field))
            {
                Fallback("HitMask field not found");
                return;
            }

            var original = (LayerMask)field.GetValue(null);
            field.SetValue(null, (LayerMask)(original.value | (1 << layer)));

            Layer = layer;
            Plugin.Log.LogInfo($"[CAS] shield ballistics layer {layer} registered into HitMask");
        }
        catch (Exception ex)
        {
            Fallback(ex.Message);
        }
    }

    private static void Fallback(string reason)
    {
        Layer = LayerMask.NameToLayer("HighPolyCollider");
        if (Layer < 0)
            Layer = 0;
        Plugin.Log.LogInfo($"[CAS] shield collider layer: {Layer} (HighPolyCollider; {reason})");
    }

    private static int FindFreeLayer()
    {
        for (int i = 31; i >= 8; i--)
        {
            if (i == 2)
                continue;
            if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                return i;
        }

        return -1;
    }

    private static bool TryFindHitMaskField(out FieldInfo field)
    {
        field = null!;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.Name.IndexOf("LayerMasksData", StringComparison.Ordinal) < 0)
                    continue;

                var candidate = type.GetField("HitMask", BindingFlags.Public | BindingFlags.Static);
                if (candidate != null && candidate.FieldType == typeof(LayerMask))
                {
                    field = candidate;
                    return true;
                }
            }
        }

        return false;
    }
}
