// Pi Web Tray — menu checkbox images
//
// ToolStripMenuItem does not draw an empty checkbox for an unchecked item;
// its check margin is otherwise blank. Build native-looking checked and
// unchecked images so all three stateful menu items always show a check box.

using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace PiWebTray
{
    internal static class TrayMenuCheckBoxImages
    {
        internal static Size ImageSize()
        {
            int size = Math.Max(13, SystemInformation.MenuCheckSize.Width);
            return new Size(size + 4, size + 4);
        }

        internal static Image Create(bool selected)
        {
            Size imageSize = ImageSize();
            Bitmap bitmap = new Bitmap(imageSize.Width, imageSize.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                bool rendered = false;
                if (Application.RenderWithVisualStyles)
                {
                    try
                    {
                        CheckBoxState state = selected
                            ? CheckBoxState.CheckedNormal
                            : CheckBoxState.UncheckedNormal;
                        CheckBoxRenderer.DrawCheckBox(graphics, new Point(2, 2), state);
                        rendered = true;
                    }
                    catch (Exception)
                    {
                    }
                }
                if (!rendered)
                {
                    ButtonState state = selected ? ButtonState.Checked : ButtonState.Normal;
                    int size = Math.Max(13, SystemInformation.MenuCheckSize.Width);
                    ControlPaint.DrawCheckBox(graphics, new Rectangle(2, 2, size, size), state);
                }
            }
            return bitmap;
        }
    }
}
