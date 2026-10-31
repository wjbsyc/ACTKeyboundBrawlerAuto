using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ACTKeyboundBrawlerAuto
{
    /// <summary>
    /// SendInput/KEYEVENTF_UNICODE input helper for the foreground FFXIV window.
    /// Each word starts with a physical comma key, which stays in the buffer.
    /// This helper never sends Backspace or uses Enter to activate input.
    /// </summary>
    public static class UnicodeInputSender
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const ushort VK_RETURN = 0x0D;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_OEM_COMMA = 0xBC;

        private static readonly object EnterRateLock = new object();
        private static DateTime _lastEnterSentUtc = DateTime.MinValue;
        private static int _minimumEnterIntervalMs;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxChars);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public sealed class Result
        {
            public bool Success { get; set; }
            public bool Submitted { get; set; }
            public bool HasTypedInput { get; set; }
            public string Reason { get; set; }
            public string ForegroundProcess { get; set; }
            public string ForegroundWindowTitle { get; set; }
            public int ElapsedMs { get; set; }
            public int EnterGateMs { get; set; }

            public static Result Fail(string reason, string process, string title)
            {
                return new Result
                {
                    Success = false,
                    Submitted = false,
                    Reason = reason,
                    ForegroundProcess = process,
                    ForegroundWindowTitle = title
                };
            }
        }

        public static int MinimumEnterIntervalMs
        {
            get { return Thread.VolatileRead(ref _minimumEnterIntervalMs); }
            set { Thread.VolatileWrite(ref _minimumEnterIntervalMs, Math.Max(0, value)); }
        }

        public static Result TypeText(string text, int characterDelayMs)
        {
            return TypeText(text, characterDelayMs, null);
        }

        public static Result TypeText(string text, int characterDelayMs, Func<bool> canContinue)
        {
            var stopwatch = Stopwatch.StartNew();
            if (String.IsNullOrEmpty(text))
                return Result.Fail("word is empty", null, null);

            string process;
            string title;
            if (!IsForegroundFfxiv(out process, out title))
                return Result.Fail("foreground window is not FFXIV", process, title);

            if (IsModifierDown())
                return Result.Fail("Shift/Ctrl/Alt is down", process, title);

            if (canContinue != null && !canContinue())
                return Result.Fail("typing paused", process, title);

            // Use the scan-code path so the prefix also registers as keyboard activity.
            if (!SendPhysicalHeldKey(VK_OEM_COMMA, 18))
            {
                return Result.Fail(
                    "SendInput comma failed: " + Marshal.GetLastWin32Error(),
                    process, title);
            }

            Thread.Sleep(200);

            foreach (char character in text)
            {
                string currentProcess;
                string currentTitle;
                if (!IsForegroundFfxiv(out currentProcess, out currentTitle))
                {
                    Result changed = Result.Fail("foreground changed while typing", currentProcess, currentTitle);
                    changed.HasTypedInput = true;
                    return changed;
                }

                if ((canContinue != null && !canContinue()) || IsModifierDown())
                {
                    Result paused = Result.Fail("typing paused", currentProcess, currentTitle);
                    paused.HasTypedInput = true;
                    return paused;
                }

                if (!SendUnicodeKey(character, false) || !SendUnicodeKey(character, true))
                {
                    Result failed = Result.Fail(
                        "SendInput failed: " + Marshal.GetLastWin32Error(),
                        currentProcess, currentTitle);
                    failed.HasTypedInput = true;
                    return failed;
                }

                if (characterDelayMs > 0)
                    PreciseSleep(characterDelayMs);
            }

            stopwatch.Stop();
            return new Result
            {
                Success = true,
                Submitted = false,
                HasTypedInput = true,
                Reason = "typed-after-comma",
                ForegroundProcess = process,
                ForegroundWindowTitle = title,
                ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
                EnterGateMs = 0
            };
        }

        public static Result Submit()
        {
            return Submit(null);
        }

        public static Result Submit(Func<bool> canContinue)
        {
            var stopwatch = Stopwatch.StartNew();
            string process;
            string title;
            if (!IsForegroundFfxiv(out process, out title))
                return Result.Fail("foreground window is not FFXIV", process, title);

            if (IsModifierDown())
                return Result.Fail("Shift/Ctrl/Alt is down", process, title);

            int enterGateMs;
            if (!SendEnterRespectingRateLimit(delegate
            {
                string currentProcess;
                string currentTitle;
                return IsForegroundFfxiv(out currentProcess, out currentTitle) &&
                       !IsModifierDown() && (canContinue == null || canContinue()) &&
                       SendVirtualKey(VK_RETURN, false) &&
                       SendVirtualKey(VK_RETURN, true);
            }, out enterGateMs))
            {
                IsForegroundFfxiv(out process, out title);
                return Result.Fail("SendInput Enter failed: " + Marshal.GetLastWin32Error(), process, title);
            }

            // Keep the next input cycle from starting immediately after Enter.
            // The next cycle will still perform comma -> 200ms -> body as one
            // continuous TypeText operation.
            Thread.Sleep(100);

            stopwatch.Stop();
            return new Result
            {
                Success = true,
                Submitted = true,
                Reason = "ok",
                ForegroundProcess = process,
                ForegroundWindowTitle = title,
                ElapsedMs = (int)stopwatch.ElapsedMilliseconds,
                EnterGateMs = enterGateMs
            };
        }

        /// <summary>
        /// Clear by Enter only. Backspace is intentionally forbidden.
        /// </summary>
        public static void ClearTypedInput()
        {
            Submit();
        }

        private static bool SendEnterRespectingRateLimit(Func<bool> send, out int waitedMs)
        {
            waitedMs = 0;
            if (send == null)
                return false;

            lock (EnterRateLock)
            {
                if (_lastEnterSentUtc != DateTime.MinValue)
                {
                    DateTime notBefore = _lastEnterSentUtc.AddMilliseconds(MinimumEnterIntervalMs);
                    double remaining = (notBefore - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining > 0)
                    {
                        waitedMs = (int)Math.Ceiling(remaining);
                        Thread.Sleep(waitedMs);
                    }
                }

                // Mark conservatively before invoking the delegate. If a key-down
                // is accepted but a later key-up fails, the next Enter must still
                // respect the minimum interval.
                _lastEnterSentUtc = DateTime.UtcNow;
                if (!send())
                    return false;

                return true;
            }
        }

        private static void PreciseSleep(int milliseconds)
        {
            if (milliseconds <= 0)
                return;

            if (milliseconds >= 16)
                Thread.Sleep(milliseconds - 8);

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < milliseconds)
                Thread.Sleep(0);
        }

        private static bool SendPhysicalHeldKey(ushort virtualKey, int holdMilliseconds)
        {
            ushort scanCode = (ushort)MapVirtualKey(virtualKey, 0);
            if (scanCode == 0)
                return false;

            INPUT[] input =
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = scanCode,
                            dwFlags = KEYEVENTF_SCANCODE,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };

            if (SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) != 1)
                return false;

            Thread.Sleep(holdMilliseconds);
            input[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = scanCode,
                        dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            bool sent = SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) == 1;
            Thread.Sleep(3);
            return sent;
        }

        private static bool SendUnicodeKey(char character, bool keyUp)
        {
            INPUT[] input =
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = character,
                            dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };

            return SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        private static bool SendVirtualKey(ushort virtualKey, bool keyUp)
        {
            INPUT[] input =
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = virtualKey,
                            wScan = 0,
                            dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };

            return SendInput(1, input, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        private static bool IsForegroundFfxiv(out string processName, out string windowTitle)
        {
            processName = null;
            windowTitle = null;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return false;

            var titleBuilder = new StringBuilder(256);
            if (GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity) > 0)
                windowTitle = titleBuilder.ToString();

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0)
                return false;

            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                {
                    processName = process.ProcessName;
                    return processName != null &&
                           processName.StartsWith("ffxiv", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsModifierDown()
        {
            return (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        }
    }
}
