// Pi Web Tray — settings window
//
// Edits the supported keys of pi-web-tray.ini without requiring the user to find and
// edit the file. Port, language, working directory and explicit tool paths take effect
// after the tray is restarted; dependency settings apply to the next install.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
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
        private readonly NumericUpDown _port;
        private readonly TextBox _workingDir;
        private readonly ComboBox _language;
        private readonly CheckBox _autoInstall;
        private readonly CheckBox _installPiCli;
        private readonly CheckBox _autoRestart;
        private readonly CheckBox _autoCheckUpdates;
        private readonly TextBox _installTag;
        private readonly TextBox _npmRegistry;
        private readonly TextBox _nodePath;
        private readonly TextBox _serverScript;

        internal SettingsForm(Config config)
        {
            SuspendLayout();
            _config = config;

            Text = L.T("settings.title");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
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

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 2;
            root.RowCount = 12;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 12; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            scroll.Controls.Add(root);

            _port = new NumericUpDown();
            _port.Minimum = 1;
            _port.Maximum = 65535;
            _port.Value = Math.Max(1, Math.Min(65535, config.Port));
            _port.Width = 120;
            _port.Anchor = AnchorStyles.Left;
            AddRow(root, 0, L.T("settings.port"), _port);

            _workingDir = new TextBox();
            _workingDir.Text = config.WorkingDir ?? "";
            AddBrowseRow(root, 1, L.T("settings.workingDir"), _workingDir,
                L.T("settings.workingDir"), delegate { BrowseFolder(_workingDir); });

            _language = new ComboBox();
            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Width = 220;
            _language.Anchor = AnchorStyles.Left;
            AddLanguageItems(config.Language);
            AddRow(root, 2, L.T("settings.language"), _language);

            _autoInstall = new CheckBox();
            _autoInstall.Text = L.T("settings.autoInstall");
            _autoInstall.Checked = config.AutoInstall;
            _autoInstall.AutoSize = true;
            AddRow(root, 3, "", _autoInstall);

            _installPiCli = new CheckBox();
            _installPiCli.Text = L.T("settings.installPiCli");
            _installPiCli.Checked = config.InstallPiCli;
            _installPiCli.AutoSize = true;
            AddRow(root, 4, "", _installPiCli);

            _autoRestart = new CheckBox();
            _autoRestart.Text = L.T("settings.autoRestart");
            _autoRestart.Checked = config.AutoRestart;
            _autoRestart.AutoSize = true;
            AddRow(root, 5, "", _autoRestart);

            _autoCheckUpdates = new CheckBox();
            _autoCheckUpdates.Text = L.T("settings.autoCheckUpdates");
            _autoCheckUpdates.Checked = config.AutoCheckUpdates;
            _autoCheckUpdates.AutoSize = true;
            AddRow(root, 6, "", _autoCheckUpdates);

            _installTag = new TextBox();
            _installTag.Text = config.InstallTag ?? "latest";
            AddRow(root, 7, L.T("settings.installTag"), _installTag);

            _npmRegistry = new TextBox();
            _npmRegistry.Text = config.NpmRegistry ?? "";
            AddRow(root, 8, L.T("settings.npmRegistry"), _npmRegistry);

            _nodePath = new TextBox();
            _nodePath.Text = config.NodePath ?? "";
            AddBrowseRow(root, 9, L.T("settings.nodePath"), _nodePath,
                L.T("settings.selectNode"), delegate { BrowseFile(_nodePath, L.T("settings.selectNode"), "node.exe|node.exe|*.exe|*.exe"); });

            _serverScript = new TextBox();
            _serverScript.Text = config.ServerScript ?? "";
            AddBrowseRow(root, 10, L.T("settings.serverScript"), _serverScript,
                L.T("settings.selectServer"), delegate { BrowseFile(_serverScript, L.T("settings.selectServer"), "pi-web.js|pi-web.js|*.js|*.js"); });

            Label note = new Label();
            note.Text = L.T("settings.restartNote");
            note.AutoSize = true;
            note.MaximumSize = new Size(520, 0);
            note.Margin = new Padding(0, 12, 0, 12);
            root.Controls.Add(note, 0, 11);
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
            save.Click += delegate { SaveSettings(); };

            Button cancel = new Button();
            cancel.Text = L.T("settings.cancel");
            cancel.AutoSize = true;
            cancel.MinimumSize = new Size(88, 0);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);

            outer.Controls.Add(scroll, 0, 0);
            outer.Controls.Add(buttons, 0, 1);
            Controls.Add(outer);

            ResumeLayout(false);
            PerformLayout();

            AcceptButton = save;
            CancelButton = cancel;
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
            label.Margin = new Padding(0, 6, 12, 6);
            root.Controls.Add(label, 0, row);

            control.Margin = new Padding(0, 4, 0, 4);
            if (!(control is CheckBox) && !(control is NumericUpDown) && !(control is ComboBox))
                control.Dock = DockStyle.Fill;
            root.Controls.Add(control, 1, row);
        }

        private static void AddBrowseRow(TableLayoutPanel root, int row, string labelText,
            TextBox textBox, string buttonText, EventHandler browse)
        {
            TableLayoutPanel line = new TableLayoutPanel();
            line.Dock = DockStyle.Fill;
            line.ColumnCount = 2;
            line.RowCount = 1;
            line.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            textBox.Dock = DockStyle.Fill;
            line.Controls.Add(textBox, 0, 0);

            Button button = new Button();
            button.Text = buttonText;
            button.AutoSize = true;
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

        private void SaveSettings()
        {
            string workingDir = (_workingDir.Text ?? "").Trim();
            if (workingDir.Length == 0 || !Directory.Exists(workingDir))
            {
                ShowError(L.T("settings.invalidWorkingDir"));
                _workingDir.Focus();
                return;
            }

            string nodePath = (_nodePath.Text ?? "").Trim();
            if (nodePath.Length > 0 && !File.Exists(nodePath))
            {
                ShowError(L.T("settings.invalidNodePath"));
                _nodePath.Focus();
                return;
            }

            string serverScript = (_serverScript.Text ?? "").Trim();
            if (serverScript.Length > 0 && !File.Exists(serverScript))
            {
                ShowError(L.T("settings.invalidServerScript"));
                _serverScript.Focus();
                return;
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
                    return;
                }
            }

            string tag = (_installTag.Text ?? "").Trim();
            if (tag.Length == 0) tag = "latest";
            if (!string.Equals(Installer.SafeTag(tag), tag, StringComparison.Ordinal))
            {
                ShowError(L.T("settings.invalidTag"));
                _installTag.Focus();
                return;
            }

            int port = (int)_port.Value;
            if (port != _config.Port)
            {
                PortOwnerInfo owner = ServiceProbe.Inspect(port);
                if (owner.IsUnknown)
                {
                    DialogResult answer = MessageBox.Show(
                        this,
                        L.T("settings.portInUseBody", port),
                        L.T("settings.portInUseTitle"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) return;
                }
            }

            LanguageOption language = _language.SelectedItem as LanguageOption;
            string languageValue = language == null ? L.Auto : language.Value;

            List<KeyValuePair<string, string>> values = new List<KeyValuePair<string, string>>();
            values.Add(new KeyValuePair<string, string>("Port", port.ToString(CultureInfo.InvariantCulture)));
            values.Add(new KeyValuePair<string, string>("WorkingDir", workingDir));
            values.Add(new KeyValuePair<string, string>("Language", languageValue));
            values.Add(new KeyValuePair<string, string>("AutoInstall", _autoInstall.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("InstallPiCli", _installPiCli.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("AutoRestart", _autoRestart.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("AutoCheckUpdates", _autoCheckUpdates.Checked ? "1" : "0"));
            values.Add(new KeyValuePair<string, string>("InstallTag", tag));
            values.Add(new KeyValuePair<string, string>("NpmRegistry", registry));
            values.Add(new KeyValuePair<string, string>("NodePath", nodePath));
            values.Add(new KeyValuePair<string, string>("ServerScript", serverScript));

            if (!_config.SetValues(values))
            {
                ShowError(L.T("settings.saveFailed"));
                return;
            }

            _config.Port = port;
            _config.WorkingDir = workingDir;
            _config.Language = languageValue;
            _config.AutoInstall = _autoInstall.Checked;
            _config.InstallPiCli = _installPiCli.Checked;
            _config.AutoRestart = _autoRestart.Checked;
            _config.AutoCheckUpdates = _autoCheckUpdates.Checked;
            _config.InstallTag = tag;
            _config.NpmRegistry = registry;
            _config.NodePath = nodePath;
            _config.ServerScript = serverScript;

            DialogResult = DialogResult.OK;
            Close();
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
