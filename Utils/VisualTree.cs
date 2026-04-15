using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;

namespace process_pipeline.Utils
{
    public class VisualTree
    {
        /// <summary>
        /// 从点击事件的源向上查找指定类型的视觉树父级
        /// </summary>
        /// <typeparam name="T">要查找的控件类型 (如 DataGridRowHeader, DataGridCell)</typeparam>
        /// <param name="originalSource">事件参数中的 e.OriginalSource</param>
        /// <returns>找到的控件实例，若未找到则返回 null</returns>
        public static T GetClickedElement<T>(object originalSource) where T : DependencyObject
        {
            DependencyObject dep = originalSource as DependencyObject;
            while (dep != null && !(dep is T))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }
            return dep as T;
        }

        /// <summary>
        /// 一次性获取点击位置关联的行头和行对象
        /// </summary>
        public static (DataGridRowHeader Header, DataGridRow Row) GetRowContext(object originalSource)
        {
            DependencyObject dep = originalSource as DependencyObject;
            DataGridRowHeader header = null;

            while (dep != null)
            {
                // 如果还没找到 Header，看看当前层级是不是 Header
                if (header == null && dep is DataGridRowHeader h)
                {
                    header = h;
                }

                // 继续向上，如果碰到了 Row，说明 Header 和 Row 的关系已经明确，直接返回
                if (dep is DataGridRow row)
                {
                    return (header, row);
                }

                // 向上爬一层
                dep = VisualTreeHelper.GetParent(dep);
            }

            return (null, null);
        }

        /// <summary>
        /// 通用的向上查找父级方法
        /// </summary>
        public static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }
    }
}
