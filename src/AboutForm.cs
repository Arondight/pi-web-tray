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
        private const string PiWebUrl = "https://github.com/agegr/pi-web";
        private const string PiUrl = "https://github.com/earendil-works/pi";

        private Icon _formIcon;
        private readonly TableLayoutPanel _root;
        private readonly FlowLayoutPanel _content;
        private readonly FlowLayoutPanel _buttons;

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
            _root = root;

            FlowLayoutPanel content = new FlowLayoutPanel();
            content.Dock = DockStyle.Fill;
            content.FlowDirection = FlowDirection.TopDown;
            content.WrapContents = false;
            content.AutoScroll = true;
            _content = content;

            Label title = new Label();
            title.Text = Program.AppName;
            title.Font = new Font(Font.FontFamily, 16f, FontStyle.Bold);
            title.AutoSize = true;
            title.Margin = new Padding(0, 0, 0, 4);

            Label version = new Label();
            version.Text = L.T("about.version", Program.Version);
            version.AutoSize = true;
            version.Margin = new Padding(0, 0, 0, 14);

            LinkLabel description = new LinkLabel();
            description.Text = L.T("about.description");
            description.AutoSize = true;
            description.MaximumSize = new Size(440, 0);
            description.Margin = new Padding(0, 0, 0, 14);
            description.LinkColor = Color.Blue;
            AddProductLinks(description, description.Text);
            description.LinkClicked += delegate (object sender, LinkLabelLinkClickedEventArgs e)
            {
                string url = e.Link == null ? null : e.Link.LinkData as string;
                if (!string.IsNullOrEmpty(url)) OpenUrl(url);
            };

            Label author = new Label();
            author.Text = L.T("about.author", L.T("about.authorName"));
            author.AutoSize = true;
            author.Margin = new Padding(0, 0, 0, 4);

            LinkLabel link = CreateLink(L.T("about.link", Program.ProjectUrl),
                Program.ProjectUrl, 4);

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
            _buttons = buttons;

            root.Controls.Add(content, 0, 0);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            AcceptButton = close;
            CancelButton = close;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                // Measure actual children: a Dock.Fill FlowLayoutPanel reports its
                // filled height as PreferredSize, which would keep the old blank.
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

        private static void AddProductLinks(LinkLabel label, string text)
        {
            int webStart = text.IndexOf("Pi Web", StringComparison.Ordinal);
            if (webStart >= 0)
                label.Links.Add(webStart, "Pi Web".Length, PiWebUrl);

            int start = 0;
            while ((start = text.IndexOf("Pi", start, StringComparison.Ordinal)) >= 0)
            {
                bool insideWeb = webStart >= 0
                    && start < webStart + "Pi Web".Length
                    && start + "Pi".Length > webStart;
                if (!insideWeb) label.Links.Add(start, "Pi".Length, PiUrl);
                start += "Pi".Length;
            }
        }

        private static LinkLabel CreateLink(string text, string url, int bottomMargin)
        {
            LinkLabel link = new LinkLabel();
            link.Text = text;
            link.AutoSize = true;
            link.Margin = new Padding(0, 0, 0, bottomMargin);
            // Only the URL is the link; the product/project prefix stays normal text.
            int start = text.IndexOf(url, StringComparison.Ordinal);
            link.LinkArea = start >= 0
                ? new LinkArea(start, url.Length)
                : new LinkArea(0, text.Length);
            link.LinkClicked += delegate { OpenUrl(url); };
            return link;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                using (Process browser = Process.Start(url))
                {
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
