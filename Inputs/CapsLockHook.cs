using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CombatAugmentationSystem.Client.Inputs;

/// <summary>
/// 进程内低级键盘钩子 —— 只处理 CapsLock 一个键：
///  · 提供可靠的按住/松开状态（Unity 对 CapsLock 的支持不稳定）；
///  · 可选吞掉事件，防止每次开轮盘都切换大写状态。
/// 安全规则：只在游戏窗口位于前台时拦截；出任何异常一律放行；退出即失效。
/// </summary>
internal static class CapsLockHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_CAPITAL = 0x14;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static IntPtr _hook = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    private static IntPtr _gameWindow = IntPtr.Zero;

    internal static bool Installed => _hook != IntPtr.Zero;

    internal static bool IsCapsHeld { get; private set; }

    internal static bool SwallowToggle { get; set; } = true;

    internal static bool Install()
    {
        if (Installed)
            return true;

        try
        {
            var module = Process.GetCurrentProcess().MainModule;
            IntPtr hMod = module != null ? GetModuleHandle(module.ModuleName) : IntPtr.Zero;
            _gameWindow = Process.GetCurrentProcess().MainWindowHandle;

            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);

            if (_hook == IntPtr.Zero)
                Plugin.Log.LogWarning("[CAS] CapsLock hook install failed (Win32)");
            else
                Plugin.Dbg("CapsLock hook installed");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CAS] CapsLock hook exception: {ex.Message}");
            _hook = IntPtr.Zero;
        }

        return Installed;
    }

    internal static void Uninstall()
    {
        try
        {
            if (_hook != IntPtr.Zero)
                UnhookWindowsHookEx(_hook);
        }
        catch
        {
            // ignore — fail-open policy
        }

        _hook = IntPtr.Zero;
        _proc = null;
        IsCapsHeld = false;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int vk = Marshal.ReadInt32(lParam, 0);
                if (vk == VK_CAPITAL)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (down)
                        IsCapsHeld = true;
                    else if (up)
                        IsCapsHeld = false;

                    bool foreground = _gameWindow != IntPtr.Zero &&
                                      GetForegroundWindow() == _gameWindow;
                    if (SwallowToggle && foreground)
                        return new IntPtr(1); // 吞掉事件：不切换大写状态
                }
            }
        }
        catch
        {
            // fail-open: never block input on error
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
