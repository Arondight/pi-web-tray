// Pi Web Tray — About window
//
// A small static dialog with the application name, version, author and project link.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class AboutForm : Form
    {
        private const string PiWebUrl = "https://github.com/agegr/pi-web";
        private const string PiUrl = "https://github.com/earendil-works/pi";

        private sealed class LinkSpan
        {
            internal readonly int Start;
            internal readonly int Length;
            internal readonly string Url;

            internal LinkSpan(int start, int length, string url)
            {
                Start = start;
                Length = length;
                Url = url;
            }
        }

        private Icon _formIcon;
        private readonly TableLayoutPanel _root;
        private readonly FlowLayoutPanel _content;
        private readonly FlowLayoutPanel _buttons;
        private readonly List<Font> _linkFonts = new List<Font>();

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

            Label title = CreateTextLabel(Program.AppName);
            title.Font = new Font(Font.FontFamily, 16f, FontStyle.Bold);
            title.Margin = new Padding(0, 0, 0, 4);

            Label version = CreateTextLabel(L.T("about.version", Program.Version));
            version.Margin = new Padding(0, 0, 0, 14);

            FlowLayoutPanel description = CreateLine(14);
            AddDescriptionLinks(description, L.T("about.description"));

            Label author = CreateTextLabel(L.T("about.author", L.T("about.authorName")));
            author.Margin = new Padding(0, 0, 0, 4);

            FlowLayoutPanel project = CreateLine(4);
            AddProjectLink(project, L.T("about.link", Program.ProjectUrl));

            Label license = CreateTextLabel(L.T("about.license"));
            license.Margin = new Padding(0, 0, 0, 4);

            Label copyright = CreateTextLabel(L.T("about.copyright"));
            copyright.Margin = new Padding(0, 0, 0, 0);

            content.Controls.Add(title);
            content.Controls.Add(version);
            content.Controls.Add(description);
            content.Controls.Add(author);
            content.Controls.Add(project);
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

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                // Fit before the first paint; OnShown would show the default height
                // for one frame and then visibly shrink the window.
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
            if (disposing)
            {
                foreach (Font font in _linkFonts)
                {
                    try { font.Dispose(); } catch (Exception) { }
                }
                _linkFonts.Clear();
            }
        }

        // Labels use the same GDI renderer as Settings and the other dialogs.
        // LinkLabel always switches to GDI+, so the About links are ordinary
        // clickable labels with an underlined font instead.
        private static Label CreateTextLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = Padding.Empty;
            label.UseCompatibleTextRendering = false;
            return label;
        }

        private static FlowLayoutPanel CreateLine(int bottomMargin)
        {
            FlowLayoutPanel line = new FlowLayoutPanel();
            line.AutoSize = true;
            line.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            line.FlowDirection = FlowDirection.LeftToRight;
            line.WrapContents = true;
            line.MaximumSize = new Size(440, 0);
            line.Margin = new Padding(0, 0, 0, bottomMargin);
            return line;
        }

        private void AddText(FlowLayoutPanel line, string text, string url)
        {
            if (string.IsNullOrEmpty(text)) return;
            Label label = CreateTextLabel(text);
            if (!string.IsNullOrEmpty(url))
            {
                Font linkFont = new Font(Font, FontStyle.Underline);
                _linkFonts.Add(linkFont);
                label.Font = linkFont;
                label.ForeColor = SystemColors.HotTrack;
                label.Cursor = Cursors.Hand;
                label.Tag = url;
                label.TabStop = true;
                label.Click += delegate { OpenUrl(url); };
                label.KeyDown += delegate (object sender, KeyEventArgs e)
                {
                    if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
                    e.Handled = true;
                    OpenUrl(url);
                };
            }
            line.Controls.Add(label);
        }

        private void AddDescriptionLinks(FlowLayoutPanel line, string text)
        {
            List<LinkSpan> spans = FindProductSpans(text);
            int position = 0;
            foreach (LinkSpan span in spans)
            {
                if (span.Start > position)
                    AddText(line, text.Substring(position, span.Start - position), null);
                AddText(line, text.Substring(span.Start, span.Length), span.Url);
                position = span.Start + span.Length;
            }
            if (position < text.Length)
                AddText(line, text.Substring(position), null);
        }

        private static List<LinkSpan> FindProductSpans(string text)
        {
            List<LinkSpan> spans = new List<LinkSpan>();
            int webStart = text.IndexOf("Pi Web", StringComparison.Ordinal);
            if (webStart >= 0)
                spans.Add(new LinkSpan(webStart, "Pi Web".Length, PiWebUrl));

            int start = 0;
            while ((start = text.IndexOf("Pi", start, StringComparison.Ordinal)) >= 0)
            {
                bool insideWeb = webStart >= 0
                    && start < webStart + "Pi Web".Length
                    && start + "Pi".Length > webStart;
                if (!insideWeb) spans.Add(new LinkSpan(start, "Pi".Length, PiUrl));
                start += "Pi".Length;
            }
            spans.Sort(delegate (LinkSpan a, LinkSpan b) { return a.Start.CompareTo(b.Start); });
            return spans;
        }

        private void AddProjectLink(FlowLayoutPanel line, string text)
        {
            int start = text.IndexOf(Program.ProjectUrl, StringComparison.Ordinal);
            if (start < 0)
            {
                AddText(line, text, Program.ProjectUrl);
                return;
            }
            if (start > 0) AddText(line, text.Substring(0, start), null);
            AddText(line, text.Substring(start, Program.ProjectUrl.Length), Program.ProjectUrl);
            int end = start + Program.ProjectUrl.Length;
            if (end < text.Length) AddText(line, text.Substring(end), null);
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
