// Pi Web Tray — environment report window
//
// Displays the environment check as two tables: runtime components and application
// information. The form is populated on the UI thread from a ProbeResult produced by
// EnvironmentProbe.Detect().

using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class CheckForm : Form
    {
        private readonly Config _config;
        private readonly ProbeResult _probe;
        private readonly bool _elevated;
        private readonly DataGridView _runtimeGrid;
        private readonly DataGridView _softwareGrid;

        internal CheckForm(Config config, ProbeResult probe, bool elevated)
        {
            _config = config;
            _probe = probe;
            _elevated = elevated;

            Text = L.T("sc.title");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(920, 640);
            MinimumSize = new Size(760, 540);
            Icon = SystemIcons.Application;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Padding = new Padding(12);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label summary = new Label();
            summary.AutoSize = true;
            summary.MaximumSize = new Size(880, 0);
            summary.Padding = new Padding(0, 0, 0, 8);
            summary.Text = BuildSummary();

            _runtimeGrid = BuildRuntimeGrid();
            _softwareGrid = BuildSoftwareGrid();

            GroupBox runtimeGroup = BuildGroup(L.T("check.runtime"), _runtimeGrid);
            GroupBox softwareGroup = BuildGroup(L.T("check.software"), _softwareGrid);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.WrapContents = false;

            Button close = new Button();
            close.Text = L.T("check.close");
            close.AutoSize = true;
            close.MinimumSize = new Size(88, 0);
            close.Click += delegate { Close(); };

            Button copy = new Button();
            copy.Text = L.T("check.copy");
            copy.AutoSize = true;
            copy.MinimumSize = new Size(88, 0);
            copy.Click += delegate { CopyReport(); };

            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);

            root.Controls.Add(summary, 0, 0);
            root.Controls.Add(runtimeGroup, 0, 1);
            root.Controls.Add(softwareGroup, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);

            CancelButton = close;
        }

        internal static void ShowReport(Config config, ProbeResult probe, bool elevated)
        {
            using (CheckForm form = new CheckForm(config, probe, elevated))
            {
                form.ShowDialog();
            }
        }

        private static GroupBox BuildGroup(string title, Control content)
        {
            GroupBox group = new GroupBox();
            group.Text = title;
            group.Dock = DockStyle.Fill;
            group.Padding = new Padding(8);
            content.Dock = DockStyle.Fill;
            group.Controls.Add(content);
            return group;
        }

        private static DataGridView CreateGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.GridColor = SystemColors.ControlLight;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
            grid.RowTemplate.Height = Math.Max(24, grid.Font.Height + 8);
            return grid;
        }

        private DataGridView BuildRuntimeGrid()
        {
            DataGridView grid = CreateGrid();

            DataGridViewTextBoxColumn component = new DataGridViewTextBoxColumn();
            component.HeaderText = L.T("check.component");
            component.FillWeight = 20f;
            component.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn version = new DataGridViewTextBoxColumn();
            version.HeaderText = L.T("check.version");
            version.FillWeight = 16f;
            version.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn path = new DataGridViewTextBoxColumn();
            path.HeaderText = L.T("check.path");
            path.FillWeight = 44f;
            path.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn status = new DataGridViewTextBoxColumn();
            status.HeaderText = L.T("check.status");
            status.FillWeight = 20f;
            status.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.Columns.Add(component);
            grid.Columns.Add(version);
            grid.Columns.Add(path);
            grid.Columns.Add(status);

            AddComponentRow(grid, L.T("probe.node"), _probe.NodeVersion, _probe.NodePath, NodeStatus());
            AddComponentRow(grid, L.T("probe.npm"), _probe.NpmVersion, _probe.NpmCliPath,
                _probe.NpmFound ? L.T("check.ok") : L.T("check.missing"));
            AddComponentRow(grid, L.T("probe.piweb"), _probe.PiWebVersion, _probe.PiWebScript,
                _probe.PiWebFound ? L.T("check.ok") : L.T("check.missing"));
            AddComponentRow(grid, L.T("probe.picli"), _probe.PiCliVersion, _probe.PiCliPath,
                _probe.PiCliFound ? L.T("check.ok") : L.T("check.optional"));

            return grid;
        }

        private DataGridView BuildSoftwareGrid()
        {
            DataGridView grid = CreateGrid();

            DataGridViewTextBoxColumn item = new DataGridViewTextBoxColumn();
            item.HeaderText = L.T("check.item");
            item.FillWeight = 32f;
            item.SortMode = DataGridViewColumnSortMode.NotSortable;

            DataGridViewTextBoxColumn value = new DataGridViewTextBoxColumn();
            value.HeaderText = L.T("check.value");
            value.FillWeight = 68f;
            value.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.Columns.Add(item);
            grid.Columns.Add(value);

            bool listening = Net.IsPortOpen(_config.Port, 300);
            AddInfoRow(grid, L.T("sc.config"), _config.IniPath);
            AddInfoRow(grid, L.T("check.port"),
                L.T("check.listening", _config.Port, listening ? L.T("bool.yes") : L.T("bool.no")));
            AddInfoRow(grid, L.T("sc.workDir"), _config.WorkingDir);
            AddInfoRow(grid, L.T("sc.dataDir"), _config.OutputDir);
            AddInfoRow(grid, L.T("sc.admin"), _elevated ? L.T("bool.yes") : L.T("bool.no"));
            AddInfoRow(grid, L.T("sc.language"), L.Current);
            AddInfoRow(grid, L.T("probe.npmGlobal"),
                string.IsNullOrEmpty(_probe.NpmPrefix) ? L.T("probe.unknown") : _probe.NpmPrefix);
            AddInfoRow(grid, L.T("sc.serverLog"), _config.LogFile);
            AddInfoRow(grid, L.T("sc.installLog"), _config.InstallLogFile);
            AddInfoRow(grid, L.T("sc.updateLog"), _config.UpdateLogFile);

            return grid;
        }

        private static void AddComponentRow(
            DataGridView grid, string component, string version, string path, string status)
        {
            int index = grid.Rows.Add(component, version, path, status);
            DataGridViewRow row = grid.Rows[index];
            row.Cells[2].ToolTipText = path;
            row.Cells[3].ToolTipText = status;
        }

        private static void AddInfoRow(DataGridView grid, string item, string value)
        {
            int index = grid.Rows.Add(item, value);
            grid.Rows[index].Cells[1].ToolTipText = value;
        }

        private string NodeStatus()
        {
            if (!_probe.NodeFound) return L.T("check.missing");
            if (!_probe.NodeRunnable) return L.T("check.cannotRun");
            if (!_probe.NodeVersionOk) return L.T("check.tooOld");
            return L.T("check.ok");
        }

        private string BuildSummary()
        {
            string summary = _probe.Ready ? L.T("check.ready") : L.T("check.notReady");
            if (_probe.Ready) return summary;

            string issue = _probe.NeedsNode ? _probe.NodeIssue() : L.T("probe.needPiweb");
            if (!string.IsNullOrEmpty(issue)) summary += "\r\n" + issue;
            return summary;
        }

        private void CopyReport()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(BuildSummary());
                sb.AppendLine();
                AppendTable(sb, L.T("check.runtime"), _runtimeGrid);
                sb.AppendLine();
                AppendTable(sb, L.T("check.software"), _softwareGrid);

                Clipboard.SetText(sb.ToString());
                MessageBox.Show(this, L.T("check.copied"), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception)
            {
            }
        }

        private static void AppendTable(StringBuilder sb, string title, DataGridView grid)
        {
            sb.AppendLine(title);
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                if (i > 0) sb.Append("\t");
                sb.Append(grid.Columns[i].HeaderText);
            }
            sb.AppendLine();

            foreach (DataGridViewRow row in grid.Rows)
            {
                for (int i = 0; i < row.Cells.Count; i++)
                {
                    if (i > 0) sb.Append("\t");
                    sb.Append(row.Cells[i].Value == null ? "" : row.Cells[i].Value.ToString());
                }
                sb.AppendLine();
            }
        }
    }
}
