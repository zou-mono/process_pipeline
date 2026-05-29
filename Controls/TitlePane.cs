using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Drawing;

namespace process_pipeline.Controls
{
    /// <summary>
    /// 带标题栏的pane控件，左侧是标题名，右侧是一个togglebutton
    /// </summary>    
    public class TitlePane : ContentControl
    {
        static TitlePane()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(TitlePane),
                new FrameworkPropertyMetadata(typeof(TitlePane)));
        }
        #region Title 标题名称
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitlePane), new PropertyMetadata("Pane"));

        #endregion

        #region ShowHeader 是否显示标题栏
        public bool ShowHeader
        {
            get => (bool)GetValue(ShowHeaderProperty);
            set => SetValue(ShowHeaderProperty, value);
        }
        public static readonly DependencyProperty ShowHeaderProperty =
            DependencyProperty.Register(nameof(ShowHeader), typeof(bool), typeof(TitlePane), new PropertyMetadata(true));

        #endregion

        #region ToggleContent 右侧toggleButton中显示的内容
        public object ToggleContent
        {
            get => GetValue(ToggleContentProperty);
            set => SetValue(ToggleContentProperty, value);
        }
        public static readonly DependencyProperty ToggleContentProperty =
            DependencyProperty.Register(nameof(ToggleContent), typeof(object), typeof(TitlePane), new PropertyMetadata("◀"));

        #endregion

        public ICommand ToggleCommand
        {
            get => (ICommand)GetValue(ToggleCommandProperty);
            set => SetValue(ToggleCommandProperty, value);
        }
        public static readonly DependencyProperty ToggleCommandProperty =
            DependencyProperty.Register(nameof(ToggleCommand), typeof(ICommand), typeof(TitlePane), new PropertyMetadata(null));

        public bool IsPaneCollapsed
        {
            get => (bool)GetValue(IsPaneCollapsedProperty);
            set => SetValue(IsPaneCollapsedProperty, value);
        }
        public static readonly DependencyProperty IsPaneCollapsedProperty =
            DependencyProperty.Register(nameof(IsPaneCollapsed), typeof(bool), typeof(TitlePane), new PropertyMetadata(false));

        #region 外部可覆盖样式 DP

        /// <summary>最外层 Border 的样式。</summary>
        public Style PaneBorderStyle
        {
            get => (Style)GetValue(PaneBorderStyleProperty);
            set => SetValue(PaneBorderStyleProperty, value);
        }
        public static readonly DependencyProperty PaneBorderStyleProperty =
            DependencyProperty.Register(nameof(PaneBorderStyle), typeof(Style), typeof(TitlePane), new PropertyMetadata(null));

        /// <summary>Header 容器 Grid 的样式。</summary>
        public Style HeaderDockStyle
        {
            get => (Style)GetValue(HeaderDockStyleProperty);
            set => SetValue(HeaderDockStyleProperty, value);
        }
        public static readonly DependencyProperty HeaderDockStyleProperty =
            DependencyProperty.Register(nameof(HeaderDockStyle), typeof(Style), typeof(TitlePane), new PropertyMetadata(null));

        /// <summary>标题 TextBlock 的样式。</summary>
        public Style HeaderTextStyle
        {
            get => (Style)GetValue(HeaderTextStyleProperty);
            set => SetValue(HeaderTextStyleProperty, value);
        }
        public static readonly DependencyProperty HeaderTextStyleProperty =
            DependencyProperty.Register(nameof(HeaderTextStyle), typeof(Style), typeof(TitlePane), new PropertyMetadata(null));

        /// <summary>折叠按钮 Button 的样式。</summary>
        public Style HeaderButtonStyle
        {
            get => (Style)GetValue(HeaderButtonStyleProperty);
            set => SetValue(HeaderButtonStyleProperty, value);
        }
        public static readonly DependencyProperty HeaderButtonStyleProperty =
            DependencyProperty.Register(nameof(HeaderButtonStyle), typeof(Style), typeof(TitlePane), new PropertyMetadata(null));

        /// <summary>内容区 ContentPresenter 的样式（可选增强）</summary>
        public Style PaneContentStyle
        {
            get => (Style)GetValue(PaneContentStyleProperty);
            set => SetValue(PaneContentStyleProperty, value);
        }
        public static readonly DependencyProperty PaneContentStyleProperty =
            DependencyProperty.Register(nameof(PaneContentStyle), typeof(Style), typeof(TitlePane), new PropertyMetadata(null));

        public GridLength HeaderHeight
        {
            get => (GridLength)GetValue(HeaderHeightProperty);
            set => SetValue(HeaderHeightProperty, value);
        }
        public static readonly DependencyProperty HeaderHeightProperty =
            DependencyProperty.Register(nameof(HeaderHeight), typeof(GridLength), typeof(TitlePane), new PropertyMetadata(new GridLength(30.0)));

        #endregion
    }
}
