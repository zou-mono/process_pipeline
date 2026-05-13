using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
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

    // 泛型抽象基类：TControl 是 UserControl 类型，TData 是数据类型
    // TControl 必须是 UserControl，实现接口，且有无参构造函数
    public abstract class PaletteSetBase<TControl, TData> : IDisposable
        where TControl : UserControl, IPaletteControl<TData>, new()  
    {
        protected static PaletteSet _paletteSet = null;
        protected TControl _currentControl;
        protected TData _currentData;
        protected PaletteRefreshManager _refreshManager;
            
        //protected readonly Guid _paletteGuid = new Guid("7e8d4f9a-5b7c-4890-8a7b-123456789abc");

        protected abstract Guid PaletteGuid { get; }

        //// 单例模式（如果需要，可以改为非单例）
        //private static readonly Lazy<PaletteSetBase<TControl, TData>> _instance =
        //    new Lazy<PaletteSetBase<TControl, TData>>(() => CreateInstance());
        //public static PaletteSetBase<TControl, TData> Instance => _instance.Value;

        // 抽象工厂方法，让子类创建具体实例
        //protected abstract PaletteSetBase<TControl, TData> CreateInstance();

        protected PaletteSetBase()
        {
            AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            _refreshManager = new PaletteRefreshManager();
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            Dispose();
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

            //UpdateData(initialData != null ? initialData : GetInitialData());

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

                _refreshManager.StartListening(currentDoc);
            }
            //else
            //{
            //    if (_currentControl != null)
            //    {
            //        UpdateControlData();
            //    }
            //}
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
                _currentControl = null;
                if (_paletteSet != null && !_paletteSet.IsDisposed)
                {
                    _paletteSet.Visible = false;
                    _paletteSet.Close();
                    _paletteSet.Dispose();
                }
                _paletteSet = null;

                var currentDoc = AcadApp.DocumentManager.MdiActiveDocument;
                if (currentDoc != null && _refreshManager != null)
                {
                    _refreshManager.StopListening(currentDoc);
                }
            }
            catch (Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n关闭 PaletteSet 失败：{ex.Message}");
            }
        }
    }
}
