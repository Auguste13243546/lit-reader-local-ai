using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using WpfApp = System.Windows.Application;
using WpfExitEventArgs = System.Windows.ExitEventArgs;
using WpfWindowState = System.Windows.WindowState;
using IoPath = System.IO.Path;

namespace LitReader
{
    // 单一 Application 实例，负责承载浮窗并挂托盘图标
    // 注意：同时引用了 WPF 与 WinForms，Application 等类型需要显式消歧义
    public class App : WpfApp
    {
        NotifyIcon tray;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [STAThread]
        public static void Main(string[] args)
        {
            // 先加载配置（数据目录、Ollama 地址）。
            // 开源版本不含任何硬编码个人路径，全部来自 config.txt / 环境变量 / 默认值。
            AppConfig.Load();
            AppConfig.SaveIfMissing();

            // 再保存启动参数：FloatWindow 构造/加载时就要用到（判断是否隐藏启动）
            PendingArgs = args;
            StartHidden = HasArgStatic("--hidden");

            // 单实例：全局热键只能注册一次，多开会让新窗口收不到热键。
            // 互斥体必须持有到程序结束（不能用 using 后提前 return，
            // 否则第二个进程退出时会释放掉第一个进程仍需要的互斥体）。
            bool createdNew;
            var mutex = new Mutex(true, "LitReaderFloatWindow_SingleInstance", out createdNew);
            if (!createdNew)
            {
                Log("another instance already running; exiting. args=" + string.Join(" ", args));
                return;
            }

            try
            {
                // 在创建/显示窗口之前记录当前前台窗口 —— 这就是「启动前的程序」，
                // 窗口显示后要把焦点还给它，否则 Chromium 不再暴露网页选区，
                // 会导致第一次划词失效（实测确认）。
                FloatWindow.PreStartForeground = GetForegroundWindow();

                var app = new App();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var win = new FloatWindow();
                app.MainWindow = win;
                app.AttachTray(win);
                Log("started ok; args=" + (args == null ? "(none)" : string.Join(" ", args)) +
                    " preStartFg=" + FloatWindow.PreStartForeground);
                app.Run(win);
                Log("exited normally");
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.ToString());
                throw;
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Close();
            }
        }

        // 测试用参数：--set-active / --capture-test
        // 静态字段，保证 FloatWindow 构造/加载时即可读取
        public static string[] PendingArgs;
        public static bool StartHidden;

        static bool HasArgStatic(string name)
        {
            if (PendingArgs == null) return false;
            for (int i = 0; i < PendingArgs.Length; i++)
                if (string.Equals(PendingArgs[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool HasArgStaticPublic(string name) { return HasArgStatic(name); }
        public bool HasArg(string name) { return HasArgStatic(name); }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (PendingArgs == null || PendingArgs.Length == 0) return;
            Log("args: " + string.Join(" ", PendingArgs) + " startHidden=" + StartHidden);

            var w = MainWindow as FloatWindow;
            if (w == null) return;

            foreach (string a in PendingArgs)
            {
                if (a == "--set-active") w.SetActiveSelection();
                else if (a == "--termtest2") { w.RunTermFlowTest(); return; }
                else if (a == "--focus-test") { w.RunFocusTest(); return; }
                else if (a == "--marks-test") { w.RunMarksTest(); return; }
                else if (a == "--termbook-test") { w.RunTermBookTest(); return; }
                else if (a == "--input-test") { w.RunInputTest(); return; }
                else if (a == "--vocab-test") { w.RunVocabTest(); return; }
                else if (a == "--md-test") { w.RunMarkdownTest(); return; }
                else if (a == "--summarize-test") { w.RunSummarizeTest(); return; }
                else if (a == "--memory-preview") { w.RunMemoryPreview(); return; }
                else if (a == "--capture-test") { w.RunCaptureTest(); return; }
            }
        }

        // 轻量日志：热键注册结果写在这里，便于排查
        internal static void Log(string msg)
        {
            try
            {
                string dir = IoPath.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string file = IoPath.Combine(dir, "litreader.log");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        void AttachTray(FloatWindow win)
        {
            try
            {
                tray = new NotifyIcon();
                tray.Icon = BuildIcon();
                tray.Text = "文献术语助手";
                tray.Visible = true;

                var menu = new ContextMenuStrip();
                menu.Items.Add("打开浮窗", null, delegate
                {
                    win.Show();
                    win.WindowState = WpfWindowState.Normal;
                    win.Activate();
                });
                menu.Items.Add("退出", null, delegate
                {
                    tray.Visible = false;
                    Shutdown();
                });
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += delegate { win.Show(); win.Activate(); };
            }
            catch
            {
                // 托盘不可用不影响主功能
            }
        }

        static Icon BuildIcon()
        {
            // 运行时绘制图标，免去额外资源文件
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(255, 77, 107, 254)))
                    g.FillEllipse(b, 0, 0, 15, 15);
                using (var w = new SolidBrush(Color.White))
                {
                    g.FillRectangle(w, 4, 4, 3, 8);
                    g.FillRectangle(w, 9, 4, 3, 8);
                }
            }
            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }

        protected override void OnExit(WpfExitEventArgs e)
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnExit(e);
        }
    }
}
