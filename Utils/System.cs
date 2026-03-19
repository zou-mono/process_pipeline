using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Core;
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
            catch (Exception ex)
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
}
