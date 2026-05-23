using WpfCompositeSplitter.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace WpfCompositeSplitter.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private Orientation _orientation = Orientation.Horizontal;
        private PaneDisplayState _displayState = PaneDisplayState.Normal;

        public Orientation Orientation
        {
            get => _orientation;
            set { _orientation = value; OnPropertyChanged(); }
        }

        public PaneDisplayState DisplayState
        {
            get => _displayState;
            set { _displayState = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> LeftItems { get; } = new ObservableCollection<string>
        {
            "给水", "排水", "燃气", "消防", "暖通"
        };

        public ObservableCollection<RowVm> Rows { get; } = new ObservableCollection<RowVm>
        {
            new RowVm{Id="PL001", Type="给水", Status="待核查", Diameter=300, Height=1.20, Remark="-"},
            new RowVm{Id="PL002", Type="排水", Status="已修改", Diameter=400, Height=0.95, Remark="管径已更新"},
            new RowVm{Id="PL003", Type="燃气", Status="待复核", Diameter=250, Height=1.10, Remark="高程疑似异常"},
        };

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RowVm
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public double Diameter { get; set; }
        public double Height { get; set; }
        public string Remark { get; set; }
    }
}
