using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows;

namespace process_pipeline.Controls
{
    [TemplatePart(Name = PART_LeftHost, Type = typeof(ContentPresenter))]
    [TemplatePart(Name = PART_RightHost, Type = typeof(ContentPresenter))]
    [TemplatePart(Name = PART_SplitterCol, Type = typeof(ColumnDefinition))]
    [TemplatePart(Name = PART_LeftCol, Type = typeof(ColumnDefinition))]
    [TemplatePart(Name = PART_RightCol, Type = typeof(ColumnDefinition))]
    [TemplatePart(Name = PART_Splitter, Type = typeof(GridSplitter))]
    [TemplatePart(Name = PART_Toggle, Type = typeof(ToggleButton))]
    public class CollapsibleSplitter : Control
    {
        private const string PART_LeftHost = "PART_LeftHost";
        private const string PART_RightHost = "PART_RightHost";
        private const string PART_LeftCol = "PART_LeftCol";
        private const string PART_SplitterCol = "PART_SplitterCol";
        private const string PART_RightCol = "PART_RightCol";
        private const string PART_Splitter = "PART_Splitter";
        private const string PART_Toggle = "PART_Toggle";

        private ColumnDefinition _leftCol;
        private ColumnDefinition _rightCol;
        private ColumnDefinition _splitterCol;
        private GridSplitter _splitter;
        private ToggleButton _toggle;

        private double _lastExpandedWidth = 320.0;
        private bool _internalUpdating;

