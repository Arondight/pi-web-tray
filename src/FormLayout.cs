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

        // Fit a content-driven dialog and lower a constructor minimum that is
        // taller than the content. Keeping that minimum would force an empty
        // filler row open. MinimumSize is only ever lowered here: a long
        // localized message must remain scrollable by shrinking the window
        // instead of being blocked by a minimum raised to the content height.
        internal static void FitContentHeight(Form form, int desiredClientHeight)
        {
            if (form == null || form.IsDisposed) return;
            try
            {
                int chrome = Math.Max(0, form.Height - form.ClientSize.Height);
                int maxClient = Math.Max(160,
                    Screen.FromControl(form).WorkingArea.Height - chrome - 20);
                int height = Math.Max(1, Math.Min(desiredClientHeight, maxClient));

                Size minimum = form.MinimumSize;
                bool hasMinimumHeight = minimum.Height > 0;
                if (hasMinimumHeight) form.MinimumSize = new Size(minimum.Width, 1);
                try
                {
                    if (height != form.ClientSize.Height)
                    {
                        form.ClientSize = new Size(form.ClientSize.Width, height);
                        form.PerformLayout();
                    }
                }
                finally
                {
                    if (form.Height > 0)
                    {
                        int fittedHeight = form.Height;
                        int minimumHeight = hasMinimumHeight
                            ? Math.Min(minimum.Height, fittedHeight)
                            : 0;
                        form.MinimumSize = new Size(minimum.Width, minimumHeight);
                    }
                }
            }
            catch (Exception)
            {
                // Layout must never prevent a dialog from opening.
            }
            // Keep the work-area cap even when measuring or resizing failed.
            FitToWorkingArea(form);
        }
    }
}
