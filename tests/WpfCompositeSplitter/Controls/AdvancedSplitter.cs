using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WpfCompositeSplitter.Layout;

namespace WpfCompositeSplitter.Controls
{
    /// <summary>
    /// 高级分栏控件（UI 层）。
    ///
    /// 设计原则（与 LayoutController 解耦）：
    /// 1) 本类负责：模板部件获取、事件订阅、命令响应、把计算结果应用到 UI；
    /// 2) LayoutController 负责：拖拽计算、Clamp、恢复宽度、显示状态切换策略；
    /// 3) 不改业务逻辑，仅把“算法/规则”从控件层移出。
    /// </summary>
    [TemplatePart(Name = PART_PrimaryDef, Type = typeof(DefinitionBase))]
    [TemplatePart(Name = PART_SecondaryDef, Type = typeof(DefinitionBase))]
    [TemplatePart(Name = PART_SplitterDef, Type = typeof(DefinitionBase))]
    [TemplatePart(Name = PART_Splitter, Type = typeof(GridSplitter))]
    public class AdvancedSplitter : Control
    {
        #region Template Part Names（模板部件名，必须与 .xaml 一致）
        
        private const string PART_PrimaryDef = "PART_PrimaryDef";
        private const string PART_SecondaryDef = "PART_SecondaryDef";
        private const string PART_SplitterDef = "PART_SplitterDef";
        private const string PART_Splitter = "PART_Splitter";

        #endregion

        #region Template Part References（模板部件引用）

        private DefinitionBase _primaryDef;
        private DefinitionBase _secondaryDef;
        private DefinitionBase _splitterDef;
        private GridSplitter _splitter;

        #endregion

        #region Runtime Flags（运行期状态）

        /// <summary>
        /// 拖拽过程中是否曾“命中折叠边界”，在 DragCompleted/LostCapture 时统一提交。
        /// 保持原有“延迟提交折叠”的行为，避免在 DragDelta 同帧切状态导致抖动。
        /// </summary>
        private bool _pendingCollapseToPrimaryOnly;

        private bool _dragSessionActive;  // 当前是否处于一次拖拽会话中

        //private double _lastExpandedLength = 280.0;

        // 记录本次拖拽开始时的“展开长度”快照.当拖拽贴边时，用它回写 LastExpandedLength，防止被临界值污染
        private double _dragStartExpandedLength = 280.0;
        
        // 防重入标记：当内部正在回写DP/列宽时，屏蔽回调再次触发造成的递归
        private bool _internalUpdating;

        #endregion

        #region LayoutController（解耦核心）

        private PaneLayoutState _layoutState; // 布局状态对象：把控件 DP 映射为 Controller 可消费的数据
        private LayoutController _layoutController; // 布局控制器：承担原先散落在 AdvancedSplitter 内的“布局算法逻辑”

        /// <summary>
        /// 确保 Controller 已初始化（惰性创建，避免模板前访问空对象）。
        /// </summary>
        private void EnsureController()
        {
            if (_layoutState != null && _layoutController != null) return;

            _layoutState = new PaneLayoutState
            {
                DisplayState = DisplayState,
                PrimaryLength = PrimaryLength,
                SplitterWidth = SplitterWidth,
                PrimaryMinExpandedLength = PrimaryMinExpandedLength,
                PrimaryCollapsedLength = PrimaryCollapsedLength,
                SecondaryMinLength = SecondaryMinLength
            };

            _layoutController = new LayoutController(_layoutState);
        }

        /// <summary>
        /// 把当前 DP 同步到 State（输入方向：UI -> Controller）。
        /// 每次计算前调用，保证 Controller 使用最新参数。
        /// </summary>
        private void SyncDpToState()
        {
            EnsureController();

            _layoutState.DisplayState = DisplayState;
            _layoutState.PrimaryLength = PrimaryLength;
            _layoutState.SplitterWidth = SplitterWidth;
            _layoutState.PrimaryMinExpandedLength = PrimaryMinExpandedLength;
            _layoutState.PrimaryCollapsedLength = PrimaryCollapsedLength;
            _layoutState.SecondaryMinLength = SecondaryMinLength;
        }

        /// <summary>
        /// 把 State 同步回 DP（输出方向：Controller -> UI）。
        /// 只同步“业务状态值”，不直接操作模板部件。
        /// </summary>
        private void SyncStateToDp()
        {
            if (_layoutState == null) return;

            DisplayState = _layoutState.DisplayState;
            PrimaryLength = _layoutState.PrimaryLength;
        }

