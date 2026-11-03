// Pi Web Tray — live log viewer
//
// Opens service/install/update logs without taking a snapshot, so the view keeps
// following a running service. Only the last part of a large file is read.

using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class LogForm : Form
    {
        private const int TailBytes = 128 * 1024;

        private readonly string _path;
        private readonly TextBox _text;
        private readonly Timer _timer;
        private long _lastLength = -1;
        private DateTime _lastWriteUtc = DateTime.MinValue;

        internal LogForm(string path, string title)
        {
            _path = path;

            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(520, 320);
            Icon = SystemIcons.Application;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(10);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _text = new TextBox();
            _text.Dock = DockStyle.Fill;
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.BackColor = SystemColors.Window;
            _text.Font = new Font("Consolas", 9f);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;

            Button close = new Button();
            close.Text = L.T("log.close");
            close.AutoSize = true;
            close.MinimumSize = new Size(88, 0);
            close.Click += delegate { Close(); };

            Button refresh = new Button();
            refresh.Text = L.T("log.refresh");
            refresh.AutoSize = true;
            refresh.MinimumSize = new Size(88, 0);
            refresh.Click += delegate { RefreshLog(true); };

            buttons.Controls.Add(close);
            buttons.Controls.Add(refresh);

            root.Controls.Add(_text, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            AcceptButton = close;
            CancelButton = close;

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { RefreshLog(false); };
            _timer.Start();

            RefreshLog(true);
        }

        private void RefreshLog(bool force)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (force) _text.Text = "";
                    return;
                }

                FileInfo info = new FileInfo(_path);
                if (!force && info.Length == _lastLength && info.LastWriteTimeUtc == _lastWriteUtc)
                    return;
                _lastLength = info.Length;
                _lastWriteUtc = info.LastWriteTimeUtc;

                bool atBottom = _text.SelectionStart >= Math.Max(0, _text.TextLength - 2);
                int oldSelection = _text.SelectionStart;
                int oldLength = _text.SelectionLength;
                string text;
                using (FileStream stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - TailBytes);
                    stream.Seek(start, SeekOrigin.Begin);
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        text = reader.ReadToEnd();
                    }
                    if (start > 0) text = L.T("log.truncated") + Environment.NewLine + text;
                }

                _text.Text = text;
                if (atBottom)
                {
                    _text.SelectionStart = _text.TextLength;
                    _text.ScrollToCaret();
                }
                else
                {
                    _text.SelectionStart = Math.Min(oldSelection, _text.TextLength);
                    _text.SelectionLength = Math.Min(oldLength, _text.TextLength - _text.SelectionStart);
                }
            }
            catch (Exception ex)
            {
                if (force) _text.Text = ex.Message;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _timer.Stop(); _timer.Dispose(); } catch (Exception) { }
            }
            base.Dispose(disposing);
        }
    }
}
