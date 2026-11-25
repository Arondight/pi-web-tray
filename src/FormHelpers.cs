// Pi Web Tray — shared dialog control factories
//
// The custom dialogs (about, node prompt, administrator-autostart warning) build
// the same GDI-rendered, mnemonic-free labels. Keep that single definition here so
// the dialogs stay visually consistent. Button factories are deliberately NOT
// shared: the flow-layout dialogs auto-size buttons while the fixed-column node
// prompt docks them, so their sizing differs.

using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class FormHelpers
    {
        // A label using the same GDI renderer as the rest of the dialog. Paths and
        // error text can contain '&', so mnemonics are disabled.
        internal static Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = Padding.Empty;
            label.UseCompatibleTextRendering = false;
            label.UseMnemonic = false;
            return label;
        }
    }
}
