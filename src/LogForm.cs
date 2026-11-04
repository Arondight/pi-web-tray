// Pi Web Tray — live log viewer
//
// Opens service/install/update logs without taking a snapshot, so the view keeps
// following a running service. Only the last part of a large file is read. The view
// can be paused and filtered without losing the scroll position.

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
        private readonly TextBox _filter;
        private readonly Button _pause;
        private readonly Label _state;
        private readonly Timer _timer;
        private long _lastLength = -1;
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private bool _paused;

        internal LogForm(string path, string title)
        {
            SuspendLayout();
            _path = path;

            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            // Match the environment check window: a taskbar button implies a working minimize.
            MinimizeBox = true;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            // Scaling runs on ResumeLayout, after the whole control tree exists.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(560, 360);
            Icon = Icons.WindowIcon();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(10);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel toolbar = new FlowLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.AutoSize = true;
            toolbar.WrapContents = true;
            toolbar.Margin = new Padding(0, 0, 0, 6);

            Label filterLabel = new Label();
            filterLabel.Text = L.T("log.filter");
            filterLabel.AutoSize = true;
            filterLabel.Margin = new Padding(0, 7, 6, 0);

            _filter = new TextBox();
            _filter.Width = 240;
            _filter.Margin = new Padding(0, 3, 10, 0);
            _filter.TextChanged += delegate { RefreshLog(true); };

            _pause = new Button();
            _pause.Text = L.T("log.pause");
            _pause.AutoSize = true;
            _pause.MinimumSize = new Size(88, 0);
            _pause.Margin = new Padding(0, 2, 10, 0);
            _pause.Click += delegate { TogglePause(); };

            _state = new Label();
            _state.AutoSize = true;
            _state.Margin = new Padding(0, 7, 0, 0);
            _state.Text = L.T("log.live");

            toolbar.Controls.Add(filterLabel);
            toolbar.Controls.Add(_filter);
            toolbar.Controls.Add(_pause);
            toolbar.Controls.Add(_state);

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

            root.Controls.Add(toolbar, 0, 0);
            root.Controls.Add(_text, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            AcceptButton = close;
            CancelButton = close;

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { RefreshLog(false); };
            _timer.Start();

            RefreshLog(true);
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _pause.Text = _paused ? L.T("log.resume") : L.T("log.pause");
            UpdateState();
            if (!_paused) RefreshLog(true);
        }

        private void UpdateState()
        {
            string state = _paused ? L.T("log.paused") : L.T("log.live");
            if ((_filter.Text ?? "").Trim().Length > 0) state += " · " + L.T("log.filtered");
            _state.Text = state;
        }

        private void RefreshLog(bool force)
        {
            if (_paused && !force) return;

            try
            {
                if (!File.Exists(_path))
                {
                    _lastLength = -1;
                    _lastWriteUtc = DateTime.MinValue;
                    if (force)
                    {
                        _text.Text = "";
                        UpdateState();
                    }
                    return;
                }

                FileInfo info = new FileInfo(_path);
                if (!force && info.Length == _lastLength && info.LastWriteTimeUtc == _lastWriteUtc)
                    return;
                long newLength = info.Length;
                DateTime newWriteUtc = info.LastWriteTimeUtc;

                bool atBottom = _text.SelectionStart >= Math.Max(0, _text.TextLength - 2);
                int firstLine = 0;
                if (!atBottom && _text.TextLength > 0)
                {
                    int firstChar = _text.GetCharIndexFromPosition(new Point(1, 1));
                    firstLine = Math.Max(0, _text.GetLineFromCharIndex(firstChar));
                }

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

                string filter = (_filter.Text ?? "").Trim();
                if (filter.Length > 0) text = FilterLines(text, filter);

                _text.Text = text;
                if (atBottom)
                {
                    _text.SelectionStart = _text.TextLength;
                    _text.ScrollToCaret();
                }
                else
                {
                    int lineCount = _text.GetLineFromCharIndex(_text.TextLength);
                    if (firstLine > lineCount) firstLine = lineCount;
                    int index = _text.GetFirstCharIndexFromLine(firstLine);
                    if (index < 0) index = Math.Min(_text.TextLength, firstLine);
                    _text.SelectionStart = Math.Min(index, _text.TextLength);
                    _text.SelectionLength = 0;
                    _text.ScrollToCaret();
                }
                UpdateState();
                _lastLength = newLength;
                _lastWriteUtc = newWriteUtc;
            }
            catch (Exception ex)
            {
                if (force) _text.Text = ex.Message;
            }
        }

        internal static string FilterLines(string text, string filter)
        {
            StringBuilder sb = new StringBuilder();
            int matches = 0;
            string[] lines = text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                if (line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (matches > 0) sb.AppendLine();
                sb.Append(line);
                matches++;
            }

            if (matches == 0) return L.T("log.noMatches", filter);
            return sb.ToString();
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
