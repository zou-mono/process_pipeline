using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WpfSplitTest
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private bool _isTreeCollapsed;

        public bool IsTreeCollapsed
        {
            get => _isTreeCollapsed;
            set => Set(ref _isTreeCollapsed, value);
        }

        public ObservableCollection<TreeNodeVm> TreeNodes { get; } = new();
        public ObservableCollection<RowVm> Rows { get; } = new();

        public MainViewModel()
        {
            TreeNodes.Add(new TreeNodeVm("全部图层")
            {
                Children =
                {
                    new TreeNodeVm("给水"),
                    new TreeNodeVm("排水"),
                    new TreeNodeVm("燃气")
                }
            });

            Rows.Add(new RowVm("PL001", "给水", "待核查", 300, 1.2, "-"));
            Rows.Add(new RowVm("PL002", "排水", "已修改", 400, 0.95, "管径已更新"));
            Rows.Add(new RowVm("PL003", "燃气", "待复核", 250, 1.1, "高程疑似异常"));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }

    public class TreeNodeVm
    {
        public string Name { get; set; }
        public ObservableCollection<TreeNodeVm> Children { get; set; } = new();
        public TreeNodeVm(string name) => Name = name;
    }

    public class RowVm
    {
        public string Id { get; }
        public string Type { get; }
        public string Status { get; }
        public double Diameter { get; }
        public double Height { get; }
        public string Remark { get; }

        public RowVm(string id, string type, string status, double diameter, double height, string remark)
        {
            Id = id; Type = type; Status = status; Diameter = diameter; Height = height; Remark = remark;
        }
    }
}
