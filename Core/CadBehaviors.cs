using Microsoft.Xaml.Behaviors;
using process_pipeline.Themes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Core
{
    // Behavior: 处理 CAD 主题同步（支持 UserControl 与 Window 等任意 FrameworkElement）
    public class CadThemeBehavior : Behavior<FrameworkElement>
    {
        protected override void OnAttached()
        {
            base.OnAttached();
            if (AssociatedObject != null)
            {
                CadThemes.ApplyCadTheme(AssociatedObject);
            }
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
                AssociatedObject?.Dispatcher.Invoke(() => CadThemes.ApplyCadTheme(AssociatedObject));
            }
        }
    }

    // Behavior: 处理 CAD 选择同步（支持 MDI 多文档切换自动重绑，Stage 7 Commit 7.4）
    public class CadSelectionSyncBehavior : Behavior<UserControl>
    {
        private Autodesk.AutoCAD.ApplicationServices.Document _subscribedDocument;

        // 可选：添加属性让 UserControl 自定义逻辑
        public static readonly DependencyProperty OnCadSelectionChangedProperty =
                DependencyProperty.Register(nameof(OnCadSelectionChanged), typeof(ICommand),
                    typeof(CadSelectionSyncBehavior), new PropertyMetadata(null));

        public ICommand OnCadSelectionChanged
        {
            get => (ICommand)GetValue(OnCadSelectionChangedProperty);
            set => SetValue(OnCadSelectionChangedProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();
            if (AcadApp.DocumentManager != null)
            {
                AcadApp.DocumentManager.DocumentActivated += DocumentManager_DocumentActivated;
                AcadApp.DocumentManager.DocumentToBeDestroyed += DocumentManager_DocumentToBeDestroyed;
                AttachToDocument(AcadApp.DocumentManager.MdiActiveDocument);
            }
        }

        protected override void OnDetaching()
        {
            if (AcadApp.DocumentManager != null)
            {
                AcadApp.DocumentManager.DocumentActivated -= DocumentManager_DocumentActivated;
                AcadApp.DocumentManager.DocumentToBeDestroyed -= DocumentManager_DocumentToBeDestroyed;
            }
            AttachToDocument(null);
            base.OnDetaching();
        }

        private void DocumentManager_DocumentActivated(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            AttachToDocument(e?.Document ?? AcadApp.DocumentManager?.MdiActiveDocument);
        }

        private void DocumentManager_DocumentToBeDestroyed(object sender, Autodesk.AutoCAD.ApplicationServices.DocumentCollectionEventArgs e)
        {
            if (e?.Document != null && ReferenceEquals(_subscribedDocument, e.Document))
            {
                AttachToDocument(null);
            }
        }

        private void AttachToDocument(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            if (ReferenceEquals(_subscribedDocument, doc))
                return;

            if (_subscribedDocument != null)
            {
                try
                {
                    _subscribedDocument.ImpliedSelectionChanged -= Editor_ImpliedSelectionChanged;
                }
                catch
                {
                    // 忽略已销毁文档的事件解绑异常
                }
                _subscribedDocument = null;
            }

            if (doc != null)
            {
                try
                {
                    doc.ImpliedSelectionChanged += Editor_ImpliedSelectionChanged;
                    _subscribedDocument = doc;
                }
                catch
                {
                    _subscribedDocument = null;
                }
            }
        }

        private void Editor_ImpliedSelectionChanged(object sender, EventArgs e)
        {
            // 【关键修复】增加严格的空值保护
            if (OnCadSelectionChanged == null) 
                return;

            AssociatedObject.Dispatcher.Invoke(() =>
            {
                try
                {
                    if (OnCadSelectionChanged.CanExecute(null))
                    {
                        OnCadSelectionChanged.Execute(null);
                    }
                }
                catch (Exception ex)
                {
                    // 防止 Behavior 崩溃整个面板
                    System.Diagnostics.Debug.WriteLine($"CadSelectionSyncBehavior 执行失败: {ex.Message}");
                }
            });
        }
    }
}
