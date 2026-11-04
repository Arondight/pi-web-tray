// Pi Web Tray — About window
//
// A small static dialog with the application name, version, author and project link.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class AboutForm : Form
    {
        private Icon _formIcon;

        internal AboutForm()
        {
            SuspendLayout();
            Text = L.T("about.title");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            // Scaling runs on ResumeLayout, after the whole control tree exists.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 320);
            try
            {
                Icon cached = Icons.Create(Icons.RunningColor);
                _formIcon = (Icon)cached.Clone();
                Icon = _formIcon;
            }
            catch (Exception)
            {
                Icon = SystemIcons.Application;
            }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel content = new FlowLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.FlowDirection = FlowDirection.TopDown;
            content.WrapContents = false;
            content.AutoScroll = true;

            Label title = new Label();
            title.Text = Program.AppName;
            title.Font = new Font(Font.FontFamily, 16f, FontStyle.Bold);
            title.AutoSize = true;
            title.Margin = new Padding(0, 0, 0, 4);

            Label version = new Label();
            version.Text = L.T("about.version", Program.Version);
            version.AutoSize = true;
            version.Margin = new Padding(0, 0, 0, 14);

            Label description = new Label();
            description.Text = L.T("about.description");
            description.AutoSize = true;
            description.MaximumSize = new Size(440, 0);
            description.Margin = new Padding(0, 0, 0, 14);

            Label author = new Label();
            author.Text = L.T("about.author", L.T("about.authorName"));
            author.AutoSize = true;
            author.Margin = new Padding(0, 0, 0, 4);

            LinkLabel link = new LinkLabel();
            string linkText = L.T("about.link", Program.ProjectUrl);
            link.Text = linkText;
            link.AutoSize = true;
            link.Margin = new Padding(0, 0, 0, 4);
            // Only the URL is the link; the "Project:" prefix keeps the normal text colour.
            int urlStart = linkText.IndexOf(Program.ProjectUrl, StringComparison.Ordinal);
            link.LinkArea = urlStart >= 0
                ? new LinkArea(urlStart, Program.ProjectUrl.Length)
                : new LinkArea(0, linkText.Length);
            link.LinkClicked += delegate { OpenProjectPage(); };

            Label license = new Label();
            license.Text = L.T("about.license");
            license.AutoSize = true;
            license.Margin = new Padding(0, 0, 0, 4);

            Label copyright = new Label();
            copyright.Text = L.T("about.copyright");
            copyright.AutoSize = true;
            copyright.Margin = new Padding(0, 0, 0, 0);

            content.Controls.Add(title);
            content.Controls.Add(version);
            content.Controls.Add(description);
            content.Controls.Add(author);
            content.Controls.Add(link);
            content.Controls.Add(license);
            content.Controls.Add(copyright);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;

            Button close = new Button();
            close.Text = L.T("about.close");
            close.AutoSize = true;
            close.MinimumSize = new Size(88, 0);
            close.Click += delegate { Close(); };

            buttons.Controls.Add(close);

            root.Controls.Add(content, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            AcceptButton = close;
            CancelButton = close;
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

        private static void OpenProjectPage()
        {
            try
            {
                using (Process browser = Process.Start(Program.ProjectUrl))
                {
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
