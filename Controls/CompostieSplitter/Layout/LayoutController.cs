using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows;
using process_pipeline.Controls.CompostieSplitter.Layout;
using process_pipeline.Controls.CompostieSplitter;

namespace process_pipeline.Controls.CompositeSplitter.Layout
{
    public class LayoutController
    {
        public PaneLayoutState State { get; }

        public LayoutController(PaneLayoutState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            if (State.PrimaryLength.IsAbsolute && State.PrimaryLength.Value > 0)
                State.LastExpandedLength = State.PrimaryLength.Value;
        }

        public void TogglePrimaryOnly(double currentPrimaryActual)
        {
            State.DisplayState = State.DisplayState == PaneDisplayState.PrimaryOnly
                ? PaneDisplayState.Normal
                : PaneDisplayState.PrimaryOnly;

            if (State.DisplayState == PaneDisplayState.PrimaryOnly &&
                currentPrimaryActual > State.PrimaryCollapsedLength)
            {
                State.LastExpandedLength = Math.Max(currentPrimaryActual, State.PrimaryMinExpandedLength);
            }
        }

        public void ToggleSecondaryOnly()
        {
            State.DisplayState = State.DisplayState == PaneDisplayState.SecondaryOnly
                ? PaneDisplayState.Normal
                : PaneDisplayState.SecondaryOnly;
        }

        public void RestoreNormal()
        {
            State.DisplayState = PaneDisplayState.Normal;
        }

        public double ComputeResize(double currentPrimary, double delta, double total, Orientation orientation)
        {
            if (State.DisplayState != PaneDisplayState.Normal) return currentPrimary;

            double target = currentPrimary + delta;
            double clamped = ClampToBounds(target, total, useCollapsedMin: false);

            if (clamped >= State.PrimaryMinExpandedLength)
                State.LastExpandedLength = clamped;

            State.PrimaryLength = new GridLength(clamped, GridUnitType.Pixel);
            return clamped;
        }

        public double GetRestoreLength(double total)
        {
            double restore = State.LastExpandedLength;
            if (double.IsNaN(restore) || restore <= 0)
                restore = State.PrimaryLength.IsAbsolute ? State.PrimaryLength.Value : 280.0;

            restore = Math.Max(restore, State.PrimaryMinExpandedLength);
            restore = ClampToBounds(restore, total, useCollapsedMin: false);
            return restore;
        }

        public double ClampToBounds(double primaryLength, double total, bool useCollapsedMin)
        {
            if (double.IsNaN(total) || total <= 0) return Math.Max(0, primaryLength);

            double s = State.SplitterWidth;
            double max = Math.Max(0, total - s - State.SecondaryMinLength);

            double min = useCollapsedMin ? State.PrimaryCollapsedLength : State.PrimaryMinExpandedLength;
            min = Math.Min(min, max);

            return Math.Max(min, Math.Min(primaryLength, max));
        }
    }
}
