using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;

namespace WpfCompositeSplitter.Controls
{
    public class PaneHost : ContentControl
    {
        static PaneHost()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(PaneHost),
                new FrameworkPropertyMetadata(typeof(PaneHost)));
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(PaneHost), new PropertyMetadata("Pane"));

        public bool ShowHeader
        {
            get => (bool)GetValue(ShowHeaderProperty);
            set => SetValue(ShowHeaderProperty, value);
        }
        public static readonly DependencyProperty ShowHeaderProperty =
            DependencyProperty.Register(nameof(ShowHeader), typeof(bool), typeof(PaneHost), new PropertyMetadata(true));

        public object ToggleContent
        {
            get => GetValue(ToggleContentProperty);
            set => SetValue(ToggleContentProperty, value);
        }
        public static readonly DependencyProperty ToggleContentProperty =
            DependencyProperty.Register(nameof(ToggleContent), typeof(object), typeof(PaneHost), new PropertyMetadata("◀"));

        public ICommand ToggleCommand
        {
            get => (ICommand)GetValue(ToggleCommandProperty);
            set => SetValue(ToggleCommandProperty, value);
        }
        public static readonly DependencyProperty ToggleCommandProperty =
            DependencyProperty.Register(nameof(ToggleCommand), typeof(ICommand), typeof(PaneHost), new PropertyMetadata(null));

        public bool IsPaneCollapsed
        {
            get => (bool)GetValue(IsPaneCollapsedProperty);
            set => SetValue(IsPaneCollapsedProperty, value);
        }
        public static readonly DependencyProperty IsPaneCollapsedProperty =
            DependencyProperty.Register(nameof(IsPaneCollapsed), typeof(bool), typeof(PaneHost), new PropertyMetadata(false));

        public GridLength HeaderHeight
        {
            get => (GridLength)GetValue(HeaderHeightProperty);
            set => SetValue(HeaderHeightProperty, value);
        }
        public static readonly DependencyProperty HeaderHeightProperty =
            DependencyProperty.Register(nameof(HeaderHeight), typeof(GridLength), typeof(PaneHost), new PropertyMetadata(new GridLength(30.0)));
    }
}
