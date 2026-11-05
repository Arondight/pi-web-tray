// Pi Web Tray — settings window
//
// Edits the supported keys of pi-web-tray.ini without requiring the user to find and
// edit the file. Port, language, working directory and explicit tool paths take effect
// after the tray is restarted.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class SettingsForm : Form
    {
        private sealed class LanguageOption
        {
            internal string Value;
            internal string Text;

            public override string ToString()
            {
                return Text;
            }
        }

        private readonly Config _config;
        private readonly TableLayoutPanel _root;
        private readonly TableLayoutPanel _outer;
        private readonly FlowLayoutPanel _buttons;
        private readonly NumericUpDown _port;
        private readonly TextBox _workingDir;
        private readonly ComboBox _language;
        private readonly CheckBox _autoInstall;
        private readonly CheckBox _autoRestart;
        private readonly CheckBox _autoCheckUpdates;
        private readonly TextBox _installTag;
        private readonly TextBox _npmRegistry;
        private readonly TextBox _proxy;
        private readonly Button _testProxy;
        private readonly TextBox _nodePath;

        internal SettingsForm(Config config)
            : this(config, true)
        {
        }

        internal bool RestartRequested { get; private set; }

        internal SettingsForm(Config config, bool allowPortChange)
        {
            SuspendLayout();
            _config = config;

            Text = L.T("settings.title");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            // Scaling runs on ResumeLayout, after the whole control tree exists.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 560);
            MinimumSize = new Size(560, 420);
            Icon = Icons.WindowIcon();

            TableLayoutPanel outer = new TableLayoutPanel();
            outer.Dock = DockStyle.Fill;
            outer.Padding = new Padding(14);
            outer.ColumnCount = 1;
            outer.RowCount = 2;
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _outer = outer;

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.Margin = Padding.Empty;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 2;
            root.RowCount = 11;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 12; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scroll.Controls.Add(root);
            _root = root;

            _port = new NumericUpDown();
            _port.Minimum = 1;
            _port.Maximum = 65535;
            _port.Value = Math.Max(1, Math.Min(65535, config.Port));
            _port.Width = 120;
            _port.Enabled = allowPortChange;
            _port.AutoSize = false;
            _port.Anchor = AnchorStyles.Left;
            AddRow(root, 0, L.T("settings.port"), _port);

            _workingDir = new TextBox();
            _workingDir.Text = config.WorkingDir ?? "";
            AddBrowseRow(root, 1, L.T("settings.workingDir"), _workingDir,
                L.T("settings.workingDir"), delegate { BrowseFolder(_workingDir); });

            _language = new ComboBox();
            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Width = 220;
            _language.AutoSize = false;
            _language.MinimumSize = new Size(220, 0);
            _language.Anchor = AnchorStyles.Left;
            AddLanguageItems(config.Language);
            AddRow(root, 2, L.T("settings.language"), _language);

            _autoInstall = new CheckBox();
            _autoInstall.Text = L.T("settings.autoInstall");
            _autoInstall.Checked = config.AutoInstall;
            _autoInstall.AutoSize = true;
            AddRow(root, 3, "", _autoInstall);

            _autoRestart = new CheckBox();
            _autoRestart.Text = L.T("settings.autoRestart");
            _autoRestart.Checked = config.AutoRestart;
            _autoRestart.AutoSize = true;
            AddRow(root, 4, "", _autoRestart);

            _autoCheckUpdates = new CheckBox();
            _autoCheckUpdates.Text = L.T("settings.autoCheckUpdates");
            _autoCheckUpdates.Checked = config.AutoCheckUpdates;
            _autoCheckUpdates.AutoSize = true;
            AddRow(root, 5, "", _autoCheckUpdates);

            _installTag = new TextBox();
            _installTag.Text = config.InstallTag ?? "latest";
            AddRow(root, 6, L.T("settings.installTag"), _installTag);

            _npmRegistry = new TextBox();
            _npmRegistry.Text = config.NpmRegistry ?? "";
            AddRow(root, 7, L.T("settings.npmRegistry"), _npmRegistry);

            _proxy = new TextBox();
            _proxy.Text = config.Proxy ?? "";
            _testProxy = new Button();
            _testProxy.Text = L.T("settings.testProxy");
            _testProxy.AutoSize = true;
            _testProxy.MinimumSize = new Size(88, 0);
            _testProxy.Click += delegate { TestProxy(); };
            TableLayoutPanel proxyLine = new TableLayoutPanel();
            proxyLine.Dock = DockStyle.Fill;
            proxyLine.AutoSize = true;
            proxyLine.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            proxyLine.ColumnCount = 2;
            proxyLine.RowCount = 1;
            proxyLine.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            proxyLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            proxyLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _proxy.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _proxy.Margin = new Padding(0, 0, 6, 0);
            _testProxy.Anchor = AnchorStyles.Left;
            _testProxy.Margin = new Padding(0);
            proxyLine.Controls.Add(_proxy, 0, 0);
            proxyLine.Controls.Add(_testProxy, 1, 0);
            AddRow(root, 8, L.T("settings.proxy"), proxyLine);

            _nodePath = new TextBox();
            _nodePath.Text = config.NodePath ?? "";
            AddBrowseRow(root, 9, L.T("settings.nodePath"), _nodePath,
                L.T("settings.selectNode"), delegate { BrowseFile(_nodePath, L.T("settings.selectNode"), "node.exe|node.exe|*.exe|*.exe"); });

            Label note = new Label();
            note.Text = L.T("settings.restartNote");
            note.AutoSize = true;
            note.MaximumSize = new Size(520, 0);
            note.Margin = new Padding(0, 12, 0, 12);
            root.Controls.Add(note, 0, 10);
            root.SetColumnSpan(note, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;
            buttons.Margin = new Padding(0, 8, 0, 0);

            Button save = new Button();
            save.Text = L.T("settings.save");
            save.AutoSize = true;
            save.MinimumSize = new Size(88, 0);
            save.Click += delegate { SaveSettings(false); };

            Button cancel = new Button();
            cancel.Text = L.T("settings.cancel");
            cancel.AutoSize = true;
            cancel.MinimumSize = new Size(88, 0);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Button restart = new Button();
            restart.Text = L.T("settings.saveRestart");
            restart.AutoSize = true;
            restart.MinimumSize = new Size(112, 0);
            restart.Click += delegate { SaveSettings(true); };

            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(restart);
            _buttons = buttons;

            outer.Controls.Add(scroll, 0, 0);
            outer.Controls.Add(buttons, 0, 1);
            Controls.Add(outer);

            ResumeLayout(false);
            PerformLayout();
            // Establish the label column and fitted height before the window is
            // shown; OnLoad repeats this after per-monitor DPI scaling.
            ApplyInitialLayout();

            AcceptButton = save;
            CancelButton = cancel;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyInitialLayout();
        }

        // NumericUpDown and ComboBox fixed widths can be collapsed by the
        // DPI-scaled TableLayoutPanel; apply them after the final layout pass.
        // CreateGraphics works on Windows 7-11; DeviceDpi may still report 96.
        private void ApplyInitialLayout()
        {
            FormLayout.FitToWorkingArea(this);
            double scale = 1.0;
            try
            {
                using (Graphics graphics = CreateGraphics())
                    scale = graphics.DpiX / 96.0;
            }
            catch (Exception)
            {
            }
            AlignLabels(scale);
            _port.Width = (int)Math.Round(120 * scale);
            _language.Width = (int)Math.Round(220 * scale);
            FitInitialHeight();
        }

        private void FitInitialHeight()
        {
            try
            {
                // The settings content is usually shorter than the default client
                // height. Do not leave a large dead area between the last row and
                // the bottom buttons; grow for taller DPI/language layouts, and
                // let the scroll panel take over only when the screen is too small.
                int desired = _root.PreferredSize.Height + _outer.Padding.Vertical
                    + _buttons.PreferredSize.Height + _buttons.Margin.Vertical;
                int chrome = Height - ClientSize.Height;
                Rectangle working = Screen.FromControl(this).WorkingArea;
                int maxClient = Math.Max(360, working.Height - chrome - 20);
                int height = Math.Max(MinimumSize.Height - chrome, Math.Min(desired, maxClient));
                if (height > 0 && height != ClientSize.Height)
                {
                    ClientSize = new Size(ClientSize.Width, height);
                    PerformLayout();
                }
            }
            catch (Exception)
            {
            }
        }

        // Keep the input column independent of the label font, language and DPI.
        // Label preferred sizes are measured after Windows has scaled the form, then
        // labels fill their cells and are centred vertically. This avoids the small
        // per-control margin drift that a fixed margin produces at some DPI scales.
        private void AlignLabels(double scale)
        {
            int maxLabelWidth = 0;
            foreach (Control control in _root.Controls)
            {
                Label label = control as Label;
                if (label == null || _root.GetColumnSpan(label) != 1
                    || string.IsNullOrEmpty(label.Text))
                    continue;
                maxLabelWidth = Math.Max(maxLabelWidth, label.PreferredSize.Width);
            }
            if (maxLabelWidth <= 0) return;

            int gap = Math.Max(6, (int)Math.Round(12 * scale));
            _root.ColumnStyles[0] = new ColumnStyle(SizeType.Absolute, maxLabelWidth + gap);
            foreach (Control control in _root.Controls)
            {
                Label label = control as Label;
                if (label == null || _root.GetColumnSpan(label) != 1) continue;
                label.AutoSize = false;
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleRight;
                label.Margin = new Padding(0, 0, gap, 0);
            }
            _root.PerformLayout();
        }

        private void AddLanguageItems(string current)
        {
            _language.Items.Add(new LanguageOption { Value = L.Auto, Text = L.T("settings.langAuto") });
            _language.Items.Add(new LanguageOption { Value = "en", Text = L.T("settings.langEn") });
            _language.Items.Add(new LanguageOption { Value = "zh", Text = L.T("settings.langZh") });
            _language.Items.Add(new LanguageOption { Value = "zh-Hant", Text = L.T("settings.langZhHant") });

            string wanted = (current ?? L.Auto).Trim();
            if (wanted.Length == 0) wanted = L.Auto;

            // "zh-Hans", "zh-CN" and similar values resolve to the "zh" choice;
            // "en-US" resolves to English, and "auto" stays automatic.
            if (!string.Equals(wanted, L.Auto, StringComparison.OrdinalIgnoreCase))
                wanted = L.Current;

            int index = 0;
            for (int i = 0; i < _language.Items.Count; i++)
            {
                LanguageOption option = _language.Items[i] as LanguageOption;
                if (option == null) continue;
                if (string.Equals(option.Value, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            _language.SelectedIndex = index;
        }

        private static void AddRow(TableLayoutPanel root, int row, string labelText, Control control)
        {
            Label label = new Label();
            label.Text = labelText;
            label.AutoSize = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            // AutoSize keeps the label at its text width; anchoring right puts every
            // label's right edge on the same line, next to the input column.
            label.Anchor = AnchorStyles.Right;
            label.Margin = new Padding(0, 6, 12, 6);
            root.Controls.Add(label, 0, row);

            // NumericUpDown margins are scaled repeatedly at 200% DPI. Keep this row
            // compact and let the label determine its height.
            if (control is NumericUpDown)
                control.Margin = Padding.Empty;
            else
                control.Margin = new Padding(0, 4, 0, 4);

            if (control is TextBox)
                control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            else if (control is CheckBox || control is NumericUpDown || control is ComboBox)
                control.Anchor = AnchorStyles.Left;
            else
                control.Dock = DockStyle.Fill;
            root.Controls.Add(control, 1, row);
        }

        private static void AddBrowseRow(TableLayoutPanel root, int row, string labelText,
            TextBox textBox, string buttonText, EventHandler browse)
        {
            TableLayoutPanel line = new TableLayoutPanel();
            line.Dock = DockStyle.Fill;
            line.AutoSize = true;
            line.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            line.ColumnCount = 2;
            line.RowCount = 1;
            line.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            textBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            textBox.Margin = new Padding(0, 0, 6, 0);
            line.Controls.Add(textBox, 0, 0);

            Button button = new Button();
            button.Text = buttonText;
            button.AutoSize = true;
            button.Anchor = AnchorStyles.Left;
            button.Margin = new Padding(6, 0, 0, 0);
            button.Click += browse;
            line.Controls.Add(button, 1, 0);

            AddRow(root, row, labelText, line);
        }

        private void BrowseFolder(TextBox target)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = L.T("settings.workingDir");
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
            }
        }

        private void BrowseFile(TextBox target, string title, string filter)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = title;
                dialog.Filter = filter;
                dialog.CheckFileExists = true;
                if (File.Exists(target.Text))
                {
                    string directory = Path.GetDirectoryName(target.Text);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                        dialog.InitialDirectory = directory;
                    dialog.FileName = Path.GetFileName(target.Text);
                }
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
            }
        }

        private void TestProxy()
        {
            string text = (_proxy.Text ?? "").Trim();
            ProxySettings parsed;
            string error;
            if (!ProxySettings.TryParse(text, out parsed, out error))
            {
                ShowError(error);
                _proxy.Focus();
                return;
            }

            _testProxy.Enabled = false;
            _testProxy.Text = L.T("settings.proxyTesting");
            Thread worker = new Thread(delegate ()
            {
                string testError;
                bool ok = ProxySettings.Test(text, out testError);
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate ()
                        {
                            if (IsDisposed) return;
                            _testProxy.Enabled = true;
                            _testProxy.Text = L.T("settings.testProxy");
                            if (ok)
                                MessageBox.Show(this, L.T("settings.proxyOk"),
                                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            else
                                ShowError(L.T("settings.proxyFailed", testError ?? ""));
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

        private bool SaveSettings(bool restart)
        {
            string workingDir = (_workingDir.Text ?? "").Trim();
            if (workingDir.Length == 0 || !Directory.Exists(workingDir))
            {
                ShowError(L.T("settings.invalidWorkingDir"));
                _workingDir.Focus();
                return false;
            }

            string nodePath = (_nodePath.Text ?? "").Trim();
            if (nodePath.Length > 0 && !File.Exists(nodePath))
            {
                ShowError(L.T("settings.invalidNodePath"));
                _nodePath.Focus();
                return false;
            }

            string registry = (_npmRegistry.Text ?? "").Trim();
            if (registry.Length > 0)
            {
                Uri uri;
                if (!Uri.TryCreate(registry, UriKind.Absolute, out uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    ShowError(L.T("settings.invalidRegistry"));
                    _npmRegistry.Focus();
                    return false;
                }
            }

            string proxy = (_proxy.Text ?? "").Trim();
            if (proxy.Length > 0)
            {
                string proxyError;
                if (!ProxySettings.TryParse(proxy, out _, out proxyError))
                {
                    ShowError(proxyError);
                    _proxy.Focus();
                    return false;
                }
            }

            string tag = (_installTag.Text ?? "").Trim();
            if (tag.Length == 0) tag = "latest";
            if (!string.Equals(Installer.SafeTag(tag), tag, StringComparison.Ordinal))
            {
                ShowError(L.T("settings.invalidTag"));
                _installTag.Focus();
                return false;
            }

            int port = (int)_port.Value;
            if (port != _config.Port)
            {
                // A cheap occupancy warning; the full owner probe is asynchronous in
                // the tray and must not freeze this dialog.
                if (Net.IsPortOpen(port, 300))
                {
                    DialogResult answer = MessageBox.Show(
                        this,
                        L.T("settings.portInUseBody", port),
                        L.T("settings.portInUseTitle"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return false;
                }
            }

            LanguageOption language = _language.SelectedItem as LanguageOption;
            string languageValue = language == null ? L.Auto : language.Value;

            string oldProxy = _config.Proxy ?? "";
            List<KeyValuePair<string, string>> values = new List<KeyValuePair<string, string>>();
            values.Add(new KeyValuePair<string, string>("Port", port.ToString(CultureInfo.InvariantCulture)));
            values.Add(new KeyValuePair<string, string>("WorkingDir", workingDir));
            values.Add(new KeyValuePair<string, string>("Language", languageValue));
            values.Add(new KeyValuePair<string, string>("AutoInstall", _autoInstall.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("AutoRestart", _autoRestart.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("AutoCheckUpdates", _autoCheckUpdates.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("InstallTag", tag));
            values.Add(new KeyValuePair<string, string>("NpmRegistry", registry));
            values.Add(new KeyValuePair<string, string>("Proxy", proxy));
            values.Add(new KeyValuePair<string, string>("NodePath", nodePath));

            if (!_config.SetValues(values))
            {
                ShowError(L.T("settings.saveFailed"));
                return false;
            }

            _config.Port = port;
            _config.WorkingDir = workingDir;
            _config.Language = languageValue;
            _config.AutoInstall = _autoInstall.Checked;
            _config.AutoRestart = _autoRestart.Checked;
            _config.AutoCheckUpdates = _autoCheckUpdates.Checked;
            _config.InstallTag = tag;
            _config.NpmRegistry = registry;
            _config.Proxy = proxy;
            if (!string.Equals(oldProxy, proxy, StringComparison.Ordinal))
                ProxyTransport.Reset();
            _config.NodePath = nodePath;

            RestartRequested = restart;
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
