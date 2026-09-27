using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IoPath = System.IO.Path;

namespace LitReader
{
    public partial class FloatWindow : Window
    {
        // ---------- 配置 ----------
        // Ollama 地址来自 AppConfig（config.txt / 环境变量），源码中不硬编码任何个人路径
        static string OllamaBase { get { return AppConfig.OllamaBase; } }
        const string ModelText = "lit-reader";
        const string ModelVision = "lit-reader-vision";
        const int CtxText = 16384;
        const int CtxVision = 8192;

        // ---------- 全局热键 ----------
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int HOTKEY_ID = 0xB1;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
        const uint VK_Z = 0x5A;
        bool hotkeyOk = false;
        string hotkeyLabel = "(未注册)";

        // ---------- 状态 ----------
        readonly HttpClient http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        readonly List<object[]> history = new List<object[]>();   // {role, content}
        bool busy = false;
        bool thinkMode = false;
        bool visionMode = false;
        byte[] pendingImage;
        string pendingImageName;
        CancellationTokenSource cts;

        // 划词自动发送
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        const uint WM_CLOSE = 0x0010;
        MouseHookManager mouseHook;
        bool autoPaste = true;          // 划词功能总开关
        bool autoSendOnSelect = true;   // true=直接发送；false=仅填入输入框
        // 最后一次划词所在的目标窗口。热键路径靠它去取选区。
        IntPtr lastDragTarget = IntPtr.Zero;
        bool grabbing = false;          // 防止取文本流程重入

        // ================= 模式与长期记忆 =================
        // 两个模式各自独立的上下文，互不串味：
        //   lit  = 文献术语助手（默认）
        //   life = 生活助手，对话结束后自动总结，并作为长期记忆回读
        enum AssistantMode { Lit, Life }
        AssistantMode mode = AssistantMode.Lit;

        readonly List<object[]> litHistory = new List<object[]>();
        readonly List<object[]> lifeHistory = new List<object[]>();

        // 生活助手长期记忆（三层）：rolling = 滚动总记忆；archive = 每次会话的摘要文件
        // 目录来自 AppConfig（默认 <程序目录>\data），不硬编码个人路径
        static string MemRoot { get { return AppConfig.DataDir; } }
        string rollingMemory = null;      // 已加载的滚动记忆文本（注入上下文用）
        bool lifeDirty = false;           // 本会话是否有新内容待总结

        // 空闲超时触发总结（分钟）
        DispatcherTimer idleTimer;
        int idleMinutes = 10;

        // ================= 空闲超时自动总结（生活助手） =================
        void StartIdleWatch()
        {
            idleTimer = new DispatcherTimer();
            idleTimer.Interval = TimeSpan.FromMinutes(1);
            idleTimer.Tick += delegate { CheckIdle(); };
            idleTimer.Start();
            lastActivity = DateTime.Now;

            // 焦点守卫单独用更快的节拍（2 秒），但只在「刚取过文本」后的窗口期内生效，
            // 避免正常切到别的程序时被反复抢回焦点。
            focusGuardTimer = new DispatcherTimer();
            focusGuardTimer.Interval = TimeSpan.FromSeconds(2);
            focusGuardTimer.Tick += delegate { CheckFocusGuard(); };
            focusGuardTimer.Start();
        }

        DispatcherTimer focusGuardTimer;
        // 只有在这个时间点之后才允许焦点守卫介入（取文本流程结束时会设置）
        DateTime focusGuardArmedUntil = DateTime.MinValue;

        DateTime lastActivity = DateTime.Now;

        void TouchActivity() { lastActivity = DateTime.Now; }

        void CheckIdle()
        {
            CheckFocusGuard();      // 焦点守卫（每 1 分钟也会兜一次）

            if (mode != AssistantMode.Life) return;
            if (busy || grabbing) return;
            if (!lifeDirty) return;
            if (lifeHistory.Count == 0) return;
            if ((DateTime.Now - lastActivity).TotalMinutes < idleMinutes) return;

            App.Log("idle " + idleMinutes + "min -> summarizing life session");
            SummarizeLifeSession("空闲超时", null);
        }

        public FloatWindow()
        {
            InitializeComponent();
            LoadGeometry();
            RegisterHotkey();
            InstallMouseHook();
            StartIdleWatch();
            StartMarksWatch();
            LoadLongTermMemory();
            Loaded += (s, e) =>
            {
                InitSmoothScroll();
                RefreshToggles();
                Greet();

                // 开机自启（--hidden）时不弹出窗口，只留托盘图标，等 Alt+Z 唤出。
                // 测试模式（--capture-test）需要窗口可见，所以跳过隐藏。
                bool hidden = App.StartHidden && !App.HasArgStaticPublic("--capture-test");
                MainWindowShown = !hidden;
                if (hidden)
                {
                    Hide();
                    App.Log("started hidden (autostart)");
                }
                else
                {
                    // 启动即显示时也要「不抢焦点」。
                    // 实测教训：浮窗一旦成为激活窗口，Chromium 就不再向 UIA 暴露网页选区
                    // （TextPattern 数量骤降），导致第一次划词必然失败；而回退的 Ctrl+C
                    // 又会被 Chromium 丢弃。所以启动时必须把焦点还给启动前的程序。
                    IntPtr prev = PreStartForeground;
                    if (prev != IntPtr.Zero) focusReturnTarget = prev;
                    SelectionReader.ShowNoActivate(new WindowInteropHelper(this).Handle);
                    App.Log("startup visible: IsVisible=" + IsVisible +
                            " IsActive=" + IsActive +
                            " handle=" + new WindowInteropHelper(this).Handle +
                            " focusReturnTarget=" + prev);
                    ReturnFocusToPrevious();
                }
            };
        }

        // 记录是否应该显示窗口（供托盘等使用）
        public bool MainWindowShown = true;

        // ================= 模式切换 =================
        List<object[]> CurrentHistory
        {
            get { return mode == AssistantMode.Life ? lifeHistory : litHistory; }
        }

        string MemoryDir
        {
            get
            {
                string sub = mode == AssistantMode.Life ? "生活" : "学术词汇";
                return IoPath.Combine(MemRoot, sub);
            }
        }

        string RollingMemoryFile { get { return IoPath.Combine(MemoryDir, "长期记忆.md"); } }

        void BtnMode_Click(object sender, RoutedEventArgs e)
        {
            if (busy) { App.Log("mode switch ignored: busy"); return; }

            // 切走生活助手前，把未总结内容交给后台异步总结（不阻塞切模式）
            if (mode == AssistantMode.Life && lifeDirty && lifeHistory.Count > 0)
                SummarizeLifeSession("切换模式", new List<object[]>(lifeHistory));

            mode = (mode == AssistantMode.Lit) ? AssistantMode.Life : AssistantMode.Lit;
            App.Log("mode -> " + mode);

            SaveDraft();      // 保留切模式前的输入
            MsgPanel.Children.Clear();
            if (smooth != null) smooth.JumpToEnd();
            LoadLongTermMemory();
            RefreshToggles();
            Greet();
            LoadDraft();
            Input.Focus();
        }

        // ================= 长期记忆：加载 =================
        void LoadLongTermMemory()
        {
            rollingMemory = null;
            try
            {
                if (!File.Exists(RollingMemoryFile)) { App.Log("no memory file yet: " + RollingMemoryFile); return; }
                string t = File.ReadAllText(RollingMemoryFile, Encoding.UTF8);
                if (!string.IsNullOrEmpty(t.Trim()))
                {
                    rollingMemory = t.Trim();
                    App.Log("memory loaded, len=" + rollingMemory.Length);
                }
            }
            catch (Exception ex) { App.Log("memory load failed: " + ex.Message); }
        }

