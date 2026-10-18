using UnityEngine;

namespace CombatAugmentationSystem.Client.Core;

/// <summary>
/// 统一相机解析：优先 "FPS Camera"（不要求其处于激活状态——开镜等瞬间状态切换时仍有效），
/// 失败时退回 Camera.main。缓存失效自动重解析。
/// </summary>
internal static class CameraResolver
{
    private static Camera? _cached;

    internal static Camera? Resolve()
    {
        if (_cached != null)
            return _cached;

        try
        {
            var go = GameObject.Find("FPS Camera");
            if (go != null)
                _cached = go.GetComponent<Camera>();
        }
        catch
        {
        }

        if (_cached == null)
            _cached = Camera.main;

        return _cached;
    }

    internal static void Reset() => _cached = null;
}
