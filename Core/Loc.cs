using System;
using Comfort.Common;
using EFT.Settings;

namespace RZSkills.Client.Core;

/// <summary>
/// 界面文案语言：跟随游戏界面语言（"ch" = 中文，其余回退英文）。
/// 用法：Loc.T("中文文案", "English text")。
/// </summary>
internal static class Loc
{
    /// <summary>游戏界面语言是否为中文（读取失败回退英文）。</summary>
    internal static bool IsChinese
    {
        get
        {
            try
            {
                var language = Singleton<SettingsManager>.Instance?.Game?.Settings?.Language?.Value;
                return string.Equals(language, "ch", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>按游戏语言二选一。</summary>
    internal static string T(string zh, string en) => IsChinese ? zh : en;
}