        // ================= 长期记忆：总结并保存 =================
        // 三层结构：
        //   长期记忆.md   —— 滚动记忆，每次会话后合并更新；下次会话注入上下文
        //   会话记录/     —— 每次会话的独立摘要存档，供人查阅（不注入上下文）
        // snapshot 为 null 时使用当前 lifeHistory；传入快照可在清空历史后仍异步总结。
        // 注意：总结走网络请求（约数秒），绝不能阻塞 UI 线程。
        void SummarizeLifeSession(string reason, List<object[]> snapshot)
        {
            List<object[]> src = snapshot != null ? snapshot : lifeHistory;
            if (src == null || src.Count == 0) return;

            App.Log("summarize start (" + reason + "), msgs=" + src.Count);
            string convo = BuildPlainTranscript(src);
            string oldMem = rollingMemory == null ? "(暂无)" : rollingMemory;
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            int msgCount = src.Count;

            // 一次调用同时产出：滚动记忆（合并）与会话摘要（独立）
            string prompt =
                "你在维护我的个人生活助手长期记忆。请阅读【已有长期记忆】和【本次对话】，输出两段内容，严格按下面格式，" +
                "不要输出任何其它文字：\n\n" +
                "===ROLLING===\n" +
                "（把本次对话中值得长期记住的新信息合并进已有记忆，输出更新后的完整记忆；" +
                "只保留事实、偏好、决定、待办、重要背景；用简短条目；不要编造，不要写客套话；" +
                "已有记忆里仍然有效的内容要保留。控制在 500 字以内。）\n" +
                "===SUMMARY===\n" +
                "（本次对话的独立摘要，200 字以内，说明聊了什么、结论是什么。）\n\n" +
                "【已有长期记忆】\n" + oldMem + "\n\n" +
                "【本次对话】\n" + convo;

            RunSummaryRequest(MemoryDir, "会话摘要", reason, prompt, msgCount,
                delegate(string output, string fpath)
                {
                    string rolling, summary;
                    ParseSummary(output, out rolling, out summary);

                    if (!string.IsNullOrEmpty(rolling))
                    {
                        File.WriteAllText(RollingMemoryFile,
                            "# 长期记忆（自动维护）\n\n最后更新：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") +
                            "\n\n" + rolling + "\n", Encoding.UTF8);
                        rollingMemory = rolling;
                        App.Log("rolling memory updated, len=" + rolling.Length);
                    }

                    lifeDirty = false;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        AddBubble("assistant", "（本次对话已总结并存入长期记忆：" +
                                  IoPath.GetFileName(fpath) + "）");
                    }));
                });
        }

        // ================= 文献模式：学术词汇自动入库 =================
        // 每次回答后异步抽取术语与释义，追加到 学术词汇 目录，便于长期积累复习。
        void ExtractAcademicVocab(string question, string answer)
        {
            if (string.IsNullOrEmpty(answer) || answer.Trim().Length < 8) return;

            string prompt =
                "从下面这段学术问答中抽取术语。对每个术语输出一行，格式严格为：\n" +
                "术语 | 一句话释义（50 字以内）\n\n" +
                "要求：\n" +
                "1. 只抽取真正的专业/学术术语，忽略日常词汇。\n" +
                "2. 优先保留英文原词；中文术语直接写中文。\n" +
                "3. 最多 5 个，不要编造，不要输出任何解释性文字或标题。\n" +
                "4. 若确实没有学术术语，只输出：NONE\n\n" +
                "【问题】\n" + Trim(question, 400) + "\n\n【回答】\n" + Trim(answer, 1200);

            string vocDir = IoPath.Combine(MemRoot, "学术词汇");
            RunSummaryRequest(vocDir, "学术词汇", "自动抽取", prompt, 1,
                delegate(string output, string fpath)
                {
                    string t = output == null ? "" : output.Trim();
                    if (t.Length == 0 || t.ToUpper().IndexOf("NONE") == 0)
                    {
                        App.Log("vocab: no academic term found");
                        return;
                    }

                    // 追加到「词汇总表.md」，按术语去重（同名术语只保留首次记录）
                    string master = IoPath.Combine(vocDir, "词汇总表.md");
                    var lines = new List<string>();
                    foreach (string raw in t.Split('\n'))
                    {
                        string line = raw.Trim().TrimStart('-', '*', ' ');
                        if (line.Length == 0) continue;
                        if (line.IndexOf('|') < 0) continue;
                        lines.Add("- " + line);
                    }
                    if (lines.Count == 0) { App.Log("vocab: nothing parseable"); return; }

                    var existing = new StringBuilder();
                    string old = "";
                    try { if (File.Exists(master)) old = File.ReadAllText(master, Encoding.UTF8); }
                    catch { }

                    var added = new List<string>();
                    foreach (string line in lines)
                    {
                        string term = line.Substring(2).Split('|')[0].Trim();
                        if (term.Length == 0) continue;
                        // 简单去重：总表里已出现过该术语就跳过
                        if (old.IndexOf("**" + term + "**", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        added.Add(line);
                    }
                    if (added.Count == 0) { App.Log("vocab: all terms already recorded"); return; }

                    existing.Append("\n## ").Append(DateTime.Now.ToString("yyyy-MM-dd")).Append('\n');
                    foreach (string a in added) existing.Append(a).Append('\n');

                    if (old.Length == 0)
                    {
                        File.WriteAllText(master,
                            "# 学术词汇总表（自动累积）\n\n> 由文献术语模式自动抽取，可自由编辑删除。\n" + existing,
                            Encoding.UTF8);
                    }
                    else
                    {
                        File.AppendAllText(master, existing.ToString(), Encoding.UTF8);
                    }
                    App.Log("vocab: added " + added.Count + " term(s) -> " + master);
                });
        }

        // 通用：发起一次总结请求并把结果写入指定目录。
        // 落盘部分在同一后台线程完成，避免阻塞 UI。
        void RunSummaryRequest(string dir, string filePrefix, string reason,
                               string prompt, int msgCount, Action<string, string> save)
        {
            var req = new List<object[]>();
            req.Add(new object[] { "user", new Dictionary<string, object> { { "role", "user" }, { "content", prompt } } });

            RunCompletion(ModelText, 8192, false, req,
                delegate(string output)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string recDir = IoPath.Combine(dir, "会话记录");
                        Directory.CreateDirectory(recDir);

                        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        string fname = DateTime.Now.ToString("yyyy-MM-dd_HHmm") + "_" + SanitizeFileName(reason) + ".md";
                        string fpath = IoPath.Combine(recDir, fname);

                        // 存档写原始输出，便于人工查阅；结构化处理交给 save 回调
                        File.WriteAllText(fpath,
                            "# " + filePrefix + "\n\n时间：" + stamp + "\n触发：" + reason +
                            "\n消息数：" + msgCount + "\n\n" + (output == null ? "" : output.Trim()) + "\n",
                            Encoding.UTF8);
                        App.Log(filePrefix + " saved: " + fname);

                        if (save != null) save(output, fpath);
                    }
                    catch (Exception ex) { App.Log(filePrefix + " save failed: " + ex.Message); }
                },
                delegate(string err) { App.Log(filePrefix + " request failed: " + err); });
        }

        // 解析 ===ROLLING=== / ===SUMMARY=== 两段
        void ParseSummary(string output, out string rolling, out string summary)
        {
            rolling = null; summary = null;
            if (string.IsNullOrEmpty(output)) return;

            const string rTag = "===ROLLING===";
            const string sTag = "===SUMMARY===";
            int ri = output.IndexOf(rTag, StringComparison.OrdinalIgnoreCase);
            int si = output.IndexOf(sTag, StringComparison.OrdinalIgnoreCase);

            if (ri >= 0 && si > ri)
            {
                rolling = output.Substring(ri + rTag.Length, si - ri - rTag.Length).Trim();
                summary = output.Substring(si + sTag.Length).Trim();
            }
            else
            {
                // 模型没按格式输出：整体当摘要，滚动记忆取前 500 字
                summary = output.Trim();
                rolling = summary.Length > 500 ? summary.Substring(0, 500) : summary;
            }
        }

        static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "session";
            foreach (char c in IoPath.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 20 ? s.Substring(0, 20) : s;
        }

        // 把消息列表拍平成纯文本，供总结使用
        string BuildPlainTranscript(List<object[]> msgs)
        {
            var sb = new StringBuilder();
            foreach (object[] m in msgs)
            {
                var d = (Dictionary<string, object>)m[1];
                string role = (string)d["role"];
                string content = d.ContainsKey("content") ? (string)d["content"] : "";
                if (string.IsNullOrEmpty(content)) continue;
                if (content.Length > 1200) content = content.Substring(0, 1200) + "…";
                sb.Append(role == "user" ? "我：" : "助手：").Append(content).Append("\n");
            }
            return sb.ToString();
        }

        // ESC 兜底键盘钩子是否启用。
        // 通过环境变量 LITREADER_NO_KBHOOK=1 可关闭，用于判断它是否影响键盘输入。
        static bool KbHookEnabled()
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("LITREADER_NO_KBHOOK");
                return !(v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
            }
            catch { return true; }
        }

        // ================= 划词：全局鼠标钩子 =================
        void InstallMouseHook()
        {
            try
            {
                mouseHook = new MouseHookManager();
                mouseHook.OnDragEnd = OnGlobalDragEnd;
                mouseHook.WantKeyboardHook = KbHookEnabled();
                // Esc 兜底：浮窗不抢焦点时键盘事件到不了它
                mouseHook.OnEscape = delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (IsVisible) { SaveDraft(); Hide(); App.Log("hidden by global Esc"); }
                    }));
                };
                mouseHook.Start();
            }
            catch (Exception ex)
            {
                App.Log("mouse hook init failed: " + ex.Message);
            }
        }

        // 由钩子线程调用：把处理切回 UI 线程
        void OnGlobalDragEnd(int x1, int y1, int x2, int y2)
        {
            App.Log("drag detected: from (" + x1 + "," + y1 + ") to (" + x2 + "," + y2 + ")");
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    // 诊断：把每个守卫的判定结果都记下来，便于定位"不该触发却触发"或"该触发却没触发"
                    bool oursStart = IsOursAt(x1, y1);
                    bool oursEnd = IsOursAt(x2, y2);
                    App.Log("drag guards: autoPaste=" + autoPaste + " visible=" + IsVisible +
                            " busy=" + busy + " grabbing=" + grabbing +
                            " oursStart=" + oursStart + " oursEnd=" + oursEnd +
                            " inside=" + IsPointInsideWindow(x2, y2) +
                            " lastDragTarget=" + lastDragTarget);

                    if (!autoPaste) return;
                    if (!IsVisible) return;          // 浮窗没开着就不接管
                    if (busy) return;                // 正在生成，别打断
                    if (grabbing) return;            // 取文本进行中，避免与剪贴板还原交错

                    // 起止任一端落在浮窗上（含子控件）都算「窗口内选择」，不接管。
                    // 判定用 WindowFromPoint，比单纯矩形比较更可靠。
                    if (oursStart || oursEnd)
                    {
                        App.Log("drag ignored: ours (start=" + oursStart + " end=" + oursEnd + ")");
                        return;
                    }

                    // 记住这次划词的目标窗口：热键路径要靠它回头取选区
                    lastDragTarget = SelectionReader.TargetWindow;
                    CaptureSelectionThenSend(lastDragTarget);
                }
                catch (Exception ex)
                {
                    App.Log("drag-end handling failed: " + ex.Message);
                }
            }), DispatcherPriority.Normal);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        // 判断某个屏幕坐标是否落在浮窗（含其子控件）上。
        // 优先用 WindowFromPoint 直接问系统「这个点属于谁的窗口」，比矩形比较更可靠
        // —— 能覆盖窗口边框、子控件、以及窗口部分被遮挡等各种情况。
        bool IsOursAt(int screenX, int screenY)
        {
            try
            {
                IntPtr me = new WindowInteropHelper(this).Handle;
                if (me == IntPtr.Zero) return false;

                var pt = new POINT();
                pt.X = screenX;
                pt.Y = screenY;
                IntPtr hit = WindowFromPoint(pt);
                if (hit != IntPtr.Zero && (hit == me || IsChild(me, hit))) return true;

                // 兜底：仍用窗口矩形比较（物理像素）
                return IsPointInsideWindow(screenX, screenY);
            }
            catch { return false; }
        }

        // 关键：钩子坐标是物理像素，必须和窗口的物理像素矩形比较。
        // 不能用 ActualWidth/ActualHeight——那是 WPF 设备无关单位，在高 DPI 下会误判。
        bool IsPointInsideWindow(int screenX, int screenY)
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h == IntPtr.Zero) return false;
                RECT r;
                if (!GetWindowRect(h, out r)) return false;
                return screenX >= r.Left && screenX <= r.Right && screenY >= r.Top && screenY <= r.Bottom;
            }
            catch (Exception ex)
            {
                App.Log("inside-check error: " + ex.Message);
                return false;
            }
        }

        // 取选区文本。
        //
        // 关键（实测定位）：GetForegroundWindow 早已是目标程序，但「键盘焦点」仍
        // 在浮窗上；SendInput 把按键投给拥有焦点的窗口，于是 Ctrl+C 落空、剪贴板
        // 序列号一动不动。必须用 AttachThreadInput 把本线程与目标线程的输入队列
        // 临时连接，再 SetFocus 真正交还焦点。
        //
        // AttachThreadInput 必须与调用它的线程配对成对释放，因此整个
        // 「让渡焦点 -> Ctrl+C -> 恢复」过程都在 UI 线程上用定时器推进，
        // 既保证配对，又不阻塞界面。
        // 焦点守卫：窗口可见时若键盘焦点不在本窗口上，把它收回来。
        // 取文本流程会用 SetFocus 把焦点交给目标程序，万一某个路径漏了恢复，
        // 就会出现「窗口在前台却打不进字」；这个守卫作为兜底。
        DateTime lastFocusHeal = DateTime.MinValue;

        void CheckFocusGuard()
        {
            try
            {
                if (!IsVisible) return;
                if (busy || grabbing) return;            // 正在生成/取文本，别打断
                if (DateTime.Now > focusGuardArmedUntil) return;   // 不在取文本后的窗口期，不干预
                if (DateTime.Now - lastFocusHeal < TimeSpan.FromSeconds(1)) return;

                IntPtr me = new WindowInteropHelper(this).Handle;
                if (me == IntPtr.Zero) return;

                IntPtr fg = SelectionReader.ForegroundWindow;
                if (fg == me) return;                    // 已是前台，正常

                lastFocusHeal = DateTime.Now;
                App.Log("focus guard: foreground=" + fg + " != ours=" + me + " -> restoring");
                RestoreOurFocus();
            }
            catch { }
        }

        // 取文本流程结束后调用：只在随后几秒内允许守卫介入
        void ArmFocusGuard()
        {
            focusGuardArmedUntil = DateTime.Now.AddSeconds(6);
        }

        // 消息锚点标尺 =================
        // 在滚动条位置画一条细标尺：每「轮」对话一个锚点（一问一答算一轮），
        // 点击即可平滑跳到该轮。跳转目标是「该轮最后一行的底部」，
        // 这样提问与回答都完整可见（若停在提问顶部，提问会被滚到上方看不见）。
        readonly List<double> markOffsets = new List<double>();   // 每轮提问的内容空间起点
        readonly List<double> markScrollTops = new List<double>(); // 每轮对应的滚动目标
        int hoverMark = -1;
        DispatcherTimer marksTimer;

        void StartMarksWatch()
        {
            marksTimer = new DispatcherTimer();
            marksTimer.Interval = TimeSpan.FromMilliseconds(300);
            marksTimer.Tick += delegate { RedrawMarks(); };
            marksTimer.Start();
        }

        void RedrawMarks()
        {
            try
            {
                if (MsgMarks == null) return;

                double scrollable = Scroller.ScrollableHeight;
                if (scrollable <= 1 || MsgPanel.Children.Count == 0)
                {
                    if (MsgMarks.Children.Count > 0) MsgMarks.Children.Clear();
                    markOffsets.Clear();
                    markScrollTops.Clear();
                    return;
                }

                // 逐条累积位置。轮次以「用户消息」为界，每轮一个锚点。
                // 跳转目标 = 该轮提问的位置（略微上留边距），
                // 让提问停在视野顶部、回答从下方接续 —— 这样两者都看得见。
                var questions = new List<double>();
                var scrollTops = new List<double>();
                double acc = 0;
                const double topMargin = 8;

                foreach (UIElement child in MsgPanel.Children)
                {
                    var fe = child as FrameworkElement;
                    if (fe == null) continue;

                    bool isQuestion = fe.Tag != null && (string)fe.Tag == "user";
                    if (isQuestion)
                    {
                        questions.Add(acc);
                        double st = acc - topMargin;
                        if (st < 0) st = 0;
                        scrollTops.Add(st);
                    }

                    double h = fe.ActualHeight;
                    if (double.IsNaN(h) || h < 0) h = 0;
                    acc += h;
                }

                if (questions.Count == 0) return;

                markOffsets.Clear();
                markOffsets.AddRange(questions);
                markScrollTops.Clear();
                markScrollTops.AddRange(scrollTops);

                double canvasH = MsgMarks.ActualHeight;
                if (canvasH <= 8) return;

                int n = questions.Count;

                if (MsgMarks.Children.Count != n)
                {
                    MsgMarks.Children.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        MsgMarks.Children.Add(new Border
                        {
                            Background = Res("Ink"),
                            Height = 3,
                            Width = 16,
                            CornerRadius = new CornerRadius(2)
                        });
                    }
                }

                // 等距排布：不按消息实际高度比例，而是一把「尺子」——视觉更整齐。
                // 每格中心 = (i + 0.5) * 格高，避免首尾贴边。
                double slot = canvasH / n;
                for (int i = 0; i < n; i++)
                {
                    var r = MsgMarks.Children[i] as Border;
                    if (r == null) continue;

                    bool hov = (i == hoverMark);
                    // 悬停：变红 + 明显加长
                    r.Background = hov ? Res("MarkRed") : Res("Ink");
                    r.Width = hov ? 24 : 16;
                    r.Height = hov ? 5 : 3;

                    double y = (i + 0.5) * slot - r.Height / 2.0;
                    if (y < 0) y = 0;
                    if (y > canvasH - r.Height) y = canvasH - r.Height;
                    // 悬停时向左伸出，右端对齐
                    Canvas.SetLeft(r, MsgMarks.Width - r.Width - 3);
                    Canvas.SetTop(r, y);
                }
            }
            catch { }
        }

        int MarkIndexAt(double y)
        {
            int n = markOffsets.Count;
            if (n == 0) return -1;
            double canvasH = MsgMarks.ActualHeight;
            if (canvasH <= 8) return -1;

            // 与绘制保持同一套等距公式
            double slot = canvasH / n;
            int idx = (int)(y / slot);
            if (idx < 0) idx = 0;
            if (idx >= n) idx = n - 1;
            return idx;
        }

        void MsgMarks_Move(object sender, MouseEventArgs e)
        {
            try
            {
                int idx = MarkIndexAt(e.GetPosition(MsgMarks).Y);
                if (idx != hoverMark) { hoverMark = idx; RedrawMarks(); }
            }
            catch { }
        }

        void MsgMarks_Leave(object sender, MouseEventArgs e)
        {
            if (hoverMark != -1) { hoverMark = -1; RedrawMarks(); }
        }

        void MsgMarks_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                int n = markOffsets.Count;
                if (n == 0 || markScrollTops.Count != n) return;

                int idx = MarkIndexAt(e.GetPosition(MsgMarks).Y);
                if (idx < 0 || idx >= n) return;

                double scrollable = Scroller.ScrollableHeight;
                // 用预先算好的「该轮底部 - 视口高度」作为目标：
                // 滚动后这一轮的提问与回答都会留在视野内。
                double target = markScrollTops[idx];
                if (target > scrollable) target = scrollable;
                if (target < 0) target = 0;

                // 跳转后暂停自动跟随，否则下一条流式内容会把你拽回底部
                if (smooth != null) smooth.ScrollTo(target, false);
                App.Log("mark jump -> round " + idx + " target=" + target.ToString("0"));
            }
            catch { }
        }

        // 把键盘焦点抢回浮窗与输入框。
        // 取文本时 SetFocus 会把焦点交给目标程序，释放输入队列连接不会撤销它，
        // 不处理就会导致「窗口在前台但打不进字」。
        void RestoreOurFocus()
        {
            try
            {
                if (!IsVisible) return;
                ArmFocusGuard();     // 随后几秒内若焦点又被抢走，守卫会兜底收回
                SelectionReader.ForceForeground(new WindowInteropHelper(this).Handle);
                Activate();
                Input.Focus();
                Keyboard.Focus(Input);
                App.Log("focus restored to window/input");
            }
            catch (Exception ex) { App.Log("focus restore failed: " + ex.Message); }
        }

        void CaptureSelectionThenSend(IntPtr target)
        {
            if (grabbing) { App.Log("capture: already grabbing, ignored"); return; }
            grabbing = true;
            App.Log("capture: target = " + SelectionReader.Describe(target));

            // 先试 UI Automation（浏览器 / Word 有效，且不碰键盘与剪贴板）。
            // UIA 首次连接 Chromium 无障碍层可能耗时几百毫秒，放到后台线程，
            // 不能阻塞 UI；成功后立刻回到 UI 线程处理。
            IntPtr t = target;
            var worker = new Thread(delegate()
            {
                string text = null;
                try { text = SelectionReader.ViaUiaRobust(t); }
                catch (Exception ex) { App.Log("uia threw: " + ex.Message); }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        App.Log("capture via UIA: len=" + text.Length + " [" + Preview(text) + "]");
                        grabbing = false;
                        // 注意：UIA 路径不碰键盘，所以这里【不】抢焦点 ——
                        // 保持焦点在用户正在读的程序里，划词后可以继续在原文操作。
                        PasteAsSend(text);
                        return;
                    }
                    App.Log("capture: UIA found nothing, falling back to Ctrl+C");
                    CaptureViaClipboard(t);
                }));
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);   // UIA 要求 STA
            worker.Start();
        }

        // 回退方案：模拟 Ctrl+C 读剪贴板。
        // 仅对控制台类程序有效；Chromium 会丢弃注入按键（实测）。
        void CaptureViaClipboard(IntPtr target)
        {
            IDataObject saved = null;
            try { saved = Clipboard.GetDataObject(); } catch { }

            // 取消置顶并压到其他窗口之下（不 Hide，避免闪烁）
            try { Topmost = false; } catch { }
            SelectionReader.LowerWindow(new WindowInteropHelper(this).Handle);
            // SendInput 要求目标处于前台，仅有键盘焦点不够
            SelectionReader.ActivateWindow(target);
            Thread.Sleep(80);
            SelectionReader.ActivateWindow(target);   // 再确认一次

            SelectionReader.FocusSession session = null;
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(60);

            int elapsed = 0;
            const int maxWait = 2600;      // 总超时（浏览器需要更久）
            int retries = 0;
            const int maxRounds = 5;
            int retryAfter = 400;
            bool sentOnce = false;
            uint beforeSeq = GetClipboardSequenceNumber();

            Action finish = delegate
            {
                timer.Stop();
                // 必须成对释放输入队列连接，否则会泄漏权限状态
                if (session != null) { session.Dispose(); session = null; }

                try { if (saved != null) Clipboard.SetDataObject(saved, true); } catch { }
                try { Topmost = true; } catch { }

                // 关键修复：为发送 Ctrl+C，FocusSession 曾用 SetFocus 把键盘焦点交给目标程序；
                // 释放 AttachThreadInput 并不会撤销这个后果，于是会出现
                // 「浮窗在前台、键盘输入却送给外部程序」-> 输入框打不进字。
                RestoreOurFocus();

                grabbing = false;
            };

            timer.Tick += delegate
            {
                elapsed += 60;
                try
                {
                    if (!sentOnce)
                    {
                        // 极少情况下目标未持焦点（例如焦点在别处），做一次显式交接
                        if (!SelectionReader.TargetHasKeyboardFocus(target))
                        {
                            session = new SelectionReader.FocusSession(target);
                            App.Log("focus handover: " + session.Detail);
                        }
                        beforeSeq = GetClipboardSequenceNumber();
                        InputSimulator.CtrlC();
                        sentOnce = true;
                        return;
                    }

                    uint seq = GetClipboardSequenceNumber();
                    string text = null;
                    try { text = Clipboard.GetText(); } catch { }
                    bool changed = seq != beforeSeq;

                    if (changed && !string.IsNullOrEmpty(text))
                    {
                        App.Log("capture ok: len=" + text.Length + " [" + Preview(text) + "] at " + elapsed + "ms");
                        finish();
                        PasteAsSend(text);
                        return;
                    }

                    if (changed)
                    {
                        // 剪贴板变了但内容为空：继续等
                        beforeSeq = seq;
                    }

                    if (retries < maxRounds && elapsed >= retryAfter)
                    {
                        retries++;
                        App.Log("capture retry " + retries + " at " + elapsed + "ms");
                        beforeSeq = GetClipboardSequenceNumber();
                        InputSimulator.CtrlC();
                        retryAfter = elapsed + 400;
                        return;
                    }

                    if (elapsed >= maxWait)
                    {
                        App.Log("capture gave up: seq=" + seq + " changed=" + changed +
                                " text=" + (text == null ? "NULL" : "len=" + text.Length));
                        finish();
                        PasteAsSend(null);
                    }
                }
                catch (Exception ex)
                {
                    App.Log("capture tick error: " + ex.Message);
                    finish();
                }
            };

            sentOnce = false;
            retries = 0;
            timer.Start();
        }

        static string Describe(string text)
        {
            if (text == null) return "NULL";
            return "len=" + text.Length + " [" + Preview(text) + "]";
        }

        static string Preview(string s)
        {
            if (s == null) return "";
            string t = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length <= 60 ? t : t.Substring(0, 60) + "...";
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        static string DescribeForeground()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                var t = new StringBuilder(200); GetWindowTextW(h, t, 200);
                var c = new StringBuilder(200); GetClassNameW(h, c, 200);
                uint pid; GetWindowThreadProcessId(h, out pid);
                return "hwnd=" + h + " pid=" + pid + " class=" + c + " title=[" + t + "]";
            }
            catch { return "(unknown)"; }
        }

        // 判断选区文本是否值得发问：过滤空白与单击误触
        static bool IsUsableSelection(string text)
        {
            if (text == null) return false;
            string t = text.Trim();
            if (t.Length < 2) return false;

            bool asciiOnly = true;
            int lettersOrDigits = 0;
            foreach (char c in t)
            {
                if (c >= 128) { asciiOnly = false; break; }
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    lettersOrDigits++;
            }
            // 纯英文/数字少于 3 个字符，视为误触
            if (asciiOnly && lettersOrDigits < 3) return false;
            return true;
        }

        void PasteAsSend(string text)
        {
            if (!IsUsableSelection(text))
            {
                if (!string.IsNullOrEmpty(text))
                    App.Log("selection too short, ignored: [" + text.Trim() + "]");
                return;
            }

            string t = text.Trim();
            if (t.Length > 3000) t = t.Substring(0, 3000);

            Input.Text = t;
            Input.CaretIndex = Input.Text.Length;
            App.Log("selection captured (" + t.Length + " chars)");

            if (autoSendOnSelect)
            {
                Input.Focus();
                Send();
            }
            else
            {
                Input.Focus();
                Keyboard.Focus(Input);
            }
        }

        // ---- 供自动化测试调用（--capture-test）----
        // 直接跑一次完整的「取选区 -> 填入 -> 发送」流程，验证核心链路。
        // 注意：必须异步等待，不能在 UI 线程上 Thread.Sleep，
        // 否则消息循环尚未启动就被阻塞，界面与输入都会卡死。
        public void RunCaptureTest()
        {
            App.Log("=== capture test start ===");
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += delegate
            {
                timer.Stop();
                IntPtr target = SelectionReader.TargetWindow;
                App.Log("capture test: target = " + SelectionReader.Describe(target));
                CaptureSelectionThenSend(target);
            };
            timer.Start();
        }

        // ---- 供自动化测试调用（--vocab-test）----
        // 用一段学术问答验证「抽取术语 -> 写入 学术词汇 目录」链路
        public void RunVocabTest()
        {
            string q = "文献里 ionic conductivity 和 grain boundary resistance 是什么意思？";
            string a =
                "**离子电导率（Ionic Conductivity）** 指材料传输离子的能力，单位 S/cm。\n" +
                "**晶界电阻（Grain Boundary Resistance）** 指晶粒界面阻碍离子迁移的阻抗，" +
                "在固态电解质中常主导总阻抗，可由 Nyquist 图的半圆直径估算。\n" +
                "两者关系类似「路宽」与「关卡」。";

            App.Log("=== vocab test start ===");
            mode = AssistantMode.Lit;
            ExtractAcademicVocab(q, a);
        }

        // ---- 供自动化测试调用（--md-test）----
        // 用真实 Markdown 样本走一遍渲染器，并检查生成的元素数量
        public void RunMarkdownTest()
        {
            string sample =
                "# 一级标题\n" +
                "## 二级标题\n\n" +
                "这是一段**加粗**文字，还有*斜体*和 `行内代码`。\n\n" +
                "- 无序项一\n" +
                "- 无序项二\n\n" +
                "1. 有序项一\n" +
                "2. 有序项二\n\n" +
                "> 这是一段引用文字\n\n" +
                "---\n\n" +
                "```\ncode line 1\ncode line 2\n```\n\n" +
                "| 列A | 列B |\n" +
                "| --- | --- |\n" +
                "| a1 | b1 |\n" +
                "| a2 | b2 |\n";

            App.Log("=== markdown test start ===");
            var el = MarkdownRenderer.Render(sample);
            var panel = el as StackPanel;
            int n = panel == null ? -1 : panel.Children.Count;
            App.Log("rendered blocks = " + n + " (expect 9: h1,h2,para,2 lists,quote,rule,code,table)");

            // 逐块报告类型，便于核对
            if (panel != null)
            {
                for (int i = 0; i < panel.Children.Count; i++)
                {
                    var c = panel.Children[i];
                    string kind = c.GetType().Name;
                    var tb = c as TextBlock;
                    if (tb != null) kind += " text=[" + Trim(tb.Text, 30) + "]";
                    else
                    {
                        var g = c as Grid;
                        if (g != null) kind += " children=" + g.Children.Count;
                        var b = c as Border;
                        if (b != null && b.Child != null) kind += " child=" + b.Child.GetType().Name;
                    }
                    App.Log("  [" + i + "] " + kind);
                }
            }

            // 真正放进界面，验证不会抛异常
            MsgPanel.Children.Clear();
            AddBubbleMarkdown("assistant", sample);
            App.Log("markdown test: added to UI ok, bubbles=" + MsgPanel.Children.Count);
            App.Log("=== markdown test end ===");
        }

        // ---- 供自动化测试调用（--summarize-test）----
        // 灌入一段生活助手对话并立即触发总结，用于验证「总结 -> 落盘 -> 回读」闭环
        public void RunSummarizeTest()
        {
            mode = AssistantMode.Life;
            litHistory.Clear();
            lifeHistory.Clear();

            string[,] seed = new string[,] {
                { "user", "我下周三要带爸妈去杭州玩三天，帮我安排一下行程" },
                { "assistant", "建议第一天西湖+灵隐寺，第二天西溪湿地+宋城，第三天京杭大运河+河坊街。爸妈年纪大，每天安排两个点就够，中午留出休息时间。" },
                { "user", "我爸膝盖不好，走不了太多路，而且我妈吃素" },
                { "assistant", "那要减少步行：西湖改坐游船+观光车，灵隐寺有索道。吃饭可选功德林、枣子树这类素菜馆，杭州有不少。酒店建议选在西湖边，减少通勤。" },
                { "user", "好，就按这个来，酒店预算一晚 600 以内" }
            };
            for (int i = 0; i < seed.GetLength(0); i++)
            {
                lifeHistory.Add(new object[] { seed[i, 0],
                    new Dictionary<string, object> { { "role", seed[i, 0] }, { "content", seed[i, 1] } } });
            }
            lifeDirty = true;
            App.Log("=== summarize test start, seeded msgs=" + lifeHistory.Count + " ===");
            SummarizeLifeSession("测试", null);
        }

        // ---- 供自动化测试调用（--memory-preview）----
        public void RunMemoryPreview()
        {
            mode = AssistantMode.Life;
            LoadLongTermMemory();
            App.Log("=== memory preview ===");
            App.Log("file=" + RollingMemoryFile + " exists=" + File.Exists(RollingMemoryFile));
            App.Log("rollingMemory len=" + (rollingMemory == null ? 0 : rollingMemory.Length));
            if (rollingMemory != null) App.Log("content >>>\n" + rollingMemory + "\n<<< end");
        }

        // 供自动化测试调用（--input-test）----
        // 在输入框里跑一次「聚焦 -> 输入 -> 读回」，验证输入链路是否通
        public bool InputDiag = false;

        // ---- 供自动化测试调用（--focus-test）----
        // 验证「唤出浮窗不抢焦点」：记录唤出前后的前台窗口，结束后应保持不变。
        public void RunFocusTest()
        {
            App.Log("=== focus test start ===");
            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(2);
            t.Tick += delegate
            {
                t.Stop();
                IntPtr me = new WindowInteropHelper(this).Handle;
                IntPtr before = SelectionReader.ForegroundWindow;
                App.Log("focus test: before toggle, visible=" + IsVisible +
                        " foreground=" + before + " ours=" + me +
                        " isOurs=" + (before == me));

                if (IsVisible) Hide();
                ToggleVisibility();

                var t2 = new DispatcherTimer();
                t2.Interval = TimeSpan.FromSeconds(1.5);
                t2.Tick += delegate
                {
                    t2.Stop();
                    IntPtr after = SelectionReader.ForegroundWindow;
                    bool ok = before == IntPtr.Zero || after == before || after != me;
                    App.Log("focus test: after toggle, visible=" + IsVisible +
                            " foreground=" + after +
                            " keptOriginal=" + (after == before) +
                            " stoleFocus=" + (after == me));
                    App.Log("focus test RESULT: " + (ok ? "PASS (focus not stolen)" : "FAIL (focus stolen)"));
                    App.Log("=== focus test end ===");
                };
                t2.Start();
            };
            t.Start();
        }

        // ---- 供自动化测试调用（--marks-test）----
        // 灌入多条消息后检查消息锚点标尺是否真的渲染出了刻度
        public void RunMarksTest()
        {
            App.Log("=== marks test start ===");
            MsgPanel.Children.Clear();
            for (int i = 1; i <= 8; i++)
            {
                AddBubble("user", "测试问题 " + i + "：请解释一个术语");
                AddBubbleMarkdown("assistant",
                    "### 术语 " + i + "\n\n这是第 " + i + " 条回答，用于把对话区撑高。" +
                    "**加粗**、`代码`、以及若干填充文字，确保内容超出视口从而出现滚动条。\n");
            }

            var t = new DispatcherTimer();
            t.Interval = TimeSpan.FromSeconds(2);
            t.Tick += delegate
            {
                t.Stop();
                App.Log("marks test: bubbles=" + MsgPanel.Children.Count +
                        " scrollable=" + Scroller.ScrollableHeight.ToString("0") +
                        " canvasH=" + MsgMarks.ActualHeight.ToString("0") +
                        " marks=" + MsgMarks.Children.Count +
                        " offsets=" + markOffsets.Count +
                        " scrollTops=" + markScrollTops.Count);

                // 跳转正确性自检：跳到中间那一轮，等缓动结束后检查提问是否可见
                int mid = markOffsets.Count / 2;
                if (mid < markOffsets.Count)
                {
                    double qTop = markOffsets[mid];
                    double target = markScrollTops[mid];
                    if (smooth != null) smooth.ScrollTo(target, false);

                    var t2 = new DispatcherTimer();
                    t2.Interval = TimeSpan.FromSeconds(1.5);
                    t2.Tick += delegate
                    {
                        t2.Stop();
                        double off = Scroller.VerticalOffset;
                        double vp = Scroller.ViewportHeight;
                        // 提问应停在视野内（顶部附近），回答在其下方接续
                        bool qVisible = qTop >= off - 2 && qTop <= off + vp - 4;
                        bool qNearTop = Math.Abs(qTop - off) <= 24;
                        App.Log("marks test jump: round=" + mid +
                                " questionTop=" + qTop.ToString("0") +
                                " target=" + target.ToString("0") +
                                " landedOffset=" + off.ToString("0") +
                                " viewport=" + vp.ToString("0") +
                                " -> questionVisible=" + qVisible +
                                " questionAtTop=" + qNearTop);
                        App.Log("=== marks test end ===");
                    };
                    t2.Start();
                }
                else App.Log("=== marks test end ===");
            };
            t.Start();
        }

        // ---- 供自动化测试调用（--termbook-test）----
        public void RunTermBookTest()
        {
            App.Log("=== termbook test start ===");
            var b = AddBubbleWith("assistant", new TextBlock
            {
                Text = "### 测试术语\n\nionic conductivity | 离子电导率，材料传输离子的能力。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("Ink")
            });
            mode = AssistantMode.Lit;
            AddFavoriteRow(b);
            App.Log("favorite row added, children of bubble=" +
                    (b.Child is Panel ? ((Panel)b.Child).Children.Count : -1));

            // 真的触发一次收藏，验证「按钮 -> 落盘 -> 术语本读取」完整链路
            var btn = FindFavoriteButton(b);
            if (btn != null)
            {
                btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                App.Log("favorite button clicked");
            }
            else App.Log("favorite button NOT FOUND in bubble");

            ToggleTermPanel();
            App.Log("termbook OPEN: panelVisible=" + termPanelVisible +
                    " items=" + TermList.Children.Count +
                    " inputBar=" + InputBar.Visibility +
                    " termRowSpan=" + Grid.GetRowSpan(TermPanel) +
                    " termBg=" + ((SolidColorBrush)TermPanel.Background).Color);

            // 关闭路径也必须恢复操作栏，否则输入框会一直不可见
            CloseTermPanel();
            App.Log("termbook CLOSE: panelVisible=" + termPanelVisible +
                    " termPanel=" + TermPanel.Visibility +
                    " inputBar=" + InputBar.Visibility +
                    " termRowSpan=" + Grid.GetRowSpan(TermPanel) +
                    " termBtn=" + ((SolidColorBrush)BtnTerm.Background).Color);
            App.Log("=== termbook test end ===");
        }

        // ---- 供自动化测试调用（--termtest2）----
        // 完整验证：收藏只存术语名 -> 术语本只显示术语名 -> 删除按钮生效
        public void RunTermFlowTest()
        {
            App.Log("=== term flow test start ===");
            mode = AssistantMode.Lit;

            // 1) 模拟一轮问答：先加用户提问，再加助手回答
            //    术语名应从【提问气泡】取，而不是从回答里猜
            AddBubble("user", "grain boundary resistance 是什么意思？");
            var b = AddBubbleMarkdown("assistant",
                "### 晶界电阻\n\n晶界界面阻碍离子迁移的阻抗，常主导固态电解质总阻抗。\n\n" +
                "— 1242 tok · 18.4s · 67.5 tok/s");
            AddFavoriteRow(b);
            App.Log("LastUserQuestion -> [" + LastUserQuestion() + "]");
            App.Log("CleanTerm        -> [" + CleanTerm(LastUserQuestion()) + "]");
            var fav = FindFavoriteButton(b);
            if (fav != null) fav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // 2) 再手工追加一条，验证多条目与删除
            string dir = IoPath.Combine(MemRoot, "学术词汇");
            Directory.CreateDirectory(dir);
            File.AppendAllText(IoPath.Combine(dir, "我的收藏.md"), "- [[ionic conductivity]]\n", Encoding.UTF8);

            // 3) 打开术语本，核对显示的是「术语名」而非整段回答
            ToggleTermPanel();
            int chips = 0;
            var names = new List<string>();
            foreach (UIElement el in TermList.Children)
            {
                var g = el as Grid;
                if (g == null) continue;
                chips++;
                foreach (UIElement c in g.Children)
                {
                    var bb = c as Button;
                    if (bb != null && bb.Content != null && !"✕".Equals(bb.Content.ToString()))
                        names.Add(bb.Content.ToString());
                }
            }
            App.Log("term flow: chips=" + chips + " names=[" + string.Join(" | ", names.ToArray()) + "]");

            // 4) 触发第一个术语的删除按钮
            string favFile = IoPath.Combine(dir, "我的收藏.md");
            int linesBefore = File.ReadAllLines(favFile, Encoding.UTF8).Length;
            if (names.Count > 0)
            {
                DeleteTerm(favFile, names[0]);
                int linesAfter = File.ReadAllLines(favFile, Encoding.UTF8).Length;
                App.Log("term flow delete: fileLines " + linesBefore + " -> " + linesAfter +
                        "  remaining chips=" + TermList.Children.Count);
            }
            App.Log("favorite file content >>>\n" + File.ReadAllText(favFile, Encoding.UTF8) + "<<<");
            App.Log("=== term flow test end ===");
        }

        // 在气泡里找那个「★ 收藏」按钮
        Button FindFavoriteButton(DependencyObject root)
        {
            if (root == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                var b = c as Button;
                if (b != null && b.Content != null && b.Content.ToString().IndexOf("收藏") >= 0) return b;
                var r = FindFavoriteButton(c);
                if (r != null) return r;
            }
            return null;
        }

        public void RunInputTest()
        {
            InputDiag = true;
            App.Log("=== input test start ===");
            Dispatcher.BeginInvoke(new Action(delegate
            {
                Input.Focus();
                Keyboard.Focus(Input);
                App.Log("input focused; Input.IsFocused=" + Input.IsFocused +
                        " window.IsActive=" + IsActive + " window visible=" + IsVisible);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- 供自动化测试调用（--set-active）----
        public void SetActiveSelection()
        {
            try
            {
                Input.Focus();
                Keyboard.Focus(Input);
                Input.SelectAll();
                App.Log("active selection set, len=" + (Input.Text == null ? 0 : Input.Text.Length));
            }
            catch (Exception ex) { App.Log("set-active failed: " + ex.Message); }
        }

        void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            autoPaste = !autoPaste;
            RefreshToggles();
            App.Log("auto-paste toggled: " + (autoPaste ? "ON" : "OFF"));
        }

        // ================= 窗口尺寸 / 定位 =================
        // 默认：屏幕 1/3 宽 × 1/3 高，停靠右下角（比 1/16 更实用，回答不用频繁滚动）
        void SizeToDefault()
        {
            var wa = SystemParameters.WorkArea;
            Width = Math.Max(360, Math.Round(wa.Width / 3));
            Height = Math.Max(260, Math.Round(wa.Height / 3));
        }

        void PositionBottomRight()
        {
            var wa = SystemParameters.WorkArea;
            const double margin = 12;
            Left = wa.Right - Width - margin;
            Top = wa.Bottom - Height - margin;
        }

        // 记忆窗口大小与位置，下次启动还原
        string ConfigFile
        {
            get
            {
                string dir = IoPath.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                return IoPath.Combine(dir, "window.txt");
            }
        }

        void LoadGeometry()
        {
            try
            {
                if (!File.Exists(ConfigFile)) { SizeToDefault(); PositionBottomRight(); return; }
                string[] parts = File.ReadAllText(ConfigFile).Split(',');
                if (parts.Length != 4) { SizeToDefault(); PositionBottomRight(); return; }
                double w = double.Parse(parts[0]), h = double.Parse(parts[1]);
                double l = double.Parse(parts[2]), t = double.Parse(parts[3]);
                var wa = SystemParameters.WorkArea;
                if (w < 360 || h < 260 || w > wa.Width || h > wa.Height) { SizeToDefault(); PositionBottomRight(); return; }
                Width = w; Height = h;
                // 位置越界则回到右下角
                if (l < wa.Left - 40 || t < wa.Top - 40 || l > wa.Right - 80 || t > wa.Bottom - 60)
                    PositionBottomRight();
                else { Left = l; Top = t; }
            }
            catch { SizeToDefault(); PositionBottomRight(); }
        }

        void SaveGeometry()
        {
            try
            {
                string line = Width.ToString("0") + "," + Height.ToString("0") + "," +
                              Left.ToString("0") + "," + Top.ToString("0");
                File.WriteAllText(ConfigFile, line);
                App.Log("geometry saved to " + ConfigFile + " = " + line);
            }
            catch (Exception ex)
            {
                App.Log("geometry save FAILED: " + ex.Message);
            }
        }

        // ================= 输入草稿保留 =================
        // 隐藏窗口或退出时保存未发送的输入，下次打开还在，避免手打的内容丢失
        string DraftFile
        {
            get
            {
                string dir = IoPath.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                return IoPath.Combine(dir, "draft.txt");
            }
        }

        void SaveDraft()
        {
            try
            {
                string t = Input.Text;
                if (string.IsNullOrEmpty(t)) { if (File.Exists(DraftFile)) File.Delete(DraftFile); return; }
                File.WriteAllText(DraftFile, t);
            }
            catch { }
        }

        void LoadDraft()
        {
            try
            {
                if (!File.Exists(DraftFile)) return;
                string t = File.ReadAllText(DraftFile);
                if (!string.IsNullOrEmpty(t)) { Input.Text = t; Input.CaretIndex = t.Length; }
            }
            catch { }
        }

        void ClearDraft()
        {
            try { if (File.Exists(DraftFile)) File.Delete(DraftFile); } catch { }
        }

        // ================= 全局热键 =================
        void RegisterHotkey()
        {
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.EnsureHandle();
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(HwndHook);

            // 依次尝试：Alt+Z（首选）→ Win+Z → Ctrl+Alt+Z → Ctrl+Shift+Z
            // Win+Z 通常被 Windows 贴靠布局独占
            if (TryRegister("Alt+Z", MOD_ALT, VK_Z)) return;
            if (TryRegister("Win+Z", MOD_WIN, VK_Z)) return;
            if (TryRegister("Ctrl+Alt+Z", MOD_CONTROL | MOD_ALT, VK_Z)) return;
            if (TryRegister("Ctrl+Shift+Z", MOD_CONTROL | MOD_SHIFT, VK_Z)) return;
            hotkeyOk = false; hotkeyLabel = "热键注册失败";
            App.Log("all hotkey candidates failed");
        }

        bool TryRegister(string label, uint mods, uint vk)
        {
            var helper = new WindowInteropHelper(this);
            if (RegisterHotKey(helper.EnsureHandle(), HOTKEY_ID, mods, vk))
            {
                hotkeyOk = true; hotkeyLabel = label;
                App.Log("hotkey registered: " + label);
                return true;
            }
            App.Log("hotkey " + label + " unavailable (err " + Marshal.GetLastWin32Error() + ")");
            return false;
        }

        IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleVisibility();
                handled = true;
            }
            return IntPtr.Zero;
        }

        void ToggleVisibility()
        {
            if (IsVisible)
            {
                SaveDraft();      // 保留未发送的输入
                Hide();
                return;
            }

            // 在取文本之前记录当前前台窗口（就是用户正在读的浏览器/PDF），
            // 取文本流程会激活目标程序，之后再取就晚了。
            IntPtr prevFg = SelectionReader.TargetWindow;
            if (prevFg != new WindowInteropHelper(this).Handle) focusReturnTarget = prevFg;

            // 唤出时若刚在别的程序里划过词，主动去取一次选区
            if (autoPaste && !busy && !grabbing && lastDragTarget != IntPtr.Zero)
                TryCaptureFromLastTarget();

            LoadDraft();          // 恢复上次未发送的输入
            Show();
            // 关键：只把窗口抬到最上层，不抢键盘焦点 ——
            // 这样唤出浮窗后，焦点仍留在浏览器/PDF，用户可以继续在原文里操作。
            SelectionReader.ShowNoActivate(new WindowInteropHelper(this).Handle);
            // 窗口可能在屏幕外（分辨率变化），重新摆正
            var wa = SystemParameters.WorkArea;
            if (Left < wa.Left - 50 || Left > wa.Right - 100 || Top < wa.Top - 50 || Top > wa.Bottom - 100)
                PositionBottomRight();
            SelectInputText();
            ReturnFocusToPrevious();
        }

        // 只选中输入框内容，不把焦点留在浮窗
        void SelectInputText()
        {
            try
            {
                if (string.IsNullOrEmpty(Input.Text)) return;
                Input.Focus();
                Input.SelectAll();
            }
            catch { }
        }

        // 把键盘焦点还给「浮窗出现前的前台窗口」。
        // 记录时机很关键：必须在 PerformCapture 取文本之前记录，
        // 因为取文本流程本身会激活目标程序，之后 GetForegroundWindow 就变成了目标程序。
        IntPtr focusReturnTarget = IntPtr.Zero;

        // 进程启动、窗口创建之前的前台窗口（由 App.Main 写入）
        public static IntPtr PreStartForeground = IntPtr.Zero;

        void ReturnFocusToPrevious()
        {
            try
            {
                if (focusReturnTarget == IntPtr.Zero) return;
                if (focusReturnTarget == new WindowInteropHelper(this).Handle) return;
                SelectionReader.ActivateWindow(focusReturnTarget);
                App.Log("focus returned to " + focusReturnTarget);
            }
            catch { }
        }

        // 热键路径：把选区取回来填进输入框（取到就替换输入框内容，供确认后发送）
        void TryCaptureFromLastTarget()
        {
            grabbing = true;
            IntPtr target = lastDragTarget;
            lastDragTarget = IntPtr.Zero;      // 只尝试一次，避免反复触发

            var t = new Thread(delegate()
            {
                string text = null;
                try
                {
                    SelectionReader.ActivateWindow(target);
                    Thread.Sleep(60);
                    using (new SelectionReader.FocusSession(target))
                    {
                        text = SelectionReader.GrabViaCtrlC(2, 500);
                    }
                }
                catch (Exception ex) { App.Log("hotkey grab threw: " + ex.Message); }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    grabbing = false;
                    App.Log("hotkey grab: " + Describe(text));
                    if (!string.IsNullOrEmpty(text)) PasteAsSend(text);
                    // 无论是否取到，都把焦点还给原来的程序
                    ReturnFocusToPrevious();
                }));
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        // 设计取舍（实测得出）：
        //  A) WS_EX_NOACTIVATE：浮窗不抢焦点，浏览器保持焦点，但代价是浮窗无法打字（已实测失效）—— 弃用。
        //  B) 正常可激活：打字正常；取文本时主动把自己压到最底层并做焦点交接，
        //     让目标程序重新成为前台，Ctrl+C 才会被浏览器接受。
        // 采用 B。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var h = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(CloseHook);
        }

        IntPtr CloseHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLOSE)
            {
                SaveGeometry();
                SaveDraft();
                Hide();
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 记住窗口大小与位置，下次启动还原
            SaveGeometry();
            SaveDraft();
            // 点关闭只是隐藏，保持热键可用
            e.Cancel = true;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (hotkeyOk) UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID);
            base.OnClosed(e);
        }

        // ================= 界面辅助 =================
        // 新粗野主义配色：开关态用「高饱和底色 + 黑字」表达，而不是改字体颜色 ——
        // 黄底上放蓝字对比度不足，且不符合该风格的直白表达。
        Brush Res(string key) { return (Brush)FindResource(key); }

        void SetToggle(Button b, bool on, string onText, string offText)
        {
            b.Background = on ? Res("Yellow") : Res("Paper");
            b.Foreground = Res("Ink");
            if (onText != null) b.Content = on ? onText : offText;
        }

        void RefreshToggles()
        {
            SetToggle(BtnThink, thinkMode, null, null);
            SetToggle(BtnVision, visionMode, null, null);
            SetToggle(BtnPaste, autoPaste, "划词·开", "划词·关");
            SetToggle(BtnMode, mode == AssistantMode.Life, "生活", "文献");
            TxtMode.Text = mode == AssistantMode.Life ? "生活助手" : "文献术语";
            TxtStatus.Text = hotkeyLabel + (thinkMode ? " · 思考" : " · 快答") + (visionMode ? " · 视觉" : "");
        }

        void Greet()
        {
            if (mode == AssistantMode.Life)
            {
                string mem = string.IsNullOrEmpty(rollingMemory)
                    ? "（暂无长期记忆，本次对话结束后会自动建立）"
                    : "（已载入跨会话长期记忆）";
                AddBubble("assistant",
                    "生活助手模式。日常问题都可以问我：饮食、健康、出行、购物、计划、写作等。\n" +
                    "· 上下文与文献模式完全独立\n" +
                    "· 本次对话结束（空闲 " + idleMinutes + " 分钟或点「新对话」）后会自动总结，" +
                    "并存入 " + IoPath.Combine(MemRoot, "生活") + "\\ " + mem);
                return;
            }

            AddBubble("assistant",
                "你好，我是文献术语助手。\n" +
                "· " + hotkeyLabel + " 唤出 / 收起，Esc 收起\n" +
                "· 拖动选中文字即自动提问（浏览器 / Word 有效）\n" +
                "· Ctrl+V 粘贴截图，或点「图」导入图片\n" +
                "· 点「文献 / 生活」切换模式，两者上下文独立");
        }

        UIElement AddBubble(string role, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("Ink"),
                FontSize = 13,
                LineHeight = 20
            };
            return AddBubbleWith(role, tb);
        }

        // 用 Markdown 渲染后的内容加气泡
        // 返回气泡本体（Border），调用方如需流式更新可取其 Child
        Border AddBubbleMarkdown(string role, string md)
        {
            return AddBubbleWith(role, new TextBlock
            {
                Text = md,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("Ink"),
                FontSize = 13,
                LineHeight = 20
            });
        }

        Border AddBubbleWith(string role, UIElement content)
        {
            bool isUser = role == "user";
            Border body;
            var g = BuildBrutalBubble(isUser, content, out body);
            g.Tag = isUser ? "user" : "assistant";   // 锚点标尺据此只给提问建点
            MsgPanel.Children.Add(g);
            ScrollToEnd();
            return body;
        }

        // 新粗野主义气泡：硬边缘矩形 + 3px 实线黑边 + 硬偏移阴影（无模糊、无圆角）
        Grid BuildBrutalBubble(bool isUser, UIElement content, out Border bodyOut)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var shadow = new Border { Background = Res("Ink") };
            // 用户气泡 = 亮黄底黑字（你要的改动）；助手气泡 = 白底黑字保证长文可读
            var body = new Border
            {
                Background = isUser ? Res("Yellow") : Res("Paper"),
                BorderBrush = Res("Ink"),
                BorderThickness = new Thickness(3),
                Padding = new Thickness(11, 8, 11, 8),
                Child = content
            };
            bodyOut = body;

            // 左侧留 3px 给阴影、右侧留 3px；用户消息右对齐，助手左对齐
            int bodyCol = isUser ? 2 : 0;
            int shadowCol = isUser ? 2 : 1;   // 阴影永远在本体的右下（偏移方向）

            Grid.SetRow(shadow, 0); Grid.SetColumn(shadow, shadowCol);
            Grid.SetRowSpan(shadow, 2);
            Grid.SetRow(body, 0); Grid.SetColumn(body, bodyCol);
            Grid.SetRowSpan(body, 2);

            g.Margin = new Thickness(isUser ? 40 : 0, 5, isUser ? 3 : 40, 5);

            g.Children.Add(shadow);
            g.Children.Add(body);

            ApplyBubbleWidth(body);
            return g;
        }

        // 气泡宽度：不能用 ActualWidth - 100 —— 布局完成前 ActualWidth 为 0，
        // 会把 MaxWidth 退化成很小的常量，回答会被挤成窄条。
        void ApplyBubbleWidth(FrameworkElement bubble)
        {
            double avail = Scroller.ActualWidth;
            if (avail <= 0) avail = Width - 40;
            if (avail <= 0) avail = 420;
            // 扣掉：左右外边距 + 3px 边框 ×2 + 3px 阴影 + 内边距
            bubble.MaxWidth = Math.Max(180, avail - 60);
        }

        void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            foreach (UIElement el in MsgPanel.Children)
            {
                var grid = el as Grid;
                if (grid == null) continue;
                foreach (UIElement child in grid.Children)
                {
                    var b = child as Border;
                    if (b != null && b.Child != null) ApplyBubbleWidth(b);
                }
            }
        }

        // 把流式阶段的纯文本气泡替换为 Markdown 渲染结果
        void RenderFinalAnswer(Border bubble, string md)
        {
            try
            {
                bubble.Child = MarkdownRenderer.Render(md);
                bubble.Background = Res("Paper");
                bubble.Padding = new Thickness(10, 7, 10, 7);
                ApplyBubbleWidth(bubble);
            }
            catch (Exception ex)
            {
                App.Log("markdown render failed: " + ex.Message);
                bubble.Child = new TextBlock
                {
                    Text = md,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("Ink")
                };
            }
        }

        // 平滑跟随到底部（带非线性缓动），而不是硬跳
        void ScrollToEnd()
        {
            if (smooth != null) smooth.FollowEnd();
        }

        SmoothScroll smooth;

        void InitSmoothScroll()
        {
            try
            {
                smooth = new SmoothScroll(Scroller);
                smooth.EnsureAttached();
                smooth.JumpToEnd();
                // 布局完成后让平滑滚动有机会补上「贴底」（新气泡的高度此时才确定）
                Scroller.LayoutUpdated += delegate
                {
                    if (smooth != null) smooth.OnLayoutUpdated();
                };
                App.Log("smooth scroll ready");
            }
            catch (Exception ex) { App.Log("smooth scroll init failed: " + ex.Message); }
        }

        // 等待指示：三个高饱和方块（非圆点），符合硬边几何风格
        void ShowBusy()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            string[] cols = { "Yellow", "Pink", "Mint" };
            for (int i = 0; i < 3; i++)
            {
                var sq = new Border
                {
                    Width = 11, Height = 11,
                    Background = Res(cols[i]),
                    BorderBrush = Res("Ink"),
                    BorderThickness = new Thickness(2),
                    Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0)
                };
                sp.Children.Add(sq);
            }
            var g = BuildBrutalBubble(false, sp, out _dummyBody);
            g.Name = "BusyBubble";
            MsgPanel.Children.Add(g);
            ScrollToEnd();
        }

        // ================= 收藏术语 (a) =================
        // 助手回答下方加「★ 收藏」按钮。点击后弹小窗确认术语名，
        // 只把【术语本身】存进 我的收藏.md —— 之前存整段回答，导致术语本里显示一整篇答案。
        void AddFavoriteRow(Border bubble)
        {
            if (bubble == null) return;
            try
            {
                // 取「干净的答案文本」用于抽术语：排除末尾的性能统计行
                string content = ExtractAnswerText(bubble);
                if (string.IsNullOrEmpty(content) || content.Trim().Length < 4) return;

                // 术语名从【用户提问气泡】里取，而不是从回答里猜 —— 提问本身就包含术语，最可靠。
                // 注意必须在生成按钮点击时取，不能在这里取：那时最新提问就是本条回答对应的那个。
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 7, 0, 0)
                };
                var btn = new Button
                {
                    Content = "★ 收藏",
                    Style = (Style)FindResource("BrutalButton"),
                    // 亮黄：白色助手气泡上最醒目的行动色，且与「黄=重要/激活」的既有语义一致
                    Background = Res("Yellow"),
                    Foreground = Res("Ink"),
                    ToolTip = "把提问里的术语存入术语本"
                };
                string captured = content;
                btn.Click += delegate
                {
                    // 实时从提问气泡抓术语；抓不到再退回回答里猜
                    string q = LastUserQuestion();
                    string term = CleanTerm(q);
                    if (string.IsNullOrEmpty(term)) term = GuessTerm(captured);
                    FavoriteTerm(term, btn);
                };
                row.Children.Add(btn);

                // 把原内容与按钮行包进一个 StackPanel。
                // 注意顺序：必须先把旧 Child 从 bubble 上脱钩，再挂进新容器，
                // 否则会抛「元素已经是另一个元素的逻辑子元素」。
                UIElement old = bubble.Child;
                bubble.Child = null;
                var wrap = new StackPanel();
                wrap.Children.Add(old);
                wrap.Children.Add(row);
                bubble.Child = wrap;
            }
            catch (Exception ex) { App.Log("add favorite row failed: " + ex.Message); }
        }

        // 从回答文本里猜术语名。
        // 必须排除：
        //   末尾的性能统计行（"— 1234 tok · 12.3s · 45.6 tok/s"）—— 否则会被当成术语名，
        //   表现为术语本里显示成 token 数；
        //   Markdown 符号、列表符、以及过长的句子。
        // 取「最近一条用户提问」的文本。
        // 术语名直接从提问里抓最可靠：提问本身就包含术语，不用从回答里猜。
        string LastUserQuestion()
        {
            try
            {
                for (int i = MsgPanel.Children.Count - 1; i >= 0; i--)
                {
                    var fe = MsgPanel.Children[i] as FrameworkElement;
                    if (fe == null) continue;
                    if (fe.Tag == null || (string)fe.Tag != "user") continue;
                    // 用户气泡结构：Grid -> [阴影 Border, 本体 Border]，取本体里的文本
                    string s = ExtractText(fe);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception ex) { App.Log("LastUserQuestion failed: " + ex.Message); }
            return "";
        }

        // 把提问整理成「术语本身」：去掉疑问句式与句尾标点
        string CleanTerm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();

            // 去掉常见提问套话（前缀）
            s = Regex.Replace(s, @"^(请问|请帮我|帮我|麻烦|想请问|想问一下|请问一下|解释一下|解释|什么是|啥是|什么叫|何为)\s*", "");
            // 去掉常见提问套话（后缀）
            s = Regex.Replace(s, @"\s*(是什么意思|什么意思|的含义是什么|的含义|的定义是什么|的定义|指的是什么|指什么|是啥|是什么|怎么理解|如何理解|有何含义)\s*$", "");
            // 去掉句尾标点
            s = s.TrimEnd('?', '？', '。', '.', '!', '！', '~', '～', ' ', '\u3000');

            // 若是「术语 | 释义」形式，只取术语
            int bar = s.IndexOf('|');
            if (bar > 0) s = s.Substring(0, bar).Trim();

            // 中英分界：切掉跟在英文术语后的中文解释
            int zh = IndexOfLeadingChineseTail(s);
            if (zh > 0) s = s.Substring(0, zh).Trim();

            s = TrimLeadingDecorations(s);
            s = s.TrimEnd('：', ':', '，', ',', '。', '.', '、', ' ');
            return s;
        }

        // 术语名截断用的分隔符/标点：出现任意一个就认为「术语部分结束」
        static readonly char[] Separators = new char[] {
            '|', '：', ':', '，', ',', '。', '；', ';', '（', '(', '）', ')',
            '、', '—', '–', '\t', '　'
        };

        // 去掉首位的中文序数词与序号（"1. "、"第一类"、"一、"）
        static string TrimLeadingDecorations(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            // 前导数字加点/顿号，例如 "1. " "2、"
            int i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '、' || s[i] == ' ')) i++;
            if (i > 0 && i < s.Length && (char.IsLetter(s[i]))) s = s.Substring(i).Trim();
            // 去掉「第一类」「第2种」这类前缀
            if (s.StartsWith("第"))
            {
                int j = 1;
                while (j < s.Length && !char.IsLetter(s[j])) j++;
                if (j < s.Length && "类种个条".IndexOf(s[j]) >= 0) s = s.Substring(j + 1).Trim();
            }
            return s;
        }

        // 找「以中文开头的尾部」的起点：用于在无标点分隔时切掉中文解释。
        // "ionic conductivity 是指…" -> 返回 "是" 的下标；
        // "钙钛矿"（整串中文）-> 返回 -1，表示不该截断。
        static int IndexOfLeadingChineseTail(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (IsChinese(s[i]))
                {
                    // 前面必须有实质内容（字母/数字）才值得截断
                    for (int j = 0; j < i; j++)
                    {
                        if (char.IsLetterOrDigit(s[j]) && !IsChinese(s[j])) return i;
                    }
                    return -1;
                }
            }
            return -1;
        }

        static bool IsChinese(char c)
        {
            return c >= 0x4E00 && c <= 0x9FFF;
        }

        // 从回答文本里猜术语名。
        // 必须排除：
        //   末尾的性能统计行（"— 1234 tok · 12.3s · 45.6 tok/s"）—— 否则会被当成术语名，
        //   表现为术语本里显示成 token 数；
        //   Markdown 符号、列表符、以及术语后面的中文解释。
        string GuessTerm(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";

            foreach (string raw in content.Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;

                // 跳过性能统计行
                if (t.StartsWith("—") || t.StartsWith("--")) continue;
                if (t.IndexOf(" tok/s") >= 0 || t.IndexOf(" tok ·") >= 0) continue;

                // 去掉标题/引用/列表标记与强调符
                t = t.TrimStart('#', '*', '-', '+', '>', ' ').Trim();
                t = t.Replace("**", "").Replace("`", "").Trim();
                if (t.Length == 0) continue;

                // 只保留术语本身：遇到任何分隔符或中文标点就截断，
                // 这样 "grain boundary resistance：晶界电阻，指…" 只会留下英文术语。
                // 之前给冒号加了 colon<=20 的长度限制，中文解释的冒号常在 20 字之后，
                // 所以没截掉，术语名就带上了中文解释。
                int cut = -1;
                foreach (char sep in Separators)
                {
                    int p = t.IndexOf(sep);
                    if (p > 0 && (cut < 0 || p < cut)) cut = p;
                }
                if (cut > 0) t = t.Substring(0, cut).Trim();

                // 去掉首位的中文序数词/修饰词（"1. 钙钛矿" -> "钙钛矿"，"第一类…" -> "…"）
                t = TrimLeadingDecorations(t);
                if (t.Length == 0) continue;

                // 中英混排时在「非中文开头」处截断：
                // 典型是 "ionic conductivity 是指材料传输离子的能力" —— 中间没有标点，
                // 只能靠字符类型分界切出英文术语。若整串以中文开头则原样保留（如「钙钛矿」）。
                int zhStart = IndexOfLeadingChineseTail(t);
                if (zhStart > 0) t = t.Substring(0, zhStart).Trim();

                t = t.TrimEnd('.', '．', '、', ',', '，', ';', '；', ':', '：', '\u3000', ' ');
                if (t.Length == 0 || t.Length > 40) continue;

                // 至少包含一个字母或汉字，排除纯符号
                bool hasWord = false;
                foreach (char c in t)
                {
                    if (char.IsLetter(c)) { hasWord = true; break; }
                }
                if (!hasWord) continue;

                return t;
            }
            return "";
        }

        // 从气泡元素里取「干净的答案文本」用于抽术语：
        // 跳过末尾的性能统计行（它是单独一个 TextBlock），其余段落拼起来。
        string ExtractAnswerText(Border bubble)
        {
            if (bubble == null) return "";
            var sb = new StringBuilder();
            CollectAnswerText(bubble.Child, sb);
            return sb.ToString().Trim();
        }

        void CollectAnswerText(UIElement el, StringBuilder sb)
        {
            if (el == null) return;
            var tb = el as TextBlock;
            if (tb != null)
            {
                var line = new StringBuilder();
                foreach (Inline i in tb.Inlines)
                {
                    var r = i as Run;
                    if (r != null) line.Append(r.Text);
                }
                if (tb.Inlines.Count == 0 && !string.IsNullOrEmpty(tb.Text)) line.Append(tb.Text);
                string s = line.ToString().Trim();
                // 丢弃性能统计行
                if (s.Length > 0 && !(s.StartsWith("—") && s.IndexOf(" tok") >= 0))
                    sb.Append(s).Append('\n');
                return;
            }
            var panel = el as Panel;
            if (panel != null)
            {
                foreach (UIElement c in panel.Children) CollectAnswerText(c, sb);
                return;
            }
            var border = el as Border;
            if (border != null) CollectAnswerText(border.Child, sb);
        }

        // 取 UIElement 里的纯文本（递归处理 StackPanel / Grid / TextBlock）
        string ExtractText(UIElement el)
        {
            var sb = new StringBuilder();
            CollectText(el, sb);
            return sb.ToString();
        }

        void CollectText(UIElement el, StringBuilder sb)
        {
            if (el == null) return;
            var tb = el as TextBlock;
            if (tb != null)
            {
                foreach (Inline i in tb.Inlines)
                {
                    var r = i as Run;
                    if (r != null) sb.Append(r.Text);
                }
                if (tb.Inlines.Count == 0 && !string.IsNullOrEmpty(tb.Text)) sb.Append(tb.Text);
                sb.Append('\n');
                return;
            }
            var panel = el as Panel;
            if (panel != null)
            {
                foreach (UIElement c in panel.Children) CollectText(c, sb);
                return;
            }
            var border = el as Border;
            if (border != null) CollectText(border.Child, sb);
        }

        // 保存术语。只存术语本身，不存整段回答。
        // term 由调用方给出：优先来自用户提问气泡（CleanTerm），兜底来自回答（GuessTerm）。
        void FavoriteTerm(string term, Button btn)
        {
            term = (term == null ? "" : term.Trim());
            if (term.Length == 0)
            {
                App.Log("favorite: no term found, skipped");
                btn.Content = "★ 收藏（未识别术语）";
                btn.ToolTip = "没能从提问里识别出术语名";
                return;
            }

            try
            {
                string dir = IoPath.Combine(MemRoot, "学术词汇");
                Directory.CreateDirectory(dir);
                string file = IoPath.Combine(dir, "我的收藏.md");

                // 只写术语本身（与自动抽取的格式保持一致，便于术语本统一解析）。
                // 用 [[术语]] 包裹作为唯一标识，删除时按它精确匹配整行。
                string entry = "- [[" + term + "]]\n";

                if (!File.Exists(file))
                {
                    File.WriteAllText(file,
                        "# 我的收藏\n\n> 由「★ 收藏」手动收集，可自由编辑删除。\n\n" + entry, Encoding.UTF8);
                }
                else
                {
                    File.AppendAllText(file, entry, Encoding.UTF8);
                }

                App.Log("favorite saved: term=[" + term + "] -> " + file);
                // 只改按钮状态即可，不再插一条「已存入」提示气泡（用户反馈不需要）
                btn.Content = "✓ 已收藏";
                btn.Background = Res("Paper");
                btn.Foreground = Res("Dim");
                btn.IsEnabled = false;
                if (termPanelVisible) RefreshTermBook();
            }
            catch (Exception ex)
            {
                App.Log("favorite failed: " + ex.Message);
                AddBubble("assistant", "收藏失败：" + ex.Message);
            }
        }

        // ================= 术语本 (c) =================
        bool termPanelVisible = false;

        void BtnTerm_Click(object sender, RoutedEventArgs e) { ToggleTermPanel(); }

        void BtnTermClose_Click(object sender, RoutedEventArgs e) { CloseTermPanel(); }

        // 关闭术语本并恢复底部操作栏。
        // 注意：术语本打开时会隐藏操作栏，所以所有关闭路径都必须恢复它，
        // 否则输入框会一直不可见（点术语条重问时就会踩到这个坑）。
        void CloseTermPanel()
        {
            termPanelVisible = false;
            TermPanel.Visibility = Visibility.Collapsed;
            Grid.SetRow(TermPanel, 1);
            Grid.SetRowSpan(TermPanel, 1);
            if (InputBar != null) InputBar.Visibility = Visibility.Visible;
            BtnTerm.Background = Res("Cream");
        }

        void ToggleTermPanel()
        {
            termPanelVisible = !termPanelVisible;
            TermPanel.Visibility = termPanelVisible ? Visibility.Visible : Visibility.Collapsed;

            // 术语本打开时隐藏底部操作栏，并让面板跨两行向下扩展，填满原本属于操作栏的区域
            if (InputBar != null)
                InputBar.Visibility = termPanelVisible ? Visibility.Collapsed : Visibility.Visible;
            if (termPanelVisible)
            {
                Grid.SetRow(TermPanel, 1);
                Grid.SetRowSpan(TermPanel, 2);
            }
            else
            {
                Grid.SetRow(TermPanel, 1);
                Grid.SetRowSpan(TermPanel, 1);
            }

            // 未触发 = 奶白（与亮黄标题栏区分开）；触发 = 荧光粉
            BtnTerm.Background = termPanelVisible ? Res("Pink") : Res("Cream");
            if (termPanelVisible) RefreshTermBook();
        }

        // 从术语文件中删除指定术语（圆圈叉号 / 右键触发）。
        // 术语是唯一标识，所以按 [[term]] 精确匹配整行删除，不碰其它内容。
        void DeleteTerm(string file, string term)
        {
            try
            {
                if (string.IsNullOrEmpty(term) || !File.Exists(file))
                {
                    App.Log("delete term: file missing or empty term (" + file + ")");
                    return;
                }

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                var kept = new List<string>();
                int removed = 0;

                foreach (string line in lines)
                {
                    bool isEntry = line.TrimStart().StartsWith("-");
                    if (isEntry && line.IndexOf("[[" + term + "]]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        removed++;
                        continue;      // 丢弃这一行 = 删除该术语
                    }
                    kept.Add(line);
                }

                if (removed == 0)
                {
                    App.Log("delete term: not found [" + term + "] in " + IoPath.GetFileName(file));
                    return;
                }

                File.WriteAllLines(file, kept.ToArray(), Encoding.UTF8);
                App.Log("delete term OK: removed " + removed + " line(s) for [" + term + "]");
                RefreshTermBook();
            }
            catch (Exception ex)
            {
                App.Log("delete term failed: " + ex.Message);
                AddBubble("assistant", "删除失败：" + ex.Message);
            }
        }

        void RefreshTermBook()
        {
            try
            {
                TermList.Children.Clear();
                string dir = IoPath.Combine(MemRoot, "学术词汇");
                var files = new List<string>();
                string fav = IoPath.Combine(dir, "我的收藏.md");
                string master = IoPath.Combine(dir, "词汇总表.md");
                if (File.Exists(fav)) files.Add(fav);
                if (File.Exists(master)) files.Add(master);

                if (files.Count == 0)
                {
                    TermList.Children.Add(new TextBlock
                    {
                        Text = "还没有收藏的术语。\n\n在文献模式下，点回答下方的「★ 收藏」即可存入这里。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Res("Dim"),
                        FontSize = 12,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                    return;
                }

                foreach (string f in files)
                {
                    TermList.Children.Add(new TextBlock
                    {
                        Text = IoPath.GetFileNameWithoutExtension(f),
                        FontFamily = (FontFamily)FindResource("Display"),
                        FontSize = 12,
                        Foreground = Res("Ink"),
                        Margin = new Thickness(0, 6, 0, 4)
                    });

                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(">")) continue;
                        if (t.StartsWith("- ")) t = t.Substring(2).Trim();
                        if (t.Length == 0) continue;

                        // 术语名解析，兼容三种写法：
                        //   - [[术语]]              手动收藏（唯一标识，便于精确删除）
                        //   - **术语** | 释义       旧版收藏
                        //   - 术语 | 释义           自动抽取的词汇总表
                        string name;
                        int lb = t.IndexOf("[[");
                        int rb = lb >= 0 ? t.IndexOf("]]", lb + 2) : -1;
                        if (lb >= 0 && rb > lb)
                        {
                            name = t.Substring(lb + 2, rb - lb - 2).Trim();
                        }
                        else
                        {
                            name = t.Split('|')[0].Trim().Replace("**", "").Trim();
                        }
                        if (name.Length == 0) continue;

                        // 一条术语 = 左侧术语名按钮 + 右上角圆圈叉号（快捷删除）
                        string ask = name;
                        string srcFile = f;

                        var chip = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                        var b = new Button
                        {
                            Content = name,
                            Style = (Style)FindResource("BrutalButton"),
                            Background = Res("Yellow"),      // 单个术语词条：亮黄
                            Foreground = Res("Ink"),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            ToolTip = "点击把该术语填入输入框；右键或点右上角 ✕ 删除"
                        };
                        b.Click += delegate
                        {
                            Input.Text = ask;
                            Input.CaretIndex = ask.Length;
                            CloseTermPanel();   // 必须走统一关闭路径，恢复底部操作栏
                            Input.Focus();
                            App.Log("termbook: re-ask [" + ask + "]");
                        };
                        b.MouseRightButtonUp += delegate { DeleteTerm(srcFile, ask); };
                        Grid.SetColumn(b, 0);
                        Grid.SetRow(b, 0);

                        var del = new Button
                        {
                            Content = "✕",
                            Style = (Style)FindResource("BrutalCircleButton"),
                            Background = Res("Pink"),
                            Foreground = Res("Paper"),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(-10, -4, 0, 0),   // 叠到术语条右上角
                            ToolTip = "从术语本删除这个术语"
                        };
                        del.Click += delegate { DeleteTerm(srcFile, ask); };
                        Grid.SetColumn(del, 1);
                        Grid.SetRow(del, 0);

                        chip.Children.Add(b);
                        chip.Children.Add(del);
                        TermList.Children.Add(chip);
                    }
                }
            }
            catch (Exception ex) { App.Log("termbook load failed: " + ex.Message); }
        }

        Border _dummyBody;

        void HideBusy()
        {
            for (int i = MsgPanel.Children.Count - 1; i >= 0; i--)
            {
                var fe = MsgPanel.Children[i] as FrameworkElement;
                if (fe != null && fe.Name == "BusyBubble") { MsgPanel.Children.RemoveAt(i); break; }
            }
        }

        // ================= 交互事件 =================
        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        void BtnHide_Click(object sender, RoutedEventArgs e) { SaveDraft(); Hide(); }

        void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;

            // 生活助手：开新对话前先把旧会话交给后台总结（用快照，清空后仍能完成）
            if (mode == AssistantMode.Life && lifeDirty && lifeHistory.Count > 0)
            {
                SummarizeLifeSession("手动新对话", new List<object[]>(lifeHistory));
                lifeDirty = false;
            }

            CurrentHistory.Clear();
            MsgPanel.Children.Clear();
            if (smooth != null) smooth.JumpToEnd();
            Greet();
            Input.Focus();
        }

        void BtnThink_Click(object sender, RoutedEventArgs e)
        {
            thinkMode = !thinkMode;
            RefreshToggles();
        }

        void BtnVision_Click(object sender, RoutedEventArgs e)
        {
            visionMode = !visionMode;
            if (!visionMode) { pendingImage = null; pendingImageName = null; UpdateAttachBar(); }
            RefreshToggles();
        }

        void BtnAttach_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择图片",
                Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    pendingImage = File.ReadAllBytes(dlg.FileName);
                    pendingImageName = IoPath.GetFileName(dlg.FileName);
                    visionMode = true;
                    UpdateAttachBar();
                    RefreshToggles();
                }
                catch (Exception ex) { AddBubble("assistant", "读图失败：" + ex.Message); }
            }
        }

        void BtnClearImage_Click(object sender, RoutedEventArgs e)
        {
            pendingImage = null; pendingImageName = null; UpdateAttachBar();
        }

        void UpdateAttachBar()
        {
            if (pendingImage != null)
            {
                TxtAttach.Text = pendingImageName + "  (" + (pendingImage.Length / 1024) + " KB)";
                AttachBar.Visibility = Visibility.Visible;
            }
            else AttachBar.Visibility = Visibility.Collapsed;
        }

        void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = sender as TextBox;
            if (tb == null) return;
            if (tb.LineCount > 1)
            {
                tb.MinHeight = 34;
                tb.MaxHeight = 76;
            }
        }

        void Input_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (InputDiag) App.Log("Input key: " + e.Key + " mods=" + Keyboard.Modifiers);
            if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                Send();
                return;
            }
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (Clipboard.ContainsImage())
                {
                    try
                    {
                        var src = Clipboard.GetImage();
                        if (src != null)
                        {
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(src));
                            using (var ms = new MemoryStream())
                            {
                                enc.Save(ms);
                                pendingImage = ms.ToArray();
                                pendingImageName = "剪贴板截图";
                                visionMode = true;
                                UpdateAttachBar();
                                RefreshToggles();
                            }
                            e.Handled = true;
                        }
                    }
                    catch (Exception ex) { AddBubble("assistant", "粘贴图片失败：" + ex.Message); }
                }
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && IsVisible) { Hide(); e.Handled = true; return; }
            base.OnPreviewKeyDown(e);
        }

        void BtnSend_Click(object sender, RoutedEventArgs e) { Send(); }

        // ================= 核心：发送 + 流式接收 =================
        // 按当前模式取出上下文，并按需注入人设与长期记忆
        List<object[]> BuildMessagesForMode()
        {
            var msgs = new List<object[]>();

            string sys = mode == AssistantMode.Life
                ? "你是一位可靠、务实的生活助手。回答日常问题：饮食、健康、出行、购物、家庭事务、写作、计划安排等。\n" +
                  "要求：\n" +
                  "1. 直接给可执行的建议，不要空泛。\n" +
                  "2. 涉及健康、用药、法律、投资等高风险话题时，明确提示风险，并建议咨询专业人士。\n" +
                  "3. 不确定就说不确定，不要编造。\n" +
                  "4. 用中文回答，简洁、口语化。"
                : "你是一位学术文献阅读助手，专长是解释各学科的专业术语。\n" +
                  "规则：\n" +
                  "1. 先用一句话给出该术语的准确中文定义。\n" +
                  "2. 再说明它在当前文献语境中的具体含义与作用。\n" +
                  "3. 如有必要，补充英文全称、其他常见中文译名、以及一个简短类比或例子。\n" +
                  "4. 只解释你有把握的内容。不确定时明确说明该术语在此领域存在歧义，绝不编造。\n" +
                  "5. 默认用中文回答，但保留原始英文术语。\n" +
                  "6. 回答保持精炼，控制在 200 字以内，除非用户要求展开。";

            // 生活助手：注入长期记忆（滚动总结），实现跨会话记忆
            if (mode == AssistantMode.Life && !string.IsNullOrEmpty(rollingMemory))
            {
                sys += "\n\n以下是你此前为我维护的长期记忆，请在回答时参考它，" +
                       "但不要主动复述整段记忆，也不要提及\"记忆\"这个说法：\n" + rollingMemory;
            }

            msgs.Add(new object[] { "system", new Dictionary<string, object> { { "role", "system" }, { "content", sys } } });

            // 只保留最近 N 条进入上下文，避免无限增长挤爆 num_ctx
            List<object[]> hist = CurrentHistory;
            const int keepRecent = 24;
            int start = hist.Count > keepRecent ? hist.Count - keepRecent : 0;
            for (int i = start; i < hist.Count; i++) msgs.Add(hist[i]);

            return msgs;
        }

        // 通用流式调用：把消息发给 Ollama 并逐字回调。供对话与总结共用。
        void RunCompletion(string model, int ctx, bool think, List<object[]> msgs,
                           Action<string> onDone, Action<string> onError)
        {
            var t = new Thread(delegate()
            {
                var sb = new StringBuilder();
                try
                {
                    string json = BuildRequestJson(model, ctx, msgs, think);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var req = new HttpRequestMessage(HttpMethod.Post, OllamaBase + "/api/chat") { Content = content };

                    using (var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).Result)
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            string err = resp.Content.ReadAsStringAsync().Result;
                            if (onError != null) onError("HTTP " + (int)resp.StatusCode + ": " + Trim(err, 300));
                            return;
                        }
                        using (var stream = resp.Content.ReadAsStreamAsync().Result)
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length == 0) continue;
                                // 同时收集 content 与 thinking：
                                // 思考型模型可能把内容放进 thinking 字段，只读 content 会导致总结为空
                                string piece = ExtractField(line, "content");
                                if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                                else
                                {
                                    string thinkPiece = ExtractField(line, "thinking");
                                    if (!string.IsNullOrEmpty(thinkPiece)) sb.Append(thinkPiece);
                                }
                            }
                        }
                    }
                    if (onDone != null) onDone(sb.ToString());
                }
                catch (Exception ex)
                {
                    if (onError != null) onError(ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        async void Send()
        {
            if (busy) { if (cts != null) cts.Cancel(); return; }
            string text = Input.Text == null ? "" : Input.Text.Trim();
            if (text.Length == 0 && pendingImage == null) return;

            Input.Clear();
            string shown = text.Length > 0 ? text : "(图片)";
            AddBubble("user", shown);

            // 发新消息（打字或划词都走这里）必须把视图拉到底部。
            // 只调 ScrollToEnd() 不够：AutoFollow 一旦因上滑被设为 false 就不会恢复，
            // 新内容会被加在视口下方看不见。FollowNewContent 同时重置 AutoFollow，
            // 并会在布局完成后再次贴底。
            if (smooth != null) smooth.FollowNewContent();

            var userMsg = new Dictionary<string, object> { { "role", "user" }, { "content", text } };
            if (pendingImage != null)
                userMsg["images"] = new List<string> { Convert.ToBase64String(pendingImage) };
            CurrentHistory.Add(new object[] { "user", userMsg });

            // 生活助手的会话内容需要被总结，标记为脏；并刷新空闲计时
            if (mode == AssistantMode.Life) lifeDirty = true;
            TouchActivity();

            string model = visionMode ? ModelVision : ModelText;
            int ctx = visionMode ? CtxVision : CtxText;

            pendingImage = null; pendingImageName = null; UpdateAttachBar();

            busy = true;
            BtnSend.Content = "停止";
            TxtStatus.Text = "生成中…";
            ShowBusy();

            cts = new CancellationTokenSource();
            // 助手气泡：流式阶段用纯文本，结束后替换为 Markdown 渲染结果
            var assistantTb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("Ink"),
                FontSize = 13,
                LineHeight = 20
            };
            var assistantBorder = AddBubbleWith("assistant", assistantTb);
            HideBusy();

            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            int evalCount = 0;
            var gotFirst = false;

            try
            {
                string json = BuildRequestJson(model, ctx, BuildMessagesForMode(), thinkMode);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, OllamaBase + "/api/chat") { Content = content };

                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string err = await resp.Content.ReadAsStringAsync();
                        assistantTb.Text = "请求失败 (" + (int)resp.StatusCode + ")：\n" + Trim(err, 400);
                        return;
                    }
                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (cts.IsCancellationRequested) break;
                            if (line.Length == 0) continue;
                            string piece = ExtractField(line, "content");
                            if (!string.IsNullOrEmpty(piece))
                            {
                                sb.Append(piece);
                                if (!gotFirst) gotFirst = true;   // 气泡已是不透明样式，无需再切换
                                string snapshot = sb.ToString();
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    assistantTb.Text = snapshot;
                                    ScrollToEnd();
                                }), System.Windows.Threading.DispatcherPriority.Background);
                            }
                            int ec = ExtractInt(line, "eval_count");
                            if (ec > 0) evalCount = ec;
                        }
                    }
                }

                sw.Stop();
                string final = sb.ToString();
                CurrentHistory.Add(new object[] { "assistant", new Dictionary<string, object> { { "role", "assistant" }, { "content", final } } });
                if (mode == AssistantMode.Life) TouchActivity();

                double secs = sw.Elapsed.TotalSeconds;
                string stat = "";
                if (evalCount > 0 && secs > 0)
                    stat = "\n\n— " + evalCount + " tok · " + secs.ToString("0.0") + "s · " + (evalCount / secs).ToString("0.0") + " tok/s";
                Dispatcher.Invoke(new Action(() =>
                {
                    // 流式阶段用纯文本（性能好），结束后换成 Markdown 渲染结果
                    RenderFinalAnswer(assistantBorder, final + stat);
                    // 助手回答下方加「★ 收藏」，用于收集术语
                    if (mode == AssistantMode.Lit) AddFavoriteRow(assistantBorder);
                    if (termPanelVisible) RefreshTermBook();
                    smooth.FollowNewContent();
                }));

                // 文献模式：异步抽取学术词汇存入 学术词汇 目录（不阻塞界面）
                if (mode == AssistantMode.Lit && final.Length > 0)
                    ExtractAcademicVocab(text, final);
            }
            catch (OperationCanceledException)
            {
                assistantTb.Text = sb.ToString() + "\n\n[已停止]";
            }
            catch (Exception ex)
            {
                assistantTb.Text = sb.Length > 0 ? sb.ToString() + "\n\n[中断：" + ex.Message + "]" : "出错了：" + ex.Message;
            }
            finally
            {
                busy = false;
                BtnSend.Content = "发送";
                RefreshToggles();
                cts = null;
                // 回答完毕后不要把焦点留在输入框 —— 还给原来的程序，
                // 这样用户可以立刻回到文献/网页里继续操作，不必先点一下原窗口。
                ReturnFocusToPrevious();
            }
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // 手写 JSON 组装，避免依赖序列化库
        string BuildRequestJson(string model, int ctx, List<object[]> msgs, bool think)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":").Append(JsonStr(model));
            sb.Append(",\"stream\":true");
            sb.Append(",\"think\":").Append(think ? "true" : "false");
            sb.Append(",\"options\":{\"num_ctx\":").Append(ctx).Append(",\"temperature\":0.3}");
            sb.Append(",\"messages\":[");
            for (int i = 0; i < msgs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var m = (Dictionary<string, object>)msgs[i][1];
                sb.Append("{\"role\":").Append(JsonStr((string)m["role"]));
                sb.Append(",\"content\":").Append(JsonStr((string)m["content"]));
                if (m.ContainsKey("images"))
                {
                    sb.Append(",\"images\":[");
                    var imgs = (List<string>)m["images"];
                    for (int j = 0; j < imgs.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(JsonStr(imgs[j]));
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // 从单行 JSON 里抠字段（够用且不引入依赖）
        static string ExtractField(string json, string key)
        {
            string pat = "\"" + key + "\":\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i += pat.Length;
            var sb = new StringBuilder();
            bool esc = false;
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (esc)
                {
                    switch (c)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (i + 4 < json.Length)
                            {
                                int code;
                                if (int.TryParse(json.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(c); break;
                    }
                    esc = false;
                }
                else if (c == '\\') esc = true;
                else if (c == '"') break;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static int ExtractInt(string json, string key)
        {
            string pat = "\"" + key + "\":";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += pat.Length;
            int j = i;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
            int v;
            if (j > i && int.TryParse(json.Substring(i, j - i), out v)) return v;
            return -1;
        }
    }
}
