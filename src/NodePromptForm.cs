// Pi Web Tray — Node.js setup prompt
//
// A modal warning shown before the tray exits when Node.js is unavailable.
// It uses the same custom-window styling as the other Pi Web Tray dialogs
// instead of a native MessageBox.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class NodePromptForm : Form
    {
        private Icon _formIcon;
        private readonly TableLayoutPanel _root;
        private readonly FlowLayoutPanel _content;
        private readonly FlowLayoutPanel _buttons;
        private Control _initialFocus;

        internal NodePromptForm(string title, string body, bool canDownload)
            : this(title, body, canDownload, true, L.T("node.exit"))
        {
        }

        internal NodePromptForm(string title, string body, bool canDownload,
            bool canContinue, string closeText)
        {
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
            ClientSize = new Size(560, 300);
            try
            {
                Icon cached = Icons.Create(Icons.SetupColor);
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
            heading.Margin = new Padding(0, 0, 0, 16);

            Label message = CreateLabel(body);
            message.MaximumSize = new Size(500, 0);
            message.Margin = new Padding(0, 0, 0, 14);

            content.Controls.Add(heading);
            content.Controls.Add(message);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            // Keep the button group right-aligned like the other dialogs. With
            // RightToLeft flow the controls are added from right to left:
            // Exit, Continue, Open download page; visually they read the reverse.
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;

            Button exit = CreateButton(closeText);
            exit.DialogResult = DialogResult.No;
            _buttons = buttons;

            Button keep = null;
            if (canContinue)
            {
                keep = CreateButton(L.T("node.continue"));
                keep.DialogResult = DialogResult.Ignore;
            }

            Button download = null;
            if (canDownload)
            {
                download = CreateButton(L.T("node.download"));
                download.DialogResult = DialogResult.Yes;
                AcceptButton = download;
                _initialFocus = download;
            }
            else
            {
                AcceptButton = exit;
                _initialFocus = exit;
            }
            buttons.Controls.Add(exit);
            if (keep != null) buttons.Controls.Add(keep);
            if (download != null) buttons.Controls.Add(download);
            CancelButton = exit;

            root.Controls.Add(content, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_initialFocus != null) _initialFocus.Select();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                // Fit before the first paint, using the same content-based sizing as
                // About and the other custom dialogs.
                int contentHeight = _content.Padding.Vertical;
                foreach (Control child in _content.Controls)
                    contentHeight = Math.Max(contentHeight, child.Bottom + child.Margin.Bottom);
                int desired = contentHeight + _content.Margin.Vertical
                    + _root.Padding.Vertical + _buttons.PreferredSize.Height
                    + _buttons.Margin.Vertical;
                int chrome = Height - ClientSize.Height;
                int maxClient = Math.Max(200, Screen.FromControl(this).WorkingArea.Height - chrome - 20);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing && _formIcon != null)
            {
                try { _formIcon.Dispose(); } catch (Exception) { }
                _formIcon = null;
            }
            base.Dispose(disposing);
        }

        private static Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = Padding.Empty;
            label.UseCompatibleTextRendering = false;
            return label;
        }

        private static Button CreateButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.MinimumSize = new Size(88, 0);
            return button;
        }
    }
}
