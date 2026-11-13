// Pi Web Tray — Node.js setup prompt
//
// A modal warning shown when Node.js is unavailable. The tray may keep running
// and detect a later installation instead of exiting.
// It uses the same custom-window styling as the other Pi Web Tray dialogs
// instead of a native MessageBox.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class NodePromptForm : Form
    {
        private Icon _formIcon;
        private readonly TableLayoutPanel _root;
        private readonly FlowLayoutPanel _content;
        private readonly TableLayoutPanel _buttons;
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

            TableLayoutPanel buttons = new TableLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.RowCount = 1;
            buttons.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _buttons = buttons;

            Button exit = CreateButton(closeText);
            exit.DialogResult = DialogResult.No;

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
                // Do not make an unsigned community download the default action.
                // Enter should keep the tray running (or exit when there is no
                // "keep running" option), not open a binary download page.
                AcceptButton = keep != null ? keep : exit;
                _initialFocus = keep != null ? keep : exit;
            }
            else
            {
                // A continue button is still the safe default even when download is
                // not offered. Enter must not exit the tray in that case.
                AcceptButton = keep != null ? keep : exit;
                _initialFocus = keep != null ? keep : exit;
            }

            // A RightToLeft FlowLayoutPanel clipped the leftmost button when
            // Windows 7 used 200% DPI. A fixed-column table keeps every button
            // visible and avoids the oversized AutoSize widths at high DPI.
            List<Button> order = new List<Button>();
            if (download != null) order.Add(download);
            if (keep != null) order.Add(keep);
            order.Add(exit);

            buttons.ColumnCount = order.Count;
            for (int i = 0; i < order.Count; i++)
            {
                float percent;
                if (order.Count == 3) percent = i == 0 ? 32f : (i == 1 ? 45f : 23f);
                else if (order.Count == 2) percent = i == 0 ? 60f : 40f;
                else percent = 100f;
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, percent));
            }
            for (int i = 0; i < order.Count; i++)
                buttons.Controls.Add(order[i], i, 0);
            // Esc should dismiss the prompt without exiting the tray. When a
            // "continue" action exists, make it the cancel button; the explicit
            // Exit button remains the only way to shut the tray down.
            CancelButton = keep != null ? keep : exit;

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
            // Error text can contain paths with '&'; do not treat it as a mnemonic.
            label.UseMnemonic = false;
            return label;
        }

        private static Button CreateButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            // Fixed columns and Dock=Fill do the sizing. AutoSize made button
            // widths grow inconsistently at 200% DPI on Windows 7.
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.MinimumSize = new Size(88, 0);
            return button;
        }
    }
}
