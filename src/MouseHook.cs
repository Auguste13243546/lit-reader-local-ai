using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LitReader
{
    // 模拟 Ctrl+C / Ctrl+V 按键（SendInput），用于取走其他程序里的选区文本
    internal static class InputSimulator
    {
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;

        const ushort VK_CONTROL = 0x11;
        const ushort VK_C = 0x43;
        const ushort VK_V = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        static INPUT Key(ushort vk, bool up)
        {
            var i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
            return i;
        }

        public static void CtrlC() { Chord(VK_C); }
        public static void CtrlV() { Chord(VK_V); }

        static void Chord(ushort vk)
        {
            // 分两次发送并在中间留出保持时间：一次性把按下/抬起全部投递，
            // 很多程序来不及处理这个组合键（实测剪贴板完全不动）。
            var down = new INPUT[2];
            down[0] = Key(VK_CONTROL, false);
            down[1] = Key(vk, false);
            SendInput(2, down, Marshal.SizeOf(typeof(INPUT)));

            Thread.Sleep(25);

            var up = new INPUT[2];
            up[0] = Key(vk, true);
            up[1] = Key(VK_CONTROL, true);
            SendInput(2, up, Marshal.SizeOf(typeof(INPUT)));
        }

        // 单独发送 keydown（用于需要更细控制的场景）
        public static void KeyDown(ushort vk) { SendInput(1, new INPUT[] { Key(vk, false) }, Marshal.SizeOf(typeof(INPUT))); }
        public static void KeyUp(ushort vk) { SendInput(1, new INPUT[] { Key(vk, true) }, Marshal.SizeOf(typeof(INPUT))); }
    }

    // 全局低级鼠标钩子：检测“左键拖动后松开”，即一次划词动作
    internal class MouseHookManager
    {
        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);

        [DllImport("user32.dll")]
        static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessageW(ref MSG msg);

        // 完整镜像 Win32 MSG，否则 GetMessageW 会写越界破坏内存
        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        // 回调签名：起点、终点（屏幕坐标）
        public Action<int, int, int, int> OnDragEnd;

        // 全局 Esc：浮窗不抢焦点时键盘事件到不了它，用它兜底关闭
        public Action OnEscape;

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int VK_ESCAPE = 0x1B;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        HookProc kbProc;
        IntPtr kbHook = IntPtr.Zero;
        public bool WantKeyboardHook = true;

        HookProc proc;          // 保持引用，防止 GC 回收导致钩子失效
        IntPtr hook = IntPtr.Zero;
        Thread thread;
        int downX, downY;
        bool down;

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Stop()
        {
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            if (kbHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(kbHook);
                kbHook = IntPtr.Zero;
            }
        }

        void Loop()
        {
            IntPtr mod = GetModuleHandleW(null);

            proc = Callback;
            hook = SetWindowsHookExW(WH_MOUSE_LL, proc, mod, 0);
            if (hook == IntPtr.Zero)
                App.Log("mouse hook install FAILED, err=" + Marshal.GetLastWin32Error());
            else
                App.Log("mouse hook installed");

            // 键盘钩子用于兜底响应 Esc：浮窗不抢焦点时收不到键盘事件
            if (WantKeyboardHook)
            {
                kbProc = KeyboardCallback;
                kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, kbProc, mod, 0);
                if (kbHook == IntPtr.Zero)
                    App.Log("keyboard hook install FAILED, err=" + Marshal.GetLastWin32Error());
                else
                    App.Log("keyboard hook installed (Esc)");
            }

            if (hook == IntPtr.Zero && kbHook == IntPtr.Zero)
            {
                App.Log("no hooks installed; hook thread exiting");
                return;
            }

            MSG msg;
            // 低级钩子需要在本线程跑消息循环才会被回调
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            App.Log("hook loop ended");
        }

        IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int m = wParam.ToInt32();
                    if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                    {
                        var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        if (k.vkCode == VK_ESCAPE)
                        {
                            var cb = OnEscape;
                            if (cb != null) cb();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("keyboard hook error: " + ex.Message);
            }
            return CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int m = wParam.ToInt32();
                    if (m == WM_LBUTTONDOWN)
                    {
                        var d = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        downX = d.pt.x; downY = d.pt.y; down = true;
                    }
                    else if (m == WM_LBUTTONUP && down)
                    {
                        down = false;
                        var d = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        int x = d.pt.x, y = d.pt.y;
                        int distX = Math.Abs(x - downX), distY = Math.Abs(y - downY);
                        // 拖动距离够大才算划词，单击/微动忽略
                        if (distX >= 8 || distY >= 8)
                        {
                            var cb = OnDragEnd;
                            int sx = downX, sy = downY;
                            if (cb != null) cb(sx, sy, x, y);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("mouse hook callback error: " + ex.Message);
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }
}
