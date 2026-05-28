using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using WpfCompositeSplitter.Controls;

namespace WpfCompositeSplitter.Layout
{
    public sealed class DragComputationResult
    {
        /// <summary>
        /// 经过约束（Clamp）后的最终长度。
        /// 
        /// 含义：
        /// - 用户拖拽后“理论长度”可能越界（小于最小宽度、超过最大宽度）；
        /// - 这个值是应用 Min/Max、折叠阈值等规则后，可以安全写回布局系统的长度。
        ///
        /// 典型用途：
        /// - GridLength（像素）更新
        /// - Splitter 左/右面板宽度更新
        /// </summary>
        public double ClampedLength { get; init; }  

        /// <summary>
        /// 本次拖拽是否触达“折叠边界”（collapse edge）。
        ///
        /// 含义：
        /// - true：拖拽已到达或越过折叠触发线，UI 可能应进入折叠态（如 PrimaryOnly / SecondaryOnly）。
        /// - false：仍在正常可见区间内，仅是普通尺寸调整。
        ///
        /// 典型用途：
        /// - 决定是否切换 DisplayState
        /// - 决定是否触发折叠动画/视觉反馈
        /// </summary>
        public bool HitCollapseEdge { get; init; }  // 判断是否拖动到边缘

        /// <summary>
        /// 本次拖拽后，是否应“记住当前展开尺寸”。
        ///
        /// 背景：
        /// - 折叠/展开场景下，常需要记住“上一次正常展开宽度”，以便下次展开恢复到用户习惯值。
        ///
        /// 含义：
        /// - true：当前长度处于可作为“有效展开宽度”的区间，应更新 rememberedExpandedLength。
        /// - false：当前结果可能是临界值/折叠值，不应覆盖已记忆的展开宽度。
        ///
        /// 典型用途：
        /// - 在进入折叠态前保存宽度
        /// - 展开时恢复到用户上次工作宽度，而不是固定默认值
        /// </summary>
        public bool ShouldRememberExpanded { get; init; }   
    }

    /// <summary>
    /// 布局计算控制器（纯布局/状态逻辑，不直接依赖视觉树）。
    /// 
    /// 职责：
    /// 1) 维护 PaneLayoutState（状态容器）
    /// 2) 处理显示状态切换（Normal / PrimaryOnly / SecondaryOnly）
    /// 3) 处理拖拽计算（边界约束、折叠边界判定、展开长度记忆）
    /// 
    /// 设计目标：
    /// - 让 Splitter 只负责“UI 事件转发 + 结果应用”
    /// - 把可测试的规则收敛到这里
    /// </summary>
    public class LayoutController
    {
        /// <summary>
        /// 布局状态对象（由外部传入并共享）。
        /// 包含长度参数、最小值、当前显示状态、上次展开长度等。
        /// </summary>
        public PaneLayoutState State { get; }

        public LayoutController(PaneLayoutState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));

