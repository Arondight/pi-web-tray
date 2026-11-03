// Pi Web Tray — update progress and cancel
//
// Checking and downloading can be cancelled. Applying the update (the elevated helper)
// cannot be cancelled safely, so the button is disabled in that stage.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class UpdateForm : Form
    {
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Button _cancel;
        private readonly Action _onCancel;
        private bool _allowClose;

        internal bool Cancelled { get; private set; }

        internal UpdateForm(Action onCancel)
        {
            _onCancel = onCancel;

            Text = L.T("update.progressTitle");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(560, 190);
            Icon = Icons.WindowIcon();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _status = new Label();
            _status.AutoSize = true;
            _status.Margin = new Padding(0, 0, 0, 10);

            _progress = new ProgressBar();
            _progress.Dock = DockStyle.Fill;
            _progress.Height = 18;
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 30;
            _progress.Margin = new Padding(0, 0, 0, 10);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;

            _cancel = new Button();
            _cancel.Text = L.T("update.cancel");
            _cancel.AutoSize = true;
            _cancel.MinimumSize = new Size(88, 0);
            _cancel.Click += delegate { CancelUpdate(); };

            buttons.Controls.Add(_cancel);

            root.Controls.Add(_status, 0, 0);
            root.Controls.Add(_progress, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);

            CancelButton = _cancel;
            SetState(L.T("update.status.checking"), -1, true);
        }

        internal void SetState(string status, int percent, bool cancellable)
        {
            if (IsDisposed) return;
            _status.Text = status;

            if (percent >= 0)
            {
                _progress.Style = ProgressBarStyle.Continuous;
                int value = Math.Max(0, Math.Min(100, percent));
                if (value < _progress.Minimum) value = _progress.Minimum;
                _progress.Value = value;
            }
            else
            {
                _progress.Style = ProgressBarStyle.Marquee;
                _progress.MarqueeAnimationSpeed = 30;
            }

            bool canCancel = cancellable && !Cancelled;
            _cancel.Enabled = canCancel;
            _cancel.Text = Cancelled ? L.T("update.cancelling") : L.T("update.cancel");
        }

        internal void CloseFromOwner()
        {
            _allowClose = true;
            Close();
        }

        private void CancelUpdate()
        {
            if (IsDisposed) return;
            if (Cancelled || !_cancel.Enabled) return;
            Cancelled = true;
            _cancel.Enabled = false;
            _cancel.Text = L.T("update.cancelling");
            if (_onCancel != null)
            {
                try { _onCancel(); } catch (Exception) { }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason != CloseReason.WindowsShutDown)
            {
                // Use the Cancel button so the tray can stop the worker and ignore a
                // late result; closing the window directly would race the worker.
                e.Cancel = true;
                // Cancel through the message loop: CancelUpdate closes this form via the
                // tray, and re-entering Close() from inside FormClosing is not safe.
                if (_cancel.Enabled)
                {
                    try { BeginInvoke(new Action(CancelUpdate)); } catch (Exception) { }
                }
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
