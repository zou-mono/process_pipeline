using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace WpfCompositeSplitter.Controls
{
    [TemplatePart(Name = PART_PrimaryDef, Type = typeof(DefinitionBase))]
    [TemplatePart(Name = PART_SecondaryDef, Type = typeof(DefinitionBase))]
    [TemplatePart(Name = PART_SplitterDef, Type = typeof(DefinitionBase))]
    [TemplatePart(Name = PART_Splitter, Type = typeof(GridSplitter))]
    public class AdvancedSplitter : Control
    {
        private const string PART_PrimaryDef = "PART_PrimaryDef";
        private const string PART_SecondaryDef = "PART_SecondaryDef";
        private const string PART_SplitterDef = "PART_SplitterDef";
        private const string PART_Splitter = "PART_Splitter";

        private DefinitionBase _primaryDef;
        private DefinitionBase _secondaryDef;
        private DefinitionBase _splitterDef;
        private GridSplitter _splitter;

        private double _lastExpandedLength = 280.0;
        private bool _internalUpdating;

        static AdvancedSplitter()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(AdvancedSplitter),
                new FrameworkPropertyMetadata(typeof(AdvancedSplitter)));
        }

        public AdvancedSplitter()
        {
            CommandBindings.Add(new CommandBinding(AdvancedSplitterCommands.TogglePrimaryOnly, OnTogglePrimaryOnly));
            CommandBindings.Add(new CommandBinding(AdvancedSplitterCommands.ToggleSecondaryOnly, OnToggleSecondaryOnly));
            CommandBindings.Add(new CommandBinding(AdvancedSplitterCommands.RestoreNormal, OnRestoreNormal));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            DetachHandlers();

            _primaryDef = GetTemplateChild(PART_PrimaryDef) as DefinitionBase;
            _secondaryDef = GetTemplateChild(PART_SecondaryDef) as DefinitionBase;
            _splitterDef = GetTemplateChild(PART_SplitterDef) as DefinitionBase;
            _splitter = GetTemplateChild(PART_Splitter) as GridSplitter;

            AttachHandlers();

            Loaded -= OnLoaded;
            Loaded += OnLoaded;
            SizeChanged -= OnSizeChanged;
            SizeChanged += OnSizeChanged;

            ApplyState(DisplayState);
        }

        private void AttachHandlers()
        {
            if (_splitter != null)
                _splitter.DragDelta += OnSplitterDragDelta;
        }

        private void DetachHandlers()
        {
            if (_splitter != null)
                _splitter.DragDelta -= OnSplitterDragDelta;

            Loaded -= OnLoaded;
            SizeChanged -= OnSizeChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (PrimaryLength.IsAbsolute && PrimaryLength.Value > 0)
                _lastExpandedLength = PrimaryLength.Value;

            ApplyState(DisplayState);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_internalUpdating) return;
            ClampCurrentPrimaryLength();
        }

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

        private void OnTogglePrimaryOnly(object sender, ExecutedRoutedEventArgs e)
        {
            DisplayState = DisplayState == PaneDisplayState.PrimaryOnly
                ? PaneDisplayState.Normal
                : PaneDisplayState.PrimaryOnly;
        }

        private void OnToggleSecondaryOnly(object sender, ExecutedRoutedEventArgs e)
        {
            DisplayState = DisplayState == PaneDisplayState.SecondaryOnly
                ? PaneDisplayState.Normal
                : PaneDisplayState.SecondaryOnly;
        }

        private void OnRestoreNormal(object sender, ExecutedRoutedEventArgs e)
        {
            DisplayState = PaneDisplayState.Normal;
        }

        private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_primaryDef == null || _secondaryDef == null || _splitterDef == null) return;

            // 折叠态：禁止拖动
            if (DisplayState != PaneDisplayState.Normal)
            {
                e.Handled = true;
                return;
            }

            double current = GetPrimaryActualLength();
            double delta = Orientation == Orientation.Horizontal ? e.HorizontalChange : e.VerticalChange;
            double target = current + delta;
            double clamped = ClampToBounds(target, useCollapsedMin: false);

            _internalUpdating = true;
            try
            {
                SetPrimaryLength(clamped);   // 始终Pixel，避免Star导致抖动
                SetSplitterFixed();          // Normal下splitter可见
                SetSecondaryStar();

                if (clamped >= PrimaryMinExpandedLength)
                    _lastExpandedLength = clamped;
            }
            finally
            {
                _internalUpdating = false;
            }

            e.Handled = true;
        }

        private void ApplyState(PaneDisplayState state)
        {
            if (_primaryDef == null || _secondaryDef == null || _splitterDef == null) return;

            _internalUpdating = true;
            try
            {
                switch (state)
                {
                    case PaneDisplayState.PrimaryOnly:
                        if (GetPrimaryActualLength() > PrimaryCollapsedLength)
                            _lastExpandedLength = Math.Max(GetPrimaryActualLength(), PrimaryMinExpandedLength);

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
                        double restore = _lastExpandedLength;
                        if (double.IsNaN(restore) || restore <= 0)
                            restore = PrimaryLength.IsAbsolute ? PrimaryLength.Value : 280.0;

                        restore = Math.Max(restore, PrimaryMinExpandedLength);
                        restore = ClampToBounds(restore, useCollapsedMin: false);

                        SetPrimaryLength(restore);
                        SetSplitterFixed();                       // Normal显示splitter
                        SetSecondaryStar();
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

        private void ClampCurrentPrimaryLength()
        {
            if (_primaryDef == null || DisplayState != PaneDisplayState.Normal) return;

            double current = GetPrimaryActualLength();
            double clamped = ClampToBounds(current, useCollapsedMin: false);

            if (Math.Abs(clamped - current) < 0.5) return;

            _internalUpdating = true;
            try
            {
                SetPrimaryLength(clamped);
                SetSplitterFixed();
                SetSecondaryStar();

                if (clamped >= PrimaryMinExpandedLength)
                    _lastExpandedLength = clamped;
            }
            finally
            {
                _internalUpdating = false;
            }
        }

        private double ClampToBounds(double primaryLength, bool useCollapsedMin)
        {
            double total = Orientation == Orientation.Horizontal ? ActualWidth : ActualHeight;
            if (double.IsNaN(total) || total <= 0) return Math.Max(0, primaryLength);

            double s = SplitterWidth;
            double secMin = SecondaryMinLength;
            double max = Math.Max(0, total - s - secMin);

            double min = useCollapsedMin ? PrimaryCollapsedLength : PrimaryMinExpandedLength;
            min = Math.Min(min, max);

            return Math.Max(min, Math.Min(primaryLength, max));
        }

        private double GetPrimaryActualLength()
        {
            if (_primaryDef is ColumnDefinition c) return c.ActualWidth;
            if (_primaryDef is RowDefinition r) return r.ActualHeight;
            return 0;
        }

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