            // 初始化“上次展开长度”：
            // 若当前 PrimaryLength 已是绝对像素值且 > 0，则把它作为初始记忆值。
            // 这样首次从折叠恢复时有合理基线。
            if (State.PrimaryLength.IsAbsolute && State.PrimaryLength.Value > 0)
                State.LastExpandedLength = State.PrimaryLength.Value;
        }

        /// <summary>
        /// 切换 PrimaryOnly 状态。
        /// 
        /// 规则（保持原逻辑）：
        /// - 如果当前就是 PrimaryOnly，则切回 Normal；
        /// - 否则切到 PrimaryOnly。
        /// 
        /// 额外处理：
        /// - 当进入 PrimaryOnly 时，如果当前主面板实际宽度仍大于“折叠宽度”，
        ///   说明用户还处于展开区间，应该记下这次宽度用于未来恢复。
        /// </summary>
        /// <param name="currentPrimaryActual">当前主面板实际宽度（像素）</param>
        public void TogglePrimaryOnly(double currentPrimaryActual)
        {
            State.DisplayState = State.DisplayState == PaneDisplayState.PrimaryOnly
                ? PaneDisplayState.Normal
                : PaneDisplayState.PrimaryOnly;

            if (State.DisplayState == PaneDisplayState.PrimaryOnly &&
                currentPrimaryActual > State.PrimaryCollapsedLength)
            {
                State.LastExpandedLength = Math.Max(currentPrimaryActual, State.PrimaryMinExpandedLength);
            }
        }

        /// <summary>
        /// 切换 SecondaryOnly 状态。
        /// 
        /// 规则（保持原逻辑）：
        /// - 如果当前就是 SecondaryOnly，则切回 Normal；
        /// - 否则切到 SecondaryOnly。
        /// </summary>
        public void ToggleSecondaryOnly()
        {
            State.DisplayState = State.DisplayState == PaneDisplayState.SecondaryOnly
                ? PaneDisplayState.Normal
                : PaneDisplayState.SecondaryOnly;
        }

        /// <summary>
        /// 直接恢复到 Normal（双面板显示）。
        /// </summary>
        public void RestoreNormal()
        {
            State.DisplayState = PaneDisplayState.Normal;
        }

        /// <summary>
        /// 计算一次拖拽结果（核心方法）。
        /// 
        /// 输入：
        /// - currentPrimary: 当前主面板长度
        /// - delta: 本次拖拽位移（正负取决于方向）
        /// - total: Splitter 总可用长度
        /// 
        /// 输出：
        /// - 返回 DragComputationResult（长度 + 标记）
        /// - 同时按原逻辑更新 State.PrimaryLength
        /// - 若满足条件，更新 LastExpandedLength
        /// </summary>
        public DragComputationResult ComputeDrag(double currentPrimary, double delta, double total)
        {
            // 1) 计算拖拽目标长度（未约束）
            double target = currentPrimary + delta;

            // 2) 按“展开模式最小值”进行约束（与原逻辑保持一致：useCollapsedMin=false）
            double clamped = ClampToBounds(target, total, useCollapsedMin: false);

            // 3) 折叠边界定义（保持原逻辑）
            // edge = max(collapsed, minExpanded) + 0.5
            // +0.5 是为了减少浮点抖动造成的边界来回跳变。
            double edge = Math.Max(State.PrimaryCollapsedLength, State.PrimaryMinExpandedLength) + 0.5;

            // 4) 判定是否命中折叠边界（保持原表达式语义）
            bool hitCollapseEdge = !(target > edge);

            // 5) 判定是否应记忆展开长度
            bool shouldRemember = target > edge;

            // 6) 仅在展开区间才记忆，防止临界值污染 LastExpandedLength
            if (shouldRemember)
            {
                RememberExpandedLength(clamped);
            }

            // 7) 同步状态中的 PrimaryLength（像素）
            State.PrimaryLength = new GridLength(clamped, GridUnitType.Pixel);

            // 8) 返回结构化结果，供控件层决定是否切换 DisplayState
            return new DragComputationResult
            {
                ClampedLength = clamped,
                HitCollapseEdge = hitCollapseEdge,
                ShouldRememberExpanded = shouldRemember
            };
        }

        /// <summary>
        /// 对“当前主面板长度”做边界校正（不使用折叠最小值）。
        /// 常用于布局变更后重新矫正当前值。
        /// </summary>
        public double ClampCurrentPrimary(double currentPrimary, double total)
        {
            return ClampToBounds(currentPrimary, total, useCollapsedMin: false);
        }

        /// <summary>
        /// 获取“从折叠恢复到 Normal 时”应使用的主面板宽度。
        /// 
        /// 策略（保持原逻辑）：
        /// 1) 优先使用 LastExpandedLength；
        /// 2) 若无效，回退到当前绝对宽度或默认值 280；
        /// 3) 至少不小于 PrimaryMinExpandedLength；
        /// 4) 最终再做一次总宽约束 Clamp。
        /// </summary>
        public double GetRestoreLength(double total)
        {
            double restore = State.LastExpandedLength;
            if (double.IsNaN(restore) || restore <= 0)
                restore = State.PrimaryLength.IsAbsolute ? State.PrimaryLength.Value : 280.0;

            restore = Math.Max(restore, State.PrimaryMinExpandedLength);
            restore = ClampToBounds(restore, total, useCollapsedMin: false);
            return restore;
        }

        /// <summary>
        /// 通用边界约束方法。
        /// 
        /// 约束区间：
        /// - max = total - splitterWidth - secondaryMin
        /// - min = (useCollapsedMin ? collapsedMin : expandedMin)，且 min 不得大于 max
        /// 
        /// 注意：
        /// - 当 total 无效（NaN 或 <=0）时，保守返回非负值。
        /// </summary>
        /// <param name="primaryLength">待约束的主面板长度</param>
        /// <param name="total">总可用长度</param>
        /// <param name="useCollapsedMin">是否使用折叠最小值作为下限</param>
        public double ClampToBounds(double primaryLength, double total, bool useCollapsedMin)
        {
            if (double.IsNaN(total) || total <= 0)
                return Math.Max(0, primaryLength);

            double s = State.SplitterWidth;
            double secMin = State.SecondaryMinLength;
            double max = Math.Max(0, total - s - secMin);

            double min = useCollapsedMin ? State.PrimaryCollapsedLength : State.PrimaryMinExpandedLength;
            min = Math.Min(min, max);

            return Math.Max(min, Math.Min(primaryLength, max));
        }

        /// <summary>
        /// 记忆“上次展开长度”。
        /// 
        /// 保护条件（保持原逻辑）：
        /// - NaN/Infinity 直接忽略
        /// - 小于 expandedMin + 0.5 的临界值忽略
        /// </summary>
        public void RememberExpandedLength(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;
            if (value < State.PrimaryMinExpandedLength + 0.5) return;
            State.LastExpandedLength = value;
        }
    }
}
