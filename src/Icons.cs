// Pi Web Tray — icons
//
// No binary icons in the repo; everything is drawn at runtime with GDI+:
// rounded square + π, color = state. The build uses --make-ico to emit a multi-size .ico for the exe.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class Icons
    {
        internal static readonly Color RunningColor = Color.FromArgb(0x2E, 0xA0, 0x43);  // green: running
        internal static readonly Color StoppedColor = Color.FromArgb(0x6B, 0x72, 0x80);  // grey: stopped
        internal static readonly Color BusyColor = Color.FromArgb(0xF0, 0xA0, 0x20);     // orange: installing / starting / stopping
        internal static readonly Color ErrorColor = Color.FromArgb(0xD1, 0x3A, 0x3A);    // red: error
        internal static readonly Color SetupColor = Color.FromArgb(0x2F, 0x6F, 0xED);    // blue: prerequisites missing

        // Icons are built once per colour and kept for the whole process: a fresh Icon per state change
        // would leak the stream and re-draw the bitmaps every time.
        private static readonly Dictionary<Color, Icon> Cache = new Dictionary<Color, Icon>();
        private static readonly object CacheLock = new object();

        // Window/taskbar icon. Use the icon embedded in the executable so taskbar
        // buttons match the application icon. The icon is shared and disposed with
        // the cache when the tray exits; WinForms does not take ownership of Form.Icon.
        private static Icon _windowIcon;

        internal static Icon WindowIcon()
        {
            lock (CacheLock)
            {
                if (_windowIcon == null)
                {
                    try { _windowIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch (Exception) { }
                    if (_windowIcon == null)
                    {
                        try { _windowIcon = (Icon)Create(RunningColor).Clone(); }
                        catch (Exception) { }
                    }
                    if (_windowIcon == null) _windowIcon = (Icon)SystemIcons.Application.Clone();
                }
                return _windowIcon;
            }
        }

        internal static Icon Create(Color background)
        {
            lock (CacheLock)
            {
                Icon cached;
                if (Cache.TryGetValue(background, out cached)) return cached;

                Icon icon = new Icon(new MemoryStream(BuildIco(background, new int[] { 16, 20, 24, 32, 40, 48 })));
                Cache[background] = icon;
                return icon;
            }
        }

        internal static void DisposeCache()
        {
            lock (CacheLock)
            {
                foreach (Icon icon in Cache.Values)
                {
                    try { icon.Dispose(); } catch (Exception) { }
                }
                Cache.Clear();
                if (_windowIcon != null)
                {
                    try { _windowIcon.Dispose(); } catch (Exception) { }
                    _windowIcon = null;
                }
            }
        }

        internal static byte[] BuildIco(Color background, int[] sizes)
        {
            List<byte[]> frames = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (Bitmap bitmap = Draw(background, size))
                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    frames.Add(stream.ToArray());
                }
            }

            using (MemoryStream ico = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ico))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)sizes.Length);

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int size = sizes[i];
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(frames[i].Length);
                    writer.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] frame in frames) writer.Write(frame);
                writer.Flush();
                return ico.ToArray();
            }
        }

        private static Bitmap Draw(Color background, int size)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = size <= 20 ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                float inset = size <= 20 ? 0.5f : 1.0f;
                RectangleF rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
                using (GraphicsPath path = RoundedRect(rect, size * 0.24f))
                using (SolidBrush brush = new SolidBrush(background))
                {
                    g.FillPath(brush, path);
                }

                using (Font font = new Font("Segoe UI", size * 0.74f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush white = new SolidBrush(Color.White))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString("π", font, white, new RectangleF(0, size * 0.01f, size, size), format);
                }
            }
            return bitmap;
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = radius * 2;
            if (diameter <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
