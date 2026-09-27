using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LitReader
{
    // 让对话区滚动变得「丝滑」：
    //
    // WPF 的 ScrollViewer 默认是线性硬跳 —— 滚轮每格固定跳固定行数，没有缓动，
    // 流式输出时每来一个 token 又硬跳一次，所以观感是一顿一顿的。
    //
    // 做法：
    //   1) 接管滚轮，改为「目标值累加」：快速连续滚动会把目标累加得更远，
    //      而不是每次被重置，所以滚得快就走得远（类惯性）。
    //   2) 每帧用 ease-out 缓动逼近目标，速度逐渐衰减 —— 这就是非线性的来源。
    //   3) 自动跟随（流式输出滚动到底）也走同一套缓动，避免硬跳。
    //   4) 用户向上滚动时暂停自动跟随，滚回底部自动恢复（聊天软件的常规行为）。
    internal class SmoothScroll
    {
        readonly ScrollViewer sv;
        DispatcherTimer timer;

        double target;
        const double WheelStep = 115;     // 每格滚轮的目标增量（像素）
        const double DecayPerSecond = 9.0; // 指数衰减速率：越小越柔、拖尾越长
        const double SnapEpsilon = 0.6;   // 小于此距离直接贴合，避免无限微动

        // 记录上一帧时间，让缓动与帧间隔无关（否则不同刷新率手感不一致）
        DateTime lastTick = DateTime.Now;

        public bool AutoFollow = true;

        public SmoothScroll(ScrollViewer viewer)
        {
            sv = viewer;
            target = sv.VerticalOffset;
            timer = new DispatcherTimer(DispatcherPriority.Render);
            timer.Interval = TimeSpan.FromMilliseconds(8);
            timer.Tick += delegate { Step(); };
            timer.Start();
        }

        public void EnsureAttached()
        {
            // PreviewMouseWheel 是隧道事件，比 ScrollViewer 内部处理更早
            sv.PreviewMouseWheel -= OnWheel;
            sv.PreviewMouseWheel += OnWheel;
            // 监听偏移变化，用于识别拖拽滚动条并让出控制权
            sv.ScrollChanged -= OnScrollChanged;
            sv.ScrollChanged += OnScrollChanged;
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;

            double cur = sv.VerticalOffset;
            if (Math.Abs(target - cur) < 1.0) target = cur;   // 静止时从当前位置续接

            // 目标值累加：快速连续滚动会把目标推得更远，形成惯性感
            double step = (e.Delta > 0 ? -WheelStep : WheelStep);

            // 非线性：越接近顶部/底部，单格位移越小（有「重量」和阻尼感）
            double boundary = 1.0;
            if (step < 0) { if (cur < 120) boundary = 0.35 + 0.65 * (cur / 120.0); }
            else { double room = MaxOffset - cur; if (room < 120) boundary = 0.35 + 0.65 * (Math.Max(0, room) / 120.0); }
            target += step * boundary;

            target = Clamp(target);
            e.Handled = true;

            // 向上滚动 -> 暂停自动跟随；滚回底部 -> 恢复
            if (e.Delta > 0) AutoFollow = false;
            else if (target >= MaxOffset - 2) AutoFollow = true;
        }

        // 流式输出时调用：平滑跟随到底部
        public void FollowEnd()
        {
            if (!AutoFollow) return;
            target = MaxOffset;
        }

        // 立即回到底部（如新对话）
        public void JumpToEnd()
        {
            AutoFollow = true;
            target = MaxOffset;
            SelfScroll(target);
        }

        // 新内容加入后调用：重置自动跟随并把视图带到底部。
        // 刚加入的元素可能尚未完成布局，此时 MaxOffset 仍是旧值，
        // 所以在随后两帧内再对一次，确保真正贴底。
        public void FollowNewContent()
        {
            AutoFollow = true;
            target = MaxOffset;
            SelfScroll(target);
            pendingFollow = 3;
        }

        // 平滑滚动到指定偏移（消息锚点跳转用）
        public void ScrollTo(double offset, bool keepAutoFollow)
        {
            target = Clamp(offset);
            if (!keepAutoFollow) AutoFollow = false;
        }

        int pendingFollow = 0;

        // 由 FloatWindow 在 LayoutUpdated 时调用（布局完成后才有正确的 MaxOffset）
        public void OnLayoutUpdated()
        {
            if (pendingFollow <= 0) return;
            pendingFollow--;
            target = MaxOffset;
        }

        // 立刻对齐当前偏移（窗口尺寸变化等场景）
        public void SyncNow()
        {
            target = Clamp(sv.VerticalOffset);
            lastTick = DateTime.Now;
        }

        double MaxOffset
        {
            get
            {
                double m = sv.ScrollableHeight;
                return m < 0 ? 0 : m;
            }
        }

        double Clamp(double v)
        {
            if (v < 0) return 0;
            double m = MaxOffset;
            return v > m ? m : v;
        }

        void Step()
        {
            DateTime now = DateTime.Now;
            double dt = (now - lastTick).TotalSeconds;
            lastTick = now;
            if (dt <= 0) return;
            if (dt > 0.25) dt = 0.25;         // 卡顿后不要一次性跳太远

            double cur = sv.VerticalOffset;
            double diff = target - cur;

            if (Math.Abs(diff) < SnapEpsilon)
            {
                if (Math.Abs(diff) > 0) SelfScroll(target);
                return;
            }

            // 指数缓动：与帧率无关，速度随时间平滑衰减（非线性拖尾）
            double k = 1.0 - Math.Exp(-DecayPerSecond * dt);
            SelfScroll(cur + diff * k);
        }

        // ================= 与滚动条拖拽互斥 =================
        // 关键修复：拖拽滚动条时 ScrollBar 自己设置偏移，而本类同时在向 target 缓动，
        // 两者抢控制权 -> 位置闪跳。
        // 用「自身滚动预算」精确区分：本类每次 ScrollToVerticalOffset 前 +1，
        // 若 ScrollChanged 紧接着触发就 -1；没配对上说明是用户拖拽，立即交出控制权。
        int selfScrollsPending = 0;

        void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0) return;

            if (selfScrollsPending > 0)
            {
                selfScrollsPending--;      // 这是本类自己的滚动，忽略
                return;
            }

            // 不是本类引起的（尺寸变化、外部滚动）-> 同步目标，避免位置跳变
            target = sv.VerticalOffset;
        }

        void SelfScroll(double offset)
        {
            selfScrollsPending++;
            sv.ScrollToVerticalOffset(offset);
        }
    }
}
