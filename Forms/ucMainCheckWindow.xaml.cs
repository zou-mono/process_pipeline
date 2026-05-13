using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace process_pipeline.Forms
{
    /// <summary>
    /// Interaction logic for ucMainCheckWindow.xaml
    /// </summary>
    public partial class ucMainCheckWindow : INotifyPropertyChanged
    {
        private bool _isTreeCollapsed;
        private string _statusText = "就绪";
        private string _keyword = string.Empty;
        private string _collapseGlyph = "◀";
        private GridLengthCache _cache = new GridLengthCache();

        public ObservableCollection<TreeNodeVm> TreeNodes { get; } = new ObservableCollection<TreeNodeVm>();
        public ObservableCollection<PipeRowVm> Rows { get; } = new ObservableCollection<PipeRowVm>();

        public ICommand ToggleTreeCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ZoomToCommand { get; }
        public ICommand ExportCommand { get; }

        public ucMainCheckWindow()
        {
            ToggleTreeCommand = new RelayCommand(_ => ToggleTree());
            RefreshCommand = new RelayCommand(_ => StatusText = "已刷新");
            ZoomToCommand = new RelayCommand(_ => StatusText = "已定位");
            ExportCommand = new RelayCommand(_ => StatusText = "已导出");

            // mock data
            TreeNodes.Add(new TreeNodeVm("全部图层")
            {
                Children = {
                    new TreeNodeVm("给水"),
                    new TreeNodeVm("排水"),
                    new TreeNodeVm("燃气")
                }
            });

            Rows.Add(new PipeRowVm("PL001","给水","待核查",300,1.20,"-"));
            Rows.Add(new PipeRowVm("PL002","排水","已修改",400,0.95,"管径已更新"));
            Rows.Add(new PipeRowVm("PL003","燃气","待复核",250,1.10,"高程疑似异常"));
        }

        public bool IsTreeCollapsed
        {
            get => _isTreeCollapsed;
            set
            {
                if (Set(ref _isTreeCollapsed, value))
                {
                    CollapseGlyph = value ? "▶" : "◀";
                    StatusText = value ? "目录已折叠" : "目录已展开";
                    // 这里与 View 层配合设置列宽（建议用行为Behavior实现）
                }
            }
        }

        public string CollapseGlyph
        {
            get => _collapseGlyph;
            set => Set(ref _collapseGlyph, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public string Keyword
        {
            get => _keyword;
            set => Set(ref _keyword, value);
        }

        public string RowCountText => $"记录数：{Rows.Count}";

        private void ToggleTree()
        {
            IsTreeCollapsed = !IsTreeCollapsed;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(Rows))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowCountText)));
            return true;
        }
    }

    public sealed class GridLengthCache
    {
        public double LastLeftWidth { get; set; } = 320;
    }

    public class TreeNodeVm
    {
        public string Name { get; set; }
        public ObservableCollection<TreeNodeVm> Children { get; set; } = new ObservableCollection<TreeNodeVm>();
        public TreeNodeVm(string name) => Name = name;
    }

    public class PipeRowVm
    {
        public string Id { get; }
        public string Type { get; }
        public string Status { get; }
        public double Diameter { get; }
        public double Height { get; }
        public string Remark { get; }

        public PipeRowVm(string id, string type, string status, double diameter, double height, string remark)
        {
            Id = id;
            Type = type;
            Status = status;
            Diameter = diameter;
            Height = height;
            Remark = remark;
        }
    }
}