        #endregion

        #region Ctor / Style

        static AdvancedSplitter()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(AdvancedSplitter),
                new FrameworkPropertyMetadata(typeof(AdvancedSplitter)));
        }

        public AdvancedSplitter()
        {
            // 命令绑定（业务命令保持不变）
            CommandBindings.Add(new CommandBinding(AdvancedSplitterCommands.TogglePrimaryOnly, OnTogglePrimaryOnly));
            CommandBindings.Add(new CommandBinding(AdvancedSplitterCommands.ToggleSecondaryOnly, OnToggleSecondaryOnly));
            CommandBindings.Add(new CommandBinding(AdvancedSplitterCommands.RestoreNormal, OnRestoreNormal));
        }

        #endregion

        #region Template Lifecycle

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            DetachHandlers();  // 先解绑旧模板事件，避免重复订阅

            _primaryDef = GetTemplateChild(PART_PrimaryDef) as DefinitionBase;
            _secondaryDef = GetTemplateChild(PART_SecondaryDef) as DefinitionBase;
            _splitterDef = GetTemplateChild(PART_SplitterDef) as DefinitionBase;
            _splitter = GetTemplateChild(PART_Splitter) as GridSplitter;

            AttachHandlers(); // 重新绑事件

            Loaded -= OnLoaded;
            Loaded += OnLoaded;
            SizeChanged -= OnSizeChanged;
            SizeChanged += OnSizeChanged;

            // 初始化 Controller 并应用当前状态
            SyncDpToState();
            ApplyState(DisplayState);
        }

        private void AttachHandlers()
        {
            if (_splitter != null) { 
                _splitter.DragDelta += OnSplitterDragDelta;
                _splitter.DragCompleted += OnSplitterDragCompleted;
                _splitter.LostMouseCapture += OnSplitterLostMouseCapture;
            }
        }

        private void DetachHandlers()
        {
            if (_splitter != null) { 
                _splitter.DragDelta -= OnSplitterDragDelta;
                _splitter.DragCompleted -= OnSplitterDragCompleted;
                _splitter.LostMouseCapture -= OnSplitterLostMouseCapture;
            }

            Loaded -= OnLoaded;
            SizeChanged -= OnSizeChanged;
        }

        #endregion

        #region Events

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            //if (PrimaryLength.IsAbsolute && PrimaryLength.Value > 0)
            //    _lastExpandedLength = PrimaryLength.Value;
            ApplyState(DisplayState);  // 首次加载时按当前 DisplayState 统一落地视觉
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_internalUpdating) return;
            ClampCurrentPrimaryLength();
        }

        /// <summary>
        /// 拖拽过程：保持原业务行为
        /// - 仅 Normal 可拖
        /// - 过程中先更新长度，不立即切折叠态
        /// - 命中边界后置标记，拖拽结束再提交
        /// </summary>
        private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_primaryDef == null || _secondaryDef == null || _splitterDef == null) return;

            // 折叠态禁拖（保持原逻辑）
            if (DisplayState != PaneDisplayState.Normal)
            {
                e.Handled = true;
                return;
            }

            // 拖拽会话首帧：记录起点展开值
            if (!_dragSessionActive)
            {
                _dragSessionActive = true;
                _dragStartExpandedLength = GetPrimaryActualLength();
            }

            double current = GetPrimaryActualLength();
            double delta = Orientation == Orientation.Horizontal ? e.HorizontalChange : e.VerticalChange;
            double total = Orientation == Orientation.Horizontal ? ActualWidth : ActualHeight;

            // 计算交给 Controller
            SyncDpToState();
            DragComputationResult result = _layoutController.ComputeDrag(current, delta, total);

            // 命中边界：仅置 pending，拖拽结束再 commit（保持你原逻辑）
            if (result.HitCollapseEdge)
            {
                _layoutController.RememberExpandedLength(_dragStartExpandedLength);
                _pendingCollapseToPrimaryOnly = true;
            }

            _internalUpdating = true;
            try
            {
                // 视觉应用仍在控件层（不改你的布局写法）
                SetPrimaryLength(result.ClampedLength); // 始终 Pixel，避免 Star 抖动
                SetSplitterFixed();
                SetSecondaryStar();

                // State -> DP 同步
                SyncStateToDp();

                // 你原逻辑：合法展开长度持续记忆
                if (result.ClampedLength >= PrimaryMinExpandedLength)
                {
                    _layoutController.RememberExpandedLength(result.ClampedLength);
                }
            }
            finally
            {
                _internalUpdating = false;
            }

            e.Handled = true;
        }

        private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragSessionActive = false;
            CommitPendingCollapseIfAny();
        }

        // 解决拖出窗口
        private void OnSplitterLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_dragSessionActive && !_pendingCollapseToPrimaryOnly) return;

            _dragSessionActive = false;
            CommitPendingCollapseIfAny();
        }

        #endregion

        #region DP Changed Callbacks

        private static void OnDisplayStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AdvancedSplitter)d;
            if (c._internalUpdating) return;
            c.ApplyState((PaneDisplayState)e.NewValue);
        }

        private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AdvancedSplitter)d;
            if (c._internalUpdating) return;

            // 双模板切换后，WPF会重套模板并触发OnApplyTemplate
            // 这里主动刷新一次，兼容某些时序
            c.ApplyState(c.DisplayState);
        }

        private static void OnSplitterWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (AdvancedSplitter)d;
            if (c._internalUpdating) return;
            c.ApplyState(c.DisplayState);
        }

        #endregion

        #region Commands

        private void OnTogglePrimaryOnly(object sender, ExecutedRoutedEventArgs e)
        {
            //DisplayState = DisplayState == PaneDisplayState.PrimaryOnly
            //    ? PaneDisplayState.Normal
            //    : PaneDisplayState.PrimaryOnly;
            SyncDpToState();
            _layoutController.TogglePrimaryOnly(GetPrimaryActualLength());
            _internalUpdating = true;
            try
            {
                SyncStateToDp();
            }
            finally
            {
                _internalUpdating = false;
            }

            ApplyState(DisplayState);
        }

        private void OnToggleSecondaryOnly(object sender, ExecutedRoutedEventArgs e)
        {
            //DisplayState = DisplayState == PaneDisplayState.SecondaryOnly
            //    ? PaneDisplayState.Normal
            //    : PaneDisplayState.SecondaryOnly;
            SyncDpToState();
            _layoutController.ToggleSecondaryOnly();

            _internalUpdating = true;
            try
            {
                SyncStateToDp();
            }
            finally
            {
                _internalUpdating = false;
            }

            ApplyState(DisplayState);
        }

        private void OnRestoreNormal(object sender, ExecutedRoutedEventArgs e)
        {
            //DisplayState = PaneDisplayState.Normal;
            SyncDpToState();
            _layoutController.RestoreNormal();

            _internalUpdating = true;
            try
            {
                SyncStateToDp();
            }
            finally
            {
                _internalUpdating = false;
            }

            ApplyState(DisplayState);
        }

        #endregion

        #region State Apply（视觉落地，不改业务语义）

        private void ApplyState(PaneDisplayState state)
        {
            if (_primaryDef == null || _secondaryDef == null || _splitterDef == null) return;

            _internalUpdating = true;
            try
            {
                switch (state)
                {
                    case PaneDisplayState.PrimaryOnly:
                        // 展开左侧，折叠右侧（与状态名一致）
                        //if (GetPrimaryActualLength() > PrimaryCollapsedLength + 0.5)
                        //    _lastExpandedLength = Math.Max(GetPrimaryActualLength(), PrimaryMinExpandedLength);

                        SetPrimaryLength(PrimaryCollapsedLength); // 保留按钮条
                        SetSplitterLength(0);                     // 折叠态隐藏splitter
                        SetSecondaryStar();
                        break;

                    case PaneDisplayState.SecondaryOnly:
                        SetPrimaryStar();
                        SetSplitterLength(0);                     // 折叠态隐藏splitter
                        SetSecondaryLength(SecondaryCollapsedLength);
                        break;

                    default:
                        double total = Orientation == Orientation.Horizontal ? ActualWidth : ActualHeight;
                        double restore = _layoutController.GetRestoreLength(total);
                        if (double.IsNaN(restore) || restore <= 0)
                            restore = PrimaryLength.IsAbsolute ? PrimaryLength.Value : 280.0;

                        SetPrimaryLength(restore);
                        SetSplitterFixed();
                        SetSecondaryStar();

                        _layoutController.RememberExpandedLength(restore);

                        SyncStateToDp();  // 同步 State->DP
                        break;
                }
            }
            finally
            {
                _internalUpdating = false;
            }

            if (_splitter != null)
                _splitter.IsEnabled = (DisplayState == PaneDisplayState.Normal);
        }

        /// <summary>
        /// 若拖拽过程中命中过折叠边界，在拖拽结束后再提交 PrimaryOnly。
        /// 保持“延迟切态”的原行为。
        /// </summary>
        private void CommitPendingCollapseIfAny()
        {
            if (!_pendingCollapseToPrimaryOnly) return;

            _pendingCollapseToPrimaryOnly = false;

            // 关键：延迟到Dispatcher，避开GridSplitter当前帧
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DisplayState != PaneDisplayState.PrimaryOnly)
                    DisplayState = PaneDisplayState.PrimaryOnly;
                else
                    ApplyState(PaneDisplayState.PrimaryOnly); // 同值强刷
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        #endregion

        #region Clamp / Measure Helpers

        private void ClampCurrentPrimaryLength()
        {
            if (_primaryDef == null || DisplayState != PaneDisplayState.Normal) return;

            double current = GetPrimaryActualLength();
            double total = Orientation == Orientation.Horizontal ? ActualWidth : ActualHeight;

            SyncDpToState();
            double clamped = _layoutController.ClampCurrentPrimary(current, total);

            if (Math.Abs(clamped - current) < 0.5) return;

            _internalUpdating = true;
            try
            {
                SetPrimaryLength(clamped);
                SetSplitterFixed();
                SetSecondaryStar();

                if (clamped >= PrimaryMinExpandedLength)
                    _layoutController.RememberExpandedLength(clamped);

                SyncStateToDp();
            }
            finally
            {
                _internalUpdating = false;
            }
        }

        private double GetPrimaryActualLength()
        {
            if (_primaryDef is ColumnDefinition c) return c.ActualWidth;
            if (_primaryDef is RowDefinition r) return r.ActualHeight;
            return 0;
        }

        #endregion

        #region Layout Setters（仅 UI 落地）

        private void SetPrimaryLength(double v)
        {
            PrimaryLength = new GridLength(v, GridUnitType.Pixel);

            if (_primaryDef is ColumnDefinition c) c.Width = new GridLength(v, GridUnitType.Pixel);
            else if (_primaryDef is RowDefinition r) r.Height = new GridLength(v, GridUnitType.Pixel);
        }

        private void SetPrimaryStar()
        {
            if (_primaryDef is ColumnDefinition c) c.Width = new GridLength(1, GridUnitType.Star);
            else if (_primaryDef is RowDefinition r) r.Height = new GridLength(1, GridUnitType.Star);
        }

        private void SetSecondaryLength(double v)
        {
            if (_secondaryDef is ColumnDefinition c) c.Width = new GridLength(v, GridUnitType.Pixel);
            else if (_secondaryDef is RowDefinition r) r.Height = new GridLength(v, GridUnitType.Pixel);
        }

        private void SetSecondaryStar()
        {
            if (_secondaryDef is ColumnDefinition c) c.Width = new GridLength(1, GridUnitType.Star);
            else if (_secondaryDef is RowDefinition r) r.Height = new GridLength(1, GridUnitType.Star);
        }

        private void SetSplitterLength(double v)
        {
            if (_splitterDef is ColumnDefinition c) c.Width = new GridLength(v, GridUnitType.Pixel);
            else if (_splitterDef is RowDefinition r) r.Height = new GridLength(v, GridUnitType.Pixel);
        }

        private void SetSplitterFixed() => SetSplitterLength(SplitterWidth);

        #endregion

        #region DP

        public object PrimaryContent
        {
            get => GetValue(PrimaryContentProperty);
            set => SetValue(PrimaryContentProperty, value);
        }
        public static readonly DependencyProperty PrimaryContentProperty =
            DependencyProperty.Register(nameof(PrimaryContent), typeof(object), typeof(AdvancedSplitter), new PropertyMetadata(null));

        public object SecondaryContent
        {
            get => GetValue(SecondaryContentProperty);
            set => SetValue(SecondaryContentProperty, value);
        }
        public static readonly DependencyProperty SecondaryContentProperty =
            DependencyProperty.Register(nameof(SecondaryContent), typeof(object), typeof(AdvancedSplitter), new PropertyMetadata(null));

        public string PrimaryTitle
        {
            get => (string)GetValue(PrimaryTitleProperty);
            set => SetValue(PrimaryTitleProperty, value);
        }
        public static readonly DependencyProperty PrimaryTitleProperty =
            DependencyProperty.Register(nameof(PrimaryTitle), typeof(string), typeof(AdvancedSplitter), new PropertyMetadata("Primary"));

        public string SecondaryTitle
        {
            get => (string)GetValue(SecondaryTitleProperty);
            set => SetValue(SecondaryTitleProperty, value);
        }
        public static readonly DependencyProperty SecondaryTitleProperty =
            DependencyProperty.Register(nameof(SecondaryTitle), typeof(string), typeof(AdvancedSplitter), new PropertyMetadata("Secondary"));

        public bool ShowPrimaryHeader
        {
            get => (bool)GetValue(ShowPrimaryHeaderProperty);
            set => SetValue(ShowPrimaryHeaderProperty, value);
        }
        public static readonly DependencyProperty ShowPrimaryHeaderProperty =
            DependencyProperty.Register(nameof(ShowPrimaryHeader), typeof(bool), typeof(AdvancedSplitter), new PropertyMetadata(true));

        public bool ShowSecondaryHeader
        {
            get => (bool)GetValue(ShowSecondaryHeaderProperty);
            set => SetValue(ShowSecondaryHeaderProperty, value);
        }
        public static readonly DependencyProperty ShowSecondaryHeaderProperty =
            DependencyProperty.Register(nameof(ShowSecondaryHeader), typeof(bool), typeof(AdvancedSplitter), new PropertyMetadata(true));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }
        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(AdvancedSplitter),
                new PropertyMetadata(Orientation.Horizontal, OnOrientationChanged));

        public GridLength PrimaryLength
        {
            get => (GridLength)GetValue(PrimaryLengthProperty);
            set => SetValue(PrimaryLengthProperty, value);
        }
        public static readonly DependencyProperty PrimaryLengthProperty =
            DependencyProperty.Register(nameof(PrimaryLength), typeof(GridLength), typeof(AdvancedSplitter), new PropertyMetadata(new GridLength(280)));

        public double SplitterWidth
        {
            get => (double)GetValue(SplitterWidthProperty);
            set => SetValue(SplitterWidthProperty, value);
        }
        public static readonly DependencyProperty SplitterWidthProperty =
            DependencyProperty.Register(nameof(SplitterWidth), typeof(double), typeof(AdvancedSplitter),
                new PropertyMetadata(10.0, OnSplitterWidthChanged));

        public double PrimaryMinExpandedLength
        {
            get => (double)GetValue(PrimaryMinExpandedLengthProperty);
            set => SetValue(PrimaryMinExpandedLengthProperty, value);
        }
        public static readonly DependencyProperty PrimaryMinExpandedLengthProperty =
            DependencyProperty.Register(nameof(PrimaryMinExpandedLength), typeof(double), typeof(AdvancedSplitter), new PropertyMetadata(0.0));

        public double PrimaryCollapsedLength
        {
            get => (double)GetValue(PrimaryCollapsedLengthProperty);
            set => SetValue(PrimaryCollapsedLengthProperty, value);
        }
        public static readonly DependencyProperty PrimaryCollapsedLengthProperty =
            DependencyProperty.Register(nameof(PrimaryCollapsedLength), typeof(double), typeof(AdvancedSplitter), new PropertyMetadata(28.0));

        public double SecondaryCollapsedLength
        {
            get => (double)GetValue(SecondaryCollapsedLengthProperty);
            set => SetValue(SecondaryCollapsedLengthProperty, value);
        }
        public static readonly DependencyProperty SecondaryCollapsedLengthProperty =
            DependencyProperty.Register(nameof(SecondaryCollapsedLength), typeof(double), typeof(AdvancedSplitter), new PropertyMetadata(28.0));

        public double SecondaryMinLength
        {
            get => (double)GetValue(SecondaryMinLengthProperty);
            set => SetValue(SecondaryMinLengthProperty, value);
        }
        public static readonly DependencyProperty SecondaryMinLengthProperty =
            DependencyProperty.Register(nameof(SecondaryMinLength), typeof(double), typeof(AdvancedSplitter), new PropertyMetadata(0.0));

        public PaneDisplayState DisplayState
        {
            get => (PaneDisplayState)GetValue(DisplayStateProperty);
            set => SetValue(DisplayStateProperty, value);
        }
        public static readonly DependencyProperty DisplayStateProperty =
            DependencyProperty.Register(nameof(DisplayState), typeof(PaneDisplayState), typeof(AdvancedSplitter),
                new FrameworkPropertyMetadata(PaneDisplayState.Normal,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDisplayStateChanged));

        #endregion
    }
}
