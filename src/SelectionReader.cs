using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace LitReader
{
    // 读取其他程序中的选中文本。
    //
    // 三条路，按可靠性排序（均为实测结论）：
    //
    // 1) UI Automation TextPattern —— 【首选，浏览器/Word 都有效】
    //    实测：Edge 中成功读出选区（visited=1286, 540 个元素支持 TextPattern）。
    //    关键点：不能靠 HasKeyboardFocus 找元素 —— Chromium 不报告该属性，
    //    必须「遍历树，找支持 TextPattern 且选区非空的元素」。
    //    全程不碰键盘、不碰剪贴板，零副作用。
    //
    // 2) 模拟 Ctrl+C —— 【仅控制台类程序有效】
    //    实测：PowerShell 成功（len=1242），但 Chromium 会检查
    //    SendInput 的 LLKHF_INJECTED 标志并丢弃合成按键，因此浏览器里必然失败。
    //
    // 3) AttachThreadInput + SetFocus —— 配合 2) 使用，让按键到达目标线程。
    internal static class SelectionReader
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

        const uint GW_CHILD = 5;
        const uint GW_HWNDNEXT = 2;

        // ================= 方案 1：UI Automation =================
        // 必须从 STA 线程调用。
        // 关键：Chromium 只在浏览器处于活动状态时才向 UIA 暴露网页选区；
        // 若浮窗抢走焦点，TextPattern 数量会骤降、读不到选区（实测确认）。
        // 这里在此前提下再做两层加固：加大遍历规模 + 未命中时短暂等待重试。
        public static string ViaUiaRobust(IntPtr hwnd)
        {
            string t = ViaUia(hwnd, 6000, 20);
            if (!string.IsNullOrEmpty(t)) return t;
            // 首次可能因无障碍桥尚未就绪而失败，稍等再试一次
            Thread.Sleep(220);
            t = ViaUia(hwnd, 6000, 20);
            if (!string.IsNullOrEmpty(t)) App.Log("uia: succeeded on retry");
            return t;
        }

        public static string ViaUia(IntPtr hwnd, int maxElements, int maxDepth)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;

                // 广度优先：Document 类型优先，通常它就是承载选区的元素
                var queue = new Queue<KeyValuePair<AutomationElement, int>>();
                queue.Enqueue(new KeyValuePair<AutomationElement, int>(root, 0));
                int visited = 0;
                int withTextPattern = 0;
                string bestText = null;

                while (queue.Count > 0 && visited < maxElements)
                {
                    var kv = queue.Dequeue();
                    AutomationElement el = kv.Key;
                    int depth = kv.Value;
                    visited++;

                    object pat = null;
                    bool has = false;
                    try { has = el.TryGetCurrentPattern(TextPattern.Pattern, out pat); }
                    catch { }

                    if (has)
                    {
                        withTextPattern++;
                        string sel = null;
                        bool isDocument = false;
                        try
                        {
                            isDocument = el.Current.ControlType == ControlType.Document;
                        }
                        catch { }
                        try
                        {
                            TextPatternRange[] ranges = ((TextPattern)pat).GetSelection();
                            if (ranges != null && ranges.Length > 0)
                                sel = ranges[0].GetText(-1);
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(sel))
                        {
                            // Document 上的选区最可信；否则先记住，继续找更好的
                            if (isDocument) return sel;
                            if (bestText == null) bestText = sel;
                        }
                    }

                    if (depth < maxDepth)
                    {
                        try
                        {
                            AutomationElementCollection kids =
                                el.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                            for (int i = 0; i < kids.Count; i++)
                                queue.Enqueue(new KeyValuePair<AutomationElement, int>(kids[i], depth + 1));
                        }
                        catch { }
                    }
                }

                App.Log("uia: visited=" + visited + " textPattern=" + withTextPattern +
                        " -> " + (bestText == null ? "no selection" : "len=" + bestText.Length));
                return bestText;
            }
            catch (Exception ex)
            {
                App.Log("uia failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        public static IntPtr TargetWindow { get { return GetForegroundWindow(); } }

        public static IntPtr ForegroundWindow { get { return GetForegroundWindow(); } }

        public static string Describe(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero) return "(null hwnd)";
                var t = new StringBuilder(200); GetWindowTextW(h, t, 200);
                var c = new StringBuilder(200); GetClassNameW(h, c, 200);
                uint pid; GetWindowThreadProcessId(h, out pid);
                return "hwnd=" + h + " pid=" + pid + " class=" + c + " title=[" + t + "]";
            }
            catch { return "(unknown)"; }
        }

        public static void ActivateWindow(IntPtr h)
        {
            try { if (h != IntPtr.Zero && IsWindow(h)) SetForegroundWindow(h); }
            catch { }
        }

        // 可靠地把本窗口带到前台并拿到键盘焦点。
        // WPF 的 Activate() 对 AllowsTransparency / 无边框窗口经常不生效，
        // 结果是窗口看着在前台，键盘事件却仍送到别的程序（Esc 失效的原因）。
        // 标准解法：AttachThreadInput 连接当前前台线程后再 SetForegroundWindow。
        [ThreadStatic] static uint _fgPid;

        public static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                IntPtr fg = GetForegroundWindow();
                uint myTid = GetCurrentThreadId();
                uint fgTid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _fgPid) : 0;

                bool attached = false;
                if (fgTid != 0 && fgTid != myTid)
                    attached = AttachThreadInput(myTid, fgTid, true);
                try
                {
                    SetForegroundWindow(hwnd);
                    SetFocus(hwnd);
                    // 让出焦点后窗口可能被系统的前台锁顶回，再补一次
                    IntPtr now = GetForegroundWindow();
                    if (now != hwnd) SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(myTid, fgTid, false);
                }
            }
            catch { }
        }

        // 把键盘焦点真正交还给目标窗口。
        // AttachThreadInput 必须成对调用，所以用一个会话对象管理生命周期。
        public sealed class FocusSession : IDisposable
        {
            readonly uint myThread = GetCurrentThreadId();
            readonly uint targetThread;
            readonly bool attached;
            public readonly string Detail;

            public FocusSession(IntPtr target)
            {
                if (target == IntPtr.Zero) { Detail = "no target"; return; }
                uint pid;
                targetThread = GetWindowThreadProcessId(target, out pid);
                if (targetThread == 0 || targetThread == myThread)
                {
                    Detail = "thread=" + targetThread + " (same as ours, no attach)";
                    return;
                }
                attached = AttachThreadInput(myThread, targetThread, true);
                var before = GetFocus();
                IntPtr r = SetFocus(target);
                Detail = "targetThread=" + targetThread + " attach=" + attached +
                         " focusBefore=" + before + " focusAfter=" + r +
                         " (SetFocus returned " + (r == IntPtr.Zero ? "NULL=FAILED" : "ok") + ")";
            }

            public void Dispose()
            {
                if (attached)
                {
                    try { AttachThreadInput(myThread, targetThread, false); } catch { }
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        // 判断目标程序是否本来就持有键盘焦点。
        // 若已经持有，就不必 AttachThreadInput，直接发键即可，副作用最小。
        public static bool TargetHasKeyboardFocus(IntPtr target)
        {
            try
            {
                if (target == IntPtr.Zero) return false;
                uint pid;
                uint tid = GetWindowThreadProcessId(target, out pid);
                if (tid == 0) return false;

                // 我方线程自己有焦点吗？
                IntPtr mine = GetFocus();

                var gti = new GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                if (!GetGUIThreadInfo(tid, ref gti)) return false;

                bool targetFocused = gti.hwndFocus != IntPtr.Zero;
                return targetFocused;
            }
            catch (Exception ex)
            {
                App.Log("focus probe failed: " + ex.Message);
                return false;
            }
        }

        // 在已交还焦点后，用 Ctrl+C 取走选区
        public static string GrabViaCtrlC(int attempts, int delayMs)
        {
            for (int round = 0; round < attempts; round++)
            {
                IntPtr fg = GetForegroundWindow();
                App.Log("grab r" + round + ": foreground=" + Describe(fg));

                uint before = GetClipboardSequenceNumber();
                App.Log("grab r" + round + ": seqBefore=" + before + " currentText=" + Brief(TryText()));

                InputSimulator.CtrlC();

                int waited = 0;
                while (waited < delayMs)
                {
                    Thread.Sleep(10);
                    waited += 10;
                    uint now = GetClipboardSequenceNumber();
                    if (now == before) continue;
                    string t = TryText();
                    App.Log("grab r" + round + ": seq changed to " + now + " after " + waited + "ms, text=" + Brief(t));
                    if (!string.IsNullOrEmpty(t)) return t;
                    before = now;
                }
                App.Log("grab r" + round + ": no usable text within " + delayMs + "ms");
            }
            return null;
        }

        static string TryText()
        {
            try { return Clipboard.GetText(); }
            catch (Exception ex) { return "<err:" + ex.Message + ">"; }
        }

        static string Brief(string s)
        {
            if (s == null) return "NULL";
            string t = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length > 40) t = t.Substring(0, 40) + "...";
            return "len=" + s.Length + " [" + t + "]";
        }

        // ---- 窗口层级（临时取消置顶，避免隐藏造成的闪烁）----
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint flags);
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        public static void LowerWindow(IntPtr h)
        {
            try { if (h != IntPtr.Zero) SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); }
            catch { }
        }

        public static void RaiseWindow(IntPtr h)
        {
            try { if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); }
            catch { }
        }

        const uint SWP_SHOWWINDOW = 0x0040;

        // 把窗口抬到最上层显示，但【不激活、不抢键盘焦点】。
        // 热键唤出用它：窗口出现在最前面，而焦点仍留在用户正在读的程序里。
        public static void ShowNoActivate(IntPtr h)
        {
            try
            {
                if (h == IntPtr.Zero) return;
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }
    }
}