        static CollapsibleSplitter()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(CollapsibleSplitter),
                new FrameworkPropertyMetadata(typeof(CollapsibleSplitter)));
        }

        #region DP - LeftContent / RightContent
        public object LeftContent
        {
            get => GetValue(LeftContentProperty);
            set => SetValue(LeftContentProperty, value);
        }

        public static readonly DependencyProperty LeftContentProperty =
            DependencyProperty.Register(nameof(LeftContent), typeof(object), 
                typeof(CollapsibleSplitter), new PropertyMetadata(null));

        public object RightContent
        {
            get => GetValue(RightContentProperty);
            set => SetValue(RightContentProperty, value);
        }

        public static readonly DependencyProperty RightContentProperty =
            DependencyProperty.Register(nameof(RightContent), typeof(object), 
                typeof(CollapsibleSplitter), new PropertyMetadata(null));
        #endregion

        #region DP - IsCollapsed
        public bool IsCollapsed
        {
            get => (bool)GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        public static readonly DependencyProperty IsCollapsedProperty =
            DependencyProperty.Register(nameof(IsCollapsed), typeof(bool), typeof(CollapsibleSplitter),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCollapsedChanged));
        #endregion

        #region DP - Width Settings
        public double LeftMinExpandedWidth
        {
            get => (double)GetValue(LeftMinExpandedWidthProperty);
            set => SetValue(LeftMinExpandedWidthProperty, value);
        }

        public static readonly DependencyProperty LeftMinExpandedWidthProperty =
            DependencyProperty.Register(nameof(LeftMinExpandedWidth), typeof(double), 
                typeof(CollapsibleSplitter), new PropertyMetadata(180.0));

        public double LeftDefaultWidth
        {
            get => (double)GetValue(LeftDefaultWidthProperty);
            set => SetValue(LeftDefaultWidthProperty, value);
        }

        public static readonly DependencyProperty LeftDefaultWidthProperty =
            DependencyProperty.Register(nameof(LeftDefaultWidth), typeof(double), 
                typeof(CollapsibleSplitter), new PropertyMetadata(320.0));

        public double RightMinWidth
        {
            get => (double)GetValue(RightMinWidthProperty);
            set => SetValue(RightMinWidthProperty, value);
        }

        public static readonly DependencyProperty RightMinWidthProperty =
            DependencyProperty.Register(nameof(RightMinWidth), typeof(double), 
                typeof(CollapsibleSplitter), new PropertyMetadata(120.0));

        public double SplitterWidth
        {
            get => (double)GetValue(SplitterWidthProperty);
            set => SetValue(SplitterWidthProperty, value);
        }

        public static readonly DependencyProperty SplitterWidthProperty =
            DependencyProperty.Register(nameof(SplitterWidth), typeof(double), 
                typeof(CollapsibleSplitter), new PropertyMetadata(10.0));
        #endregion

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            DetachHandlers();

            _leftCol = GetTemplateChild(PART_LeftCol) as ColumnDefinition;
            _rightCol = GetTemplateChild(PART_RightCol) as ColumnDefinition;
            _splitterCol = GetTemplateChild(PART_SplitterCol) as ColumnDefinition;
            _splitter = GetTemplateChild(PART_Splitter) as GridSplitter;
            _toggle = GetTemplateChild(PART_Toggle) as ToggleButton;

            AttachHandlers();

            Loaded -= OnLoaded;
            Loaded += OnLoaded;
            SizeChanged -= OnSizeChanged;
            SizeChanged += OnSizeChanged;
        }

        private void AttachHandlers()
        {
            if (_splitter != null) _splitter.DragDelta += OnSplitterDragDelta;
            if (_toggle != null) _toggle.Click += OnToggleClick;
        }

        private void DetachHandlers()
        {
            if (_splitter != null) _splitter.DragDelta -= OnSplitterDragDelta;
            if (_toggle != null) _toggle.Click -= OnToggleClick;
            Loaded -= OnLoaded;
            SizeChanged -= OnSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_leftCol == null) return;

            if (_leftCol.Width.IsAbsolute && _leftCol.Width.Value > 0)
                _lastExpandedWidth = _leftCol.Width.Value;
            else
                _lastExpandedWidth = LeftDefaultWidth;

            ApplyState(IsCollapsed, false);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_internalUpdating) return;
            ClampCurrentWidth();
        }

        private static void OnIsCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (CollapsibleSplitter)d;
            c.ApplyState((bool)e.NewValue, true);
        }

        private void OnToggleClick(object sender, RoutedEventArgs e)
        {
            IsCollapsed = !IsCollapsed;
        }

        private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_leftCol == null || _rightCol == null || _splitterCol == null) return;
            //if (IsCollapsed) return;

            double current = _leftCol.ActualWidth;
            double target = current + e.HorizontalChange;
            double clamped = ClampToBounds(target);

            _internalUpdating = true;
            try
            {
                _leftCol.Width = new GridLength(clamped, GridUnitType.Pixel);
                _rightCol.Width = new GridLength(1, GridUnitType.Star);
                if (clamped >= LeftMinExpandedWidth) _lastExpandedWidth = clamped;
            }
            finally
            {
                _internalUpdating = false;
            }

            // 关键：拖拽结果反向同步状态
            const double eps = 1.0; // 防抖阈值
            bool shouldCollapsed = clamped <= eps;

            if (IsCollapsed != shouldCollapsed)
                IsCollapsed = shouldCollapsed;

            e.Handled = true;
        }

        private void ApplyState(bool collapsed, bool fromDp)
        {
            if (_leftCol == null || _rightCol == null || _splitterCol == null || _toggle == null) return;

            _internalUpdating = true;
            try
            {
                double splitterW = SplitterWidth;
                _splitterCol.Width = new GridLength(splitterW, GridUnitType.Pixel);
                _rightCol.MinWidth = RightMinWidth;

                if (collapsed)
                {
                    if (_leftCol.ActualWidth > 0) _lastExpandedWidth = Math.Max(_leftCol.ActualWidth, LeftMinExpandedWidth);

                    _leftCol.MinWidth = 0;
                    _leftCol.Width = new GridLength(0, GridUnitType.Pixel);
                    _toggle.IsChecked = true;
                }
                else
                {
                    double restore = _lastExpandedWidth;
                    if (double.IsNaN(restore) || restore <= 0) restore = LeftDefaultWidth;
                    restore = Math.Max(restore, LeftMinExpandedWidth);
                    restore = ClampToBounds(restore);

                    _leftCol.MinWidth = 0;
                    _leftCol.Width = new GridLength(restore, GridUnitType.Pixel);
                    _rightCol.Width = new GridLength(1, GridUnitType.Star);
                    _toggle.IsChecked = false;
                }
            }
            finally
            {
                _internalUpdating = false;
            }
        }

        private void ClampCurrentWidth()
        {
            if (_leftCol == null || IsCollapsed) return;

            double clamped = ClampToBounds(_leftCol.ActualWidth);
            if (Math.Abs(clamped - _leftCol.ActualWidth) < 0.5) return;

            _internalUpdating = true;
            try
            {
                _leftCol.Width = new GridLength(clamped, GridUnitType.Pixel);
                if (clamped >= LeftMinExpandedWidth) _lastExpandedWidth = clamped;
            }
            finally
            {
                _internalUpdating = false;
            }
        }

        private double ClampToBounds(double leftWidth)
        {
            if (_splitterCol == null) return Math.Max(0, leftWidth);

            double total = ActualWidth;
            double s = _splitterCol.ActualWidth > 0 ? _splitterCol.ActualWidth : SplitterWidth;
            double max = Math.Max(0, total - s - RightMinWidth);

            double min = IsCollapsed ? 0 : Math.Min(LeftMinExpandedWidth, max);
            return Math.Max(min, Math.Min(leftWidth, max));

            //// 拖拽时允许到 0（即使当前是展开态）
            //double min = 0;
            //return Math.Max(min, Math.Min(leftWidth, max));
        }
    }
}
