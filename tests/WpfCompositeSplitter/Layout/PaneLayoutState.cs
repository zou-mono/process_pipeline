using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WpfCompositeSplitter.Controls;

namespace WpfCompositeSplitter.Layout
{
    /// <summary>
    /// PaneLayoutState = “布局状态快照”
    /// --------------------------------------------------------
    /// 这个类不做 UI 操作，它只保存“当前布局参数”。
    ///
    /// 你可以把它理解成：
    /// - ViewModel 里的“状态仓库”
    /// - 或者布局引擎的“配置对象”
    ///
    /// 典型用途：
    /// 1) 启动时加载上次布局
    /// 2) 用户拖动 Splitter 后实时更新
    /// 3) 折叠/展开时记住并恢复宽度
    /// </summary>
    public class PaneLayoutState : INotifyPropertyChanged
    {
        private PaneDisplayState _displayState = PaneDisplayState.Normal;
        private GridLength _primaryLength = new GridLength(280, GridUnitType.Pixel);
        private double _lastExpandedLength = 280.0;

        private double _splitterWidth = 10.0;
        private double _primaryCollapsedLength = 28.0;
        private double _secondaryCollapsedLength = 28.0;
        private double _primaryMinExpandedLength = 0.0;
        private double _secondaryMinLength = 0.0;

        public PaneDisplayState DisplayState
        {
            get => _displayState;
            set => SetField(ref _displayState, value);
        }

        public GridLength PrimaryLength
        {
            get => _primaryLength;
            set => SetField(ref _primaryLength, value);
        }

        public double LastExpandedLength
        {
            get => _lastExpandedLength;
            set => SetField(ref _lastExpandedLength, value);
        }

        public double SplitterWidth
        {
            get => _splitterWidth;
            set => SetField(ref _splitterWidth, value);
        }

        public double PrimaryCollapsedLength
        {
            get => _primaryCollapsedLength;
            set => SetField(ref _primaryCollapsedLength, value);
        }

        public double SecondaryCollapsedLength
        {
            get => _secondaryCollapsedLength;
            set => SetField(ref _secondaryCollapsedLength, value);
        }

        public double PrimaryMinExpandedLength
        {
            get => _primaryMinExpandedLength;
            set => SetField(ref _primaryMinExpandedLength, value);
        }

        public double SecondaryMinLength
        {
            get => _secondaryMinLength;
            set => SetField(ref _secondaryMinLength, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            return true;
        }
    }
}
