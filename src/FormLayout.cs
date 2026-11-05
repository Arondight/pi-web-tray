// Pi Web Tray — shared dialog sizing
//
// WinForms forms are scaled from their 96 DPI design size. On high-DPI screens
// the scaled fixed sizes can exceed the usable work area, especially on 1080p
// displays at 200%. Keep dialogs inside the monitor without changing their
// normal 100%/125%/150% layout.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class FormLayout
    {
        internal static void FitToWorkingArea(Form form)
        {
            if (form == null || form.IsDisposed) return;
            try
            {
                Rectangle working = Screen.FromControl(form).WorkingArea;
                int maxOuterWidth = Math.Max(320, working.Width - 20);
                int maxOuterHeight = Math.Max(240, working.Height - 20);

                // MinimumSize is part of the window frame, not just the client
                // area. Cap it first so a later ClientSize assignment is not
                // rejected by the scaled minimum.
                Size minimum = form.MinimumSize;
                Size capped = new Size(
                    Math.Min(minimum.Width, maxOuterWidth),
                    Math.Min(minimum.Height, maxOuterHeight));
                if (capped != minimum) form.MinimumSize = capped;

                int chromeWidth = Math.Max(0, form.Width - form.ClientSize.Width);
                int chromeHeight = Math.Max(0, form.Height - form.ClientSize.Height);
                int maxClientWidth = Math.Max(200, working.Width - chromeWidth - 20);
                int maxClientHeight = Math.Max(160, working.Height - chromeHeight - 20);
                int width = Math.Min(form.ClientSize.Width, maxClientWidth);
                int height = Math.Min(form.ClientSize.Height, maxClientHeight);
                if (width != form.ClientSize.Width || height != form.ClientSize.Height)
                    form.ClientSize = new Size(width, height);
            }
            catch (Exception)
            {
                // Layout must never prevent a dialog from opening.
            }
        }
    }
}
