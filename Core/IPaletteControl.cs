using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using process_pipeline.Themes;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Core
{
    public interface IPaletteControl<TData>
    {
        // 统一的更新数据方法，子类 UserControl 需要实现
        void UpdateData(TData data);
    }

    /// <summary>
    /// 泛型面板抽象基类：TControl 是 UserControl 类型，TData 是数据类型。
    /// 
    /// 架构改进说明（Commit 3.1）：
    /// 1. 将原有的 static PaletteSet _paletteSet 改为实例字段，避免不同泛型子类或多实例间的静态引用隐患。
    /// 2. 完善多文档（MDI）生命周期管理：
    ///    - 记录当前正在监听的文档 _listeningDoc；
    ///    - 当关闭某个后台非当前核查文档时，不再误杀当前打开的 PaletteSet；
    ///    - 监听 DocumentActivated 事件，当用户在多个 DWG 标签页之间切换时，自动将 _refreshManager
    ///      平滑切换到新激活的文档并清理残留的瞬态辅助线。
    /// </summary>
    public abstract class PaletteSetBase<TControl, TData> : IDisposable
        where TControl : UserControl, IPaletteControl<TData>, new()
    {
        protected PaletteSet _paletteSet = null;
        protected TControl _currentControl;
        protected TData _currentData;
        protected PaletteRefreshManager _refreshManager;
        protected Document _listeningDoc;

        protected abstract Guid PaletteGuid { get; }

        protected PaletteSetBase()
        {
            AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            AcadApp.DocumentManager.DocumentActivated += OnDocumentActivated;
            _refreshManager = new PaletteRefreshManager();
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e?.Document == null) return;

            // 清理屏幕上的瞬态辅助图形，防止跨文档残留
            GraphicManager.ClearAuxiliaryGraphics();

            if (_listeningDoc == e.Document)
            {
                _refreshManager?.StopListening(e.Document);
                _listeningDoc = null;
            }

            // 仅当最后一个文档也要关闭时才彻底销毁面板
            if (AcadApp.DocumentManager.Count <= 1)
            {
                Dispose();
            }
        }

        private void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            if (e?.Document == null || !IsVisible) return;
            if (_listeningDoc == e.Document) return;

            GraphicManager.ClearAuxiliaryGraphics();

            if (_listeningDoc != null)
            {
                _refreshManager?.StopListening(_listeningDoc);
            }

            _listeningDoc = e.Document;
            _refreshManager?.StartListening(_listeningDoc);
            OnActiveDocumentChanged(e.Document);
        }

        /// <summary>
        /// 当 MDI 活动文档切换时的钩子方法，子类可重写以从 DocumentCheckSession 恢复目标文档的数据。
        /// </summary>
        protected virtual void OnActiveDocumentChanged(Document newDoc)
        {
        }

        // 抽象方法：子类提供 PaletteSet 的标题和名称
        protected abstract string GetPaletteTitle();
        protected abstract string GetPaletteName();

        // 抽象方法：子类提供初始数据（如果需要）
        protected abstract TData GetInitialData();

        // 显示 PaletteSet（共性逻辑）
        public void Show(TData initialData = default)
        {
            var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
            if (currentDoc == null) return;

            if (_paletteSet == null || _paletteSet.IsDisposed)
            {
                _paletteSet = new PaletteSet(GetPaletteTitle(), GetPaletteName(), PaletteGuid)
                {
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.Top | DockSides.Bottom,
                    MinimumSize = new System.Drawing.Size(600, 400),
                    Size = new System.Drawing.Size(600, 700),
                    Visible = true
                };

                _currentControl = new TControl();  // 创建泛型控件
                _paletteSet.AddVisual("内容", _currentControl);  // 嵌入控件

                _listeningDoc = currentDoc;
                _refreshManager.StartListening(currentDoc);
            }
            else if (_listeningDoc != currentDoc)
            {
                if (_listeningDoc != null)
                {
                    _refreshManager.StopListening(_listeningDoc);
                }
                _listeningDoc = currentDoc;
                _refreshManager.StartListening(currentDoc);
            }

            UpdateData(initialData != null ? initialData : GetInitialData());

            _paletteSet.Visible = true;
            _paletteSet.Activate(0);
            _paletteSet.Focus();
        }

        // 更新数据（共性逻辑）
        public void UpdateData(TData newData)
        {
            _currentData = newData;
            if (_currentControl != null)
            {
                // 跨线程安全更新
                if (!_currentControl.Dispatcher.CheckAccess())
                {
                    _currentControl.Dispatcher.Invoke(() => UpdateControlData());
                }
                else
                {
                    UpdateControlData();
                }
            }
        }

        // 内部方法：调用控件的更新方法
        private void UpdateControlData()
        {
            _currentControl.UpdateData(_currentData);
        }

        // 刷新数据（抽象方法，子类实现具体刷新逻辑）
        public abstract void RefreshData();

        // 隐藏
        public void Hide()
        {
            if (_paletteSet != null && !_paletteSet.IsDisposed)
                _paletteSet.Visible = false;
        }

        // 是否可见
        public bool IsVisible => _paletteSet != null ? _paletteSet.Visible : false;

        // Dispose（共性逻辑）
        public void Dispose()
        {
            try
            {
                GraphicManager.ClearAuxiliaryGraphics();

                _currentControl = null;
                if (_paletteSet != null && !_paletteSet.IsDisposed)
                {
                    _paletteSet.Visible = false;
                    _paletteSet.Close();
                    _paletteSet.Dispose();
                }
                _paletteSet = null;

                var docToStop = _listeningDoc ?? AcadApp.DocumentManager.MdiActiveDocument;
                if (docToStop != null && _refreshManager != null)
                {
                    _refreshManager.StopListening(docToStop);
                }
                _listeningDoc = null;
            }
            catch (Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n关闭 PaletteSet 失败：{ex.Message}");
            }
        }
    }
}
