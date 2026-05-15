using Microsoft.Xaml.Behaviors;
using process_pipeline.Themes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Core
{
    // Behavior: 处理 CAD 主题同步
    public class CadThemeBehavior : Behavior<UserControl>
    {
        protected override void OnAttached()
        {
            base.OnAttached();
            AcadApp.SystemVariableChanged += AcadApp_SystemVariableChanged;
        }

        protected override void OnDetaching()
        {
            AcadApp.SystemVariableChanged -= AcadApp_SystemVariableChanged;
            base.OnDetaching();
        }

        private void AcadApp_SystemVariableChanged(object sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
        {
            if (e.Name.Equals("COLORTHEME", StringComparison.OrdinalIgnoreCase))
            {
                AssociatedObject.Dispatcher.Invoke(() => CadThemes.ApplyCadTheme(AssociatedObject));
            }
        }
    }

    // Behavior: 处理 CAD 选择同步（可扩展为虚方法或事件）
    public class SelectionSyncBehavior : Behavior<UserControl>
    {
        // 可选：添加属性让 UserControl 自定义逻辑
        public static readonly DependencyProperty OnSelectionChangedProperty =
            DependencyProperty.Register("OnSelectionChanged", typeof(Action), typeof(SelectionSyncBehavior), new PropertyMetadata(null));

        public Action OnSelectionChanged
        {
            get => (Action)GetValue(OnSelectionChangedProperty);
            set => SetValue(OnSelectionChangedProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.ImpliedSelectionChanged += Editor_ImpliedSelectionChanged;
            }
        }

        protected override void OnDetaching()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.ImpliedSelectionChanged -= Editor_ImpliedSelectionChanged;
            }
            base.OnDetaching();
        }

        private void Editor_ImpliedSelectionChanged(object sender, EventArgs e)
        {
            AssociatedObject.Dispatcher.Invoke(() =>
            {
                OnSelectionChanged?.Invoke();  // 调用自定义逻辑
            });
        }
    }
}
