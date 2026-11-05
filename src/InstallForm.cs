// Pi Web Tray — dependency installation progress
//
// The first npm install can take 10-20 minutes. A small modeless window shows the
// current step, elapsed time and latest npm output, and offers a Cancel button. The
// actual install still runs on the background worker in TrayApp.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class InstallForm : Form
    {
        private readonly Timer _timer;
        private readonly Label _step;
        private readonly Label _elapsed;
        private readonly ProgressBar _progress;
        private readonly TextBox _output;
        private readonly Button _cancel;
        private readonly DateTime _started;
        private string _lastLine = "";
        private bool _allowClose;

        internal bool Cancelled { get; private set; }

        internal InstallForm()
        {
            SuspendLayout();
            Text = L.T("install.progressTitle");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            // Scaling runs on ResumeLayout, after the whole control tree exists.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 320);
            Icon = Icons.WindowIcon();
            _started = DateTime.Now;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _step = new Label();
            _step.AutoSize = true;
            _step.Font = new Font(Font, FontStyle.Bold);
            _step.Margin = new Padding(0, 0, 0, 6);

            _elapsed = new Label();
            _elapsed.AutoSize = true;
            _elapsed.Margin = new Padding(0, 0, 0, 8);

            _progress = new ProgressBar();
            _progress.Dock = DockStyle.Fill;
            _progress.Height = 18;
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;
            _progress.Margin = new Padding(0, 0, 0, 8);

            _output = new TextBox();
            _output.Dock = DockStyle.Fill;
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Vertical;
            _output.WordWrap = true;
            _output.BackColor = SystemColors.Window;
            _output.Margin = new Padding(0, 0, 0, 8);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;

            _cancel = new Button();
            _cancel.Text = L.T("install.cancel");
            _cancel.AutoSize = true;
            _cancel.MinimumSize = new Size(88, 0);
            _cancel.Click += delegate { CancelInstall(); };

            buttons.Controls.Add(_cancel);

            root.Controls.Add(_step, 0, 0);
            root.Controls.Add(_elapsed, 0, 1);
            root.Controls.Add(_progress, 0, 2);
            root.Controls.Add(_output, 0, 3);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            CancelButton = _cancel;
            _timer = new Timer();
            _timer.Interval = 500;
            _timer.Tick += delegate { RefreshProgress(); };
            _timer.Start();
            RefreshProgress();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Keep the fixed install dialog inside a high-DPI work area.
            FormLayout.FitToWorkingArea(this);
        }

        internal void CloseFromOwner()
        {
            _allowClose = true;
            Close();
        }

        private void RefreshProgress()
        {
            InstallProgress progress = Installer.Snapshot();
            TimeSpan elapsed = progress.Elapsed;
            TimeSpan live = DateTime.Now - _started;
            if (live > elapsed) elapsed = live;

            _step.Text = progress.StepCount > 0
                ? L.T("install.progressStep", progress.Step, progress.StepCount, progress.Title)
                : progress.Title;
            _elapsed.Text = L.T("install.progressElapsed", FormatElapsed(elapsed));

            string last = progress.LastLine ?? "";
            if (!string.Equals(last, _lastLine, StringComparison.Ordinal) && last.Length > 0)
            {
                _lastLine = last;
                _output.AppendText(last + Environment.NewLine);
                if (_output.TextLength > 12000)
                {
                    _output.Text = _output.Text.Substring(_output.TextLength - 10000);
                }
                _output.SelectionStart = _output.TextLength;
                _output.ScrollToCaret();
            }
        }

        private static string FormatElapsed(TimeSpan span)
        {
            if (span.TotalSeconds < 60) return L.T("time.seconds", (int)span.TotalSeconds);
            return L.T("time.minutes", (int)span.TotalMinutes, span.Seconds);
        }

        private void CancelInstall()
        {
            if (Cancelled) return;
            Cancelled = true;
            _cancel.Enabled = false;
            _cancel.Text = L.T("install.cancelling");
            Installer.Abort();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                CancelInstall();
                return;
            }
            base.OnFormClosing(e);
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
