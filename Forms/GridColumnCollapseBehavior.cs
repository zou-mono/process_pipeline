using Microsoft.Xaml.Behaviors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace process_pipeline.Forms
{
    internal class GridColumnCollapseBehavior : Behavior<Grid>
    {
        private double _lastWidth = 320d;
        private bool _updatingInternally;

        #region DP: TargetColumnIndex
        public int TargetColumnIndex
        {
            get => (int)GetValue(TargetColumnIndexProperty);
            set => SetValue(TargetColumnIndexProperty, value);
        }

        public static readonly DependencyProperty TargetColumnIndexProperty =
            DependencyProperty.Register(
                nameof(TargetColumnIndex),
                typeof(int),
                typeof(GridColumnCollapseBehavior),
                new PropertyMetadata(0));
        #endregion

        #region DP: IsCollapsed (TwoWay)
        public bool IsCollapsed
        {
            get => (bool)GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        public static readonly DependencyProperty IsCollapsedProperty =
            DependencyProperty.Register(
                nameof(IsCollapsed),
                typeof(bool),
                typeof(GridColumnCollapseBehavior),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCollapsedChanged));
        #endregion

        #region DP: MinExpandedWidth
        public double MinExpandedWidth
        {
            get => (double)GetValue(MinExpandedWidthProperty);
            set => SetValue(MinExpandedWidthProperty, value);
        }

        public static readonly DependencyProperty MinExpandedWidthProperty =
            DependencyProperty.Register(
                nameof(MinExpandedWidth),
                typeof(double),
                typeof(GridColumnCollapseBehavior),
                new PropertyMetadata(180d));
        #endregion

        #region DP: DefaultExpandedWidth
        public double DefaultExpandedWidth
        {
            get => (double)GetValue(DefaultExpandedWidthProperty);
            set => SetValue(DefaultExpandedWidthProperty, value);
        }

        public static readonly DependencyProperty DefaultExpandedWidthProperty =
            DependencyProperty.Register(
                nameof(DefaultExpandedWidth),
                typeof(double),
                typeof(GridColumnCollapseBehavior),
                new PropertyMetadata(320d));
        #endregion

        protected override void OnAttached()
        {
            base.OnAttached();

            AssociatedObject.Loaded += OnLoaded;
            AssociatedObject.SizeChanged += OnGridSizeChanged;
        }

        protected override void OnDetaching()
        {
            AssociatedObject.Loaded -= OnLoaded;
            AssociatedObject.SizeChanged -= OnGridSizeChanged;
            base.OnDetaching();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var col = GetTargetColumn();
            if (col == null) return;

            // 初始记忆宽度
            if (col.Width.IsAbsolute && col.Width.Value > 0)
                _lastWidth = Math.Max(col.Width.Value, MinExpandedWidth);
            else
                _lastWidth = Math.Max(DefaultExpandedWidth, MinExpandedWidth);

            // 根据 IsCollapsed 应用一次
            ApplyCollapsedState(IsCollapsed);
        }

        private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_updatingInternally) return;
            var col = GetTargetColumn();
            if (col == null) return;

            // 用户拖动 splitter 后，会改变绝对宽度；实时记忆
            if (!IsCollapsed && col.Width.IsAbsolute && col.Width.Value >= MinExpandedWidth)
            {
                _lastWidth = col.Width.Value;
            }
        }

        private static void OnIsCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            GridColumnCollapseBehavior b = d as GridColumnCollapseBehavior;
            if (b == null || b.AssociatedObject == null) 
                return;

            b.ApplyCollapsedState((bool)e.NewValue);
        }

        private void ApplyCollapsedState(bool collapse)
        {
            var col = GetTargetColumn();
            if (col == null) return;

            _updatingInternally = true;
            try
            {
                if (collapse)
                {
                    // 折叠前记住当前宽度
                    if (col.Width.IsAbsolute && col.Width.Value > 0)
                        _lastWidth = Math.Max(col.Width.Value, MinExpandedWidth);

                    col.MinWidth = 0;
                    col.Width = new GridLength(0, GridUnitType.Pixel);
                }
                else
                {
                    var restore = Math.Max(_lastWidth, MinExpandedWidth);
                    if (double.IsNaN(restore) || restore <= 0)
                        restore = Math.Max(DefaultExpandedWidth, MinExpandedWidth);

                    col.MinWidth = 0;
                    col.Width = new GridLength(restore, GridUnitType.Pixel);
                }
            }
            finally
            {
                _updatingInternally = false;
            }
        }

        private ColumnDefinition GetTargetColumn()
        {
            if (AssociatedObject == null) return null;
            if (TargetColumnIndex < 0 || TargetColumnIndex >= AssociatedObject.ColumnDefinitions.Count) return null;
            return AssociatedObject.ColumnDefinitions[TargetColumnIndex];
        }
    }
}
