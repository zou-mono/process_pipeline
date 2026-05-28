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
    }
}
