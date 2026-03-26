using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using process_pipeline.Commands;
using process_pipeline.Core;
using process_pipeline.Forms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Utils
{
    public class SysVarScope : IDisposable
    {
        private readonly Dictionary<string, object> _old = new Dictionary<string, object>();
        private bool _disposed;

        public SysVarScope(Dictionary<string, object> newValues)
        {
            foreach (var kv in newValues)
            {
                try
                {
                    _old[kv.Key] = AcadApp.GetSystemVariable(kv.Key);
                    AcadApp.SetSystemVariable(kv.Key, kv.Value);
                }
                catch
                {
                    // 某些CAD不支持该变量时忽略
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kv in _old)
            {
                try
                {
                    AcadApp.SetSystemVariable(kv.Key, kv.Value);
                }
                catch
                {
                    // 恢复失败也不阻断
                }
            }
        }
    }

    public static class DbgLog
    {
        private const string Prefix = "\n[Debug] ";
        
        [Conditional("DEBUG")]
        public static void Write(Editor ed, string message)
        {
            ed.WriteMessage(Prefix + message);
        }

        [Conditional("DEBUG")]
        public static void Write(Editor ed, string format, params object[] args)
        {
            if (ed == null) return;
            ed.WriteMessage(Prefix + string.Format(format, args));
        }
    }

    public class SortableBindingList<T> : BindingList<T>
    {
        // 分离「原始数据源」和「当前显示的数据源」
        private readonly List<T> _originalSourceList; // 永久保存原始数据，不修改
        private List<T> _currentSourceList;           // 排序后的当前数据
        //private List<T> _sourceList; // 原始数据源
        private PropertyDescriptor _sortProperty; // 排序字段
        private ListSortDirection _sortDirection; // 排序方向

        // 重写IBindingList的排序属性，标记支持排序
        protected override bool SupportsSortingCore => true; // 关键：告诉DataGridView支持排序
        protected override bool IsSortedCore => _sortProperty != null; // 是否正在排序
        protected override ListSortDirection SortDirectionCore => _sortDirection; // 当前排序方向
        protected override PropertyDescriptor SortPropertyCore => _sortProperty; // 当前排序字段

        public SortableBindingList(List<T> list)
        {
            // 原始数据永久保存，不修改
            _originalSourceList = new List<T>(list ?? new List<T>());
            // 当前数据初始化为原始数据的副本
            _currentSourceList = new List<T>(_originalSourceList);
            ResetItems();
        }

        // 重置列表项
        private void ResetItems()
        {
            RaiseListChangedEvents = false;
            ClearItems();
            foreach (var item in _currentSourceList)
            {
                Add(item);
            }
            RaiseListChangedEvents = true;
            OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
        }

        // 重写标准排序方法（IBindingList要求）
        protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection direction)
        {
            //Sort(prop, direction);
            // 强制首次反向（仅当当前未排序时）
            if (_sortProperty == null && direction == ListSortDirection.Ascending)
                direction = ListSortDirection.Descending;
            Sort(prop, direction);
        }

        // 取消排序的实现
        protected override void RemoveSortCore()
        {
            _sortProperty = null;
            _sortDirection = ListSortDirection.Ascending;
            _currentSourceList = new List<T>(_originalSourceList); // 恢复原始数据
            ResetItems();
        }

        // 重写Sort方法，修复null值问题
        public void Sort(PropertyDescriptor prop, ListSortDirection direction)
        {
            if (prop == null || _originalSourceList == null || _originalSourceList.Count <= 1)
                return;

            _sortProperty = prop;
            _sortDirection = direction;

            try
            {
                // 核心修复5：处理null值，避免空引用异常
                _currentSourceList  = direction == ListSortDirection.Ascending
                    ? _originalSourceList.OrderBy(x =>
                    {
                        var value = prop.GetValue(x);
                        return value ?? DBNull.Value; // null值转为DBNull，避免排序报错
                    }).ToList()
                    : _originalSourceList.OrderByDescending(x =>
                    {
                        var value = prop.GetValue(x);
                        return value ?? DBNull.Value;
                    }).ToList();

                // 刷新列表
                ResetItems();
            }
            catch (System.Exception ex)
            {
                // 捕获排序异常（比如属性类型不支持比较）
                System.Diagnostics.Debug.WriteLine($"排序失败：{ex.Message}");
            }
        }

        // 获取当前排序状态（供UI同步图标）
        public (PropertyDescriptor Prop, ListSortDirection Direction) GetCurrentSortState()
        {
            return (_sortProperty, _sortDirection);
        }
    }

    public class GlobalEventManager : IExtensionApplication
    {
        private bool _idleSubscribed = false;
        private bool _needRefresh = false;
        public void Initialize()
        {
            // 1. 给当前已经打开的图纸挂上监听
            foreach (Document doc in Application.DocumentManager)
            { 
                doc.CommandEnded += Doc_CommandEnded;
            }

            // 2. 监听未来新建或打开的图纸
            Application.DocumentManager.DocumentCreated += DocumentManager_DocumentCreated;
        
            // 3. 监听图纸关闭，用于释放资源
            Application.DocumentManager.DocumentToBeDestroyed += DocumentManager_DocumentToBeDestroyed;
        }

        public void Terminate()
        {
            // 插件卸载或 CAD 关闭时，完美释放所有订阅
            Application.DocumentManager.DocumentCreated -= DocumentManager_DocumentCreated;
            Application.DocumentManager.DocumentToBeDestroyed -= DocumentManager_DocumentToBeDestroyed;

            foreach (Document doc in Application.DocumentManager)
            {
                doc.CommandEnded -= Doc_CommandEnded;
            }

            if (_idleSubscribed)
            {
                Application.Idle -= OnIdleRefresh;
            }
        }

        private void DocumentManager_DocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != null)
            {
                e.Document.CommandEnded += Doc_CommandEnded;
            }
        }

        private void DocumentManager_DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != null)
            {
                e.Document.CommandEnded -= Doc_CommandEnded;
            }
        }

        // 【核心】任何图纸的任何命令结束，都会进入这里
        private void Doc_CommandEnded(object sender, CommandEventArgs e)
        {
            string cmdName = e.GlobalCommandName.ToUpper();

            // 捕捉撤销(U)和重做(REDO)的所有变体
            if (cmdName == "U" || cmdName == "_U" ||
                cmdName == "UNDO" || cmdName == "_UNDO" ||
                cmdName == "REDO" || cmdName == "_REDO" ||
                cmdName == "MREDO" || cmdName == "OOPS")
            {
                try
                {
                    // 走到这里，说明图纸已经撤销完毕
                    // 触发你的窗体全局刷新逻辑！
                    // palCheckArrow.Instance?.RefreshAll(); 
                    // Undo 即将开始 → 可以备份当前 problems 状态，或标记需要重新检查
                    // 但这里只能做简单处理（如延迟刷新）
                    if (palCheckArrow.Instance.IsVisible) {
                        //palCheckArrow.Instance.RefreshProblems();
                        // 标记需要刷新，但不立即执行
                        _needRefresh = true;
                
                        if (!_idleSubscribed)
                        {
                            Application.Idle += OnIdleRefresh;
                            _idleSubscribed = true;
                        }
                    }
                }
                catch
                {
                    // 捕获异常，防止刷新 UI 失败导致 CAD 崩溃
                }
            }
        }

        private void OnIdleRefresh(object sender, EventArgs e)
        {
            Application.Idle -= OnIdleRefresh;
            _idleSubscribed = false;
        
            if (!_needRefresh) return;
            _needRefresh = false;
        
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            try
            { 
                // 现在在 Idle 状态下，应该更安全
                var service = new FlowArrowService(doc.Database, doc.Editor, useEditor: false);
                List<ProblemItem> problems = service.RunChecker();
            
                palCheckArrow.Instance.Update(problems);
            }
            catch (System.Exception ex)
            {
                DbgLog.Write(doc.Editor, $"[OnIdleRefresh] 失败: {ex.Message}");
            }
        }
    }
}
