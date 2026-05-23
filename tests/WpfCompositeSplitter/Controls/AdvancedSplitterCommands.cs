using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WpfCompositeSplitter.Controls
{
    public static class AdvancedSplitterCommands
    {
        public static readonly RoutedUICommand TogglePrimaryOnly =
            new RoutedUICommand("Toggle Primary Only", nameof(TogglePrimaryOnly), typeof(AdvancedSplitterCommands));

        public static readonly RoutedUICommand ToggleSecondaryOnly =
            new RoutedUICommand("Toggle Secondary Only", nameof(ToggleSecondaryOnly), typeof(AdvancedSplitterCommands));

        public static readonly RoutedUICommand RestoreNormal =
            new RoutedUICommand("Restore Normal", nameof(RestoreNormal), typeof(AdvancedSplitterCommands));
    }
}
