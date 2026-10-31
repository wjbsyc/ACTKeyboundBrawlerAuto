using System;
using System.Runtime.InteropServices;

namespace ACTKeyboundBrawlerAuto
{
    /// <summary>
    /// 低级键盘钩子：只记录“用户物理按键”，忽略 SendInput 注入的按键。
    /// 用于在玩家手动输入/使用 IME 时暂停自动输入，避免双方同时写同一个输入框。
    /// </summary>
    public sealed class UserKeyboardMonitor : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int LLKHF_INJECTED = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly HookProc _proc;
        private IntPtr _hook;
        private long _manualInputUntilTicks;

        public UserKeyboardMonitor()
        {
            _proc = KeyboardHookProc;
            _hook = SetWindowsHookEx(
                WH_KEYBOARD_LL, _proc,
                GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException(
                    "SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }

        public bool IsPaused
        {
            get
            {
                long until = System.Threading.Interlocked.Read(ref _manualInputUntilTicks);
                return DateTime.UtcNow.Ticks < until;
            }
        }

        private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = wParam.ToInt32();
                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                {
                    var data = (KbdLlHookStruct)Marshal.PtrToStructure(
                        lParam, typeof(KbdLlHookStruct));

                    // SendInput 注入的事件带 LLKHF_INJECTED，不算用户手动输入。
                    if ((data.Flags & LLKHF_INJECTED) == 0)
                    {
                        // 用户按任意物理键都暂停 2 秒。IME 组合输入通常会持续触发
                        // 物理 KEYDOWN，因此该状态会随着手动输入连续延长。
                        long until = DateTime.UtcNow.AddSeconds(2.0).Ticks;
                        System.Threading.Interlocked.Exchange(
                            ref _manualInputUntilTicks, until);
                    }
                }
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            IntPtr hook = _hook;
            _hook = IntPtr.Zero;
            if (hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(hook); }
                catch { }
            }
        }
    }
}
