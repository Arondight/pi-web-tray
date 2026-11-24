// Pi Web Tray — shared window chrome
//
// Every tray window is a resizable taskbar window with the same caption
// buttons. Forms that must not be closed while an operation is running
// cancel FormClosing themselves; the chrome itself stays consistent.

using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class WindowChrome
    {
        internal static void Apply(Form form)
        {
            if (form == null) return;

            form.StartPosition = FormStartPosition.CenterScreen;
            form.FormBorderStyle = FormBorderStyle.Sizable;
            form.MaximizeBox = true;
            form.MinimizeBox = true;
            form.ControlBox = true;
            form.ShowInTaskbar = true;
            form.Font = SystemFonts.MessageBoxFont;
            form.AutoScaleDimensions = new SizeF(96F, 96F);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }
    }
}
