// Pi Web Tray — administrator autostart warning
//
// Shows the security warning for a user-writable installation and offers a
// one-click ACL repair. The repair runs through UAC and is verified with the
// same check that produced the warning.

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class AdminAutostartWarningForm : Form
    {
        private readonly TableLayoutPanel _root;
        private readonly FlowLayoutPanel _content;
        private readonly FlowLayoutPanel _buttons;
        private readonly Label _status;
        private readonly Button _fix;
        private readonly Button _close;
        private readonly string _repairExecutablePath;
        private Icon _formIcon;
        private Font _headingFont;
        private bool _busy;

        internal bool RepairSucceeded { get; private set; }

        internal AdminAutostartWarningForm(string title, string body)
            : this(title, body, null)
        {
        }

        internal AdminAutostartWarningForm(string title, string body,
            string repairExecutablePath)
        {
            _repairExecutablePath = string.IsNullOrEmpty(repairExecutablePath)
                ? Application.ExecutablePath : repairExecutablePath;
            SuspendLayout();
            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(620, 340);
            MinimumSize = new Size(460, 280);
            try
            {
                Icon cached = Icons.Create(Icons.ErrorColor);
                _formIcon = (Icon)cached.Clone();
                Icon = _formIcon;
            }
            catch (Exception)
            {
                Icon = SystemIcons.Warning;
            }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root = root;

            FlowLayoutPanel content = new FlowLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.FlowDirection = FlowDirection.TopDown;
            content.WrapContents = false;
            content.AutoScroll = true;
            _content = content;

            Label heading = CreateLabel(title);
            heading.Font = new Font(Font.FontFamily, 16f, FontStyle.Bold);
            _headingFont = heading.Font;
            heading.Margin = new Padding(0, 0, 0, 16);

            Label message = CreateLabel(body);
            message.MaximumSize = new Size(560, 0);
            message.Margin = new Padding(0, 0, 0, 14);

            _status = CreateLabel("");
            _status.MaximumSize = new Size(560, 0);
            _status.Visible = false;
            _status.Margin = new Padding(0, 0, 0, 14);

            content.Controls.Add(heading);
            content.Controls.Add(message);
            content.Controls.Add(_status);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            _buttons = buttons;

            _fix = CreateButton(L.T("auto.repair"));
            _fix.Click += delegate { StartRepair(); };

            _close = CreateButton(L.T("check.close"));
            _close.DialogResult = DialogResult.Cancel;

            // FlowDirection is RightToLeft: the first control is rightmost.
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_fix);

            root.Controls.Add(content, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            AcceptButton = _close;
            CancelButton = _close;

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToContent();
            FormLayout.FitToWorkingArea(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // This dialog is normally opened from a balloon notification. The
            // shell owns the foreground at that point, so ShowDialog alone can
            // leave the window behind the current application.
            Sys.BringToForeground(Handle);
            try
            {
                // One more attempt after the current message has completed; on
                // some Windows versions the shell restores its foreground window
                // immediately after the notification click handler returns.
                BeginInvoke(new Action(delegate ()
                {
                    if (!IsDisposed) Sys.BringToForeground(Handle);
                }));
            }
            catch (Exception)
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Do not let Esc, Alt+F4, or the title-bar close button dispose the form
            // while the elevated command is still running.
            if (_busy)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _formIcon != null)
            {
                try { _formIcon.Dispose(); } catch (Exception) { }
                _formIcon = null;
            }
            if (disposing && _headingFont != null)
            {
                try { _headingFont.Dispose(); } catch (Exception) { }
                _headingFont = null;
            }
            base.Dispose(disposing);
        }

        private void StartRepair()
        {
            if (_busy) return;
            _busy = true;
            _fix.Enabled = false;
            _close.Enabled = false;
            ControlBox = false;
            CancelButton = null;
            UseWaitCursor = true;
            SetStatus(L.T("auto.repairRunning"), SystemColors.ControlText);
            FitToContent();

            Thread worker = new Thread(delegate ()
            {
                string message;
                bool repaired;
                try
                {
                    repaired = Autostart.SecureInstallDirectory(
                        _repairExecutablePath, out message);
                }
                catch (Exception ex)
                {
                    repaired = false;
                    message = L.T("auto.repairFailed", ex.Message);
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate ()
                        {
                            if (IsDisposed) return;
                            FinishRepair(repaired, message);
                        }));
                    }
                }
                catch (Exception)
                {
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void FinishRepair(bool repaired, string message)
        {
            UseWaitCursor = false;
            _busy = false;
            _close.Enabled = true;
            ControlBox = true;
            CancelButton = _close;

            if (repaired)
            {
                RepairSucceeded = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _fix.Enabled = true;
            SetStatus(message, Color.FromArgb(0xD1, 0x3A, 0x3A));
            FitToContent();
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text ?? "";
            _status.ForeColor = color;
            _status.Visible = !string.IsNullOrEmpty(_status.Text);
        }

        private void FitToContent()
        {
            try
            {
                PerformLayout();
                int contentHeight = _content.Padding.Vertical;
                foreach (Control child in _content.Controls)
                {
                    if (!child.Visible) continue;
                    contentHeight = Math.Max(contentHeight, child.Bottom + child.Margin.Bottom);
                }
                int desired = contentHeight + _content.Margin.Vertical
                    + _root.Padding.Vertical + _buttons.PreferredSize.Height
                    + _buttons.Margin.Vertical;
                int chrome = Height - ClientSize.Height;
                int maxClient = Math.Max(220, Screen.FromControl(this).WorkingArea.Height - chrome - 20);
                int height = Math.Max(1, Math.Min(desired, maxClient));
                if (height != ClientSize.Height)
                {
                    ClientSize = new Size(ClientSize.Width, height);
                    PerformLayout();
                }
                FormLayout.FitToWorkingArea(this);
            }
            catch (Exception)
            {
            }
        }

        private static Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = Padding.Empty;
            label.UseCompatibleTextRendering = false;
            // The message includes an installation path that may contain '&'.
            label.UseMnemonic = false;
            return label;
        }

        private static Button CreateButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            // Match the other tray dialogs: auto-sized buttons aligned to the
            // bottom-right, not wide buttons that stretch across the window.
            button.AutoSize = true;
            button.MinimumSize = new Size(88, 0);
            return button;
        }
    }
}
