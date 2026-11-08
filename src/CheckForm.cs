// Pi Web Tray — environment report window
//
// Displays the environment check as two tables: runtime components and application
// information. The form is populated on the UI thread from a ProbeResult; Refresh runs
// EnvironmentProbe.Detect() on a background thread and updates the tables in place.

using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PiWebTray
{
    internal sealed class CheckForm : Form
    {
        private readonly Config _config;
        private readonly bool _elevated;
        private readonly TableLayoutPanel _root;
        private readonly DataGridView _runtimeGrid;
        private readonly DataGridView _softwareGrid;
        private readonly Label _summary;
        private readonly Button _refresh;
        private ProbeResult _probe;
        private bool _refreshing;
        private bool _userResized;
        private readonly bool _refreshOnShown;

        internal CheckForm(Config config, ProbeResult probe, bool elevated)
            : this(config, probe, elevated, false)
        {
        }

        internal CheckForm(Config config, ProbeResult probe, bool elevated, bool refreshOnShown)
        {
            SuspendLayout();
            _config = config;
            _probe = probe ?? new ProbeResult();
            _elevated = elevated;
            _refreshOnShown = refreshOnShown;

            Text = L.T("sc.title");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            // Resizable windows with a taskbar button should really minimize; otherwise
            // the system menu shows a dead "Minimize" item.
            MinimizeBox = true;
            ShowInTaskbar = true;
            Font = SystemFonts.MessageBoxFont;
            // Scaling runs on ResumeLayout, after the whole control tree exists.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(920, 640);
            MinimumSize = new Size(760, 540);
            Icon = Icons.WindowIcon();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Padding = new Padding(12);
            // On high-DPI or small displays the two tables and the button row can
            // exceed the work area. Scroll the content instead of clipping it.
            root.AutoScroll = true;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root = root;

            _summary = new Label();
            _summary.AutoSize = true;
            _summary.MaximumSize = new Size(880, 0);
            _summary.Padding = new Padding(0, 0, 0, 8);
            // A report opened with refreshOnShown performs its own probe. Do not show
            // a stale/empty ProbeResult as a final "not ready" verdict first.
            _summary.Text = _refreshOnShown ? L.T("check.refreshing") : BuildSummary();

            _runtimeGrid = BuildRuntimeGrid();
            _softwareGrid = BuildSoftwareGrid();

            GroupBox runtimeGroup = BuildGroup(L.T("check.runtime"), _runtimeGrid);
            _runtimeGrid.Dock = DockStyle.Top;
            runtimeGroup.AutoSize = true;
            runtimeGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            GroupBox softwareGroup = BuildGroup(L.T("check.software"), _softwareGrid);
            softwareGroup.AutoSize = false;
            softwareGroup.Dock = DockStyle.Fill;
            // Keep the software table visible when a very small/high-DPI work
            // area forces the form to shrink; the root's AutoScroll handles the
            // overflow.
            softwareGroup.MinimumSize = new Size(0, 160);

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

            _refresh = new Button();
            _refresh.Text = L.T("check.refresh");
            _refresh.AutoSize = true;
            _refresh.MinimumSize = new Size(88, 0);
            _refresh.Click += delegate { RefreshReport(); };

            buttons.Controls.Add(close);
            buttons.Controls.Add(_refresh);

            root.Controls.Add(_summary, 0, 0);
            root.Controls.Add(runtimeGroup, 0, 1);
            root.Controls.Add(softwareGroup, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            // Do not re-fit after the user has chosen a window size. The initial
            // content fit and the post-probe fit are allowed to resize the form.
            ResizeBegin += delegate { _userResized = true; };
            CancelButton = close;
        }

        internal ProbeResult Probe
        {
            get { return _probe; }
        }

        internal static ProbeResult ShowReport(Config config, ProbeResult probe, bool elevated)
        {
            return ShowReport(config, probe, elevated, false);
        }

        internal static ProbeResult ShowReport(Config config, ProbeResult probe, bool elevated,
            bool refreshOnShown)
        {
            using (CheckForm form = new CheckForm(config, probe, elevated, refreshOnShown))
            {
                form.ShowDialog();
                return form.Probe;
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

        private DataGridView CreateGrid()
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

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem copyRow = new ToolStripMenuItem(L.T("check.copyRow"));
            copyRow.Click += delegate { CopyRow(grid); };
            ToolStripMenuItem copyAll = new ToolStripMenuItem(L.T("check.copyAll"));
            copyAll.Click += delegate { CopyTable(grid); };
            menu.Items.Add(copyRow);
            menu.Items.Add(copyAll);
            menu.Opening += delegate { copyRow.Enabled = grid.CurrentRow != null; };
            grid.ContextMenuStrip = menu;
            grid.CellMouseDown += delegate (object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                grid.ClearSelection();
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
                grid.Rows[e.RowIndex].Selected = true;
            };
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

            PopulateRuntimeGrid(grid);
            // The runtime table has a fixed small number of rows. Let its group
            // shrink to the table instead of taking 40% of the dialog and leaving
            // a large empty grid area; the software table takes the remaining space.
            grid.Dock = DockStyle.Top;
            grid.Height = Math.Max(60, grid.Rows.Count * grid.RowTemplate.Height + 32);
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

            // The owner probe can take seconds (netstat + HTTP fallback). Populate the
            // static rows immediately and refresh the port owner on a background worker
            // after the form is shown. The port row must say "checking", not falsely
            // claim that the port is closed before the worker has run.
            PopulateSoftwareGrid(grid, false, null, true);
            return grid;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Fit before the first paint; OnShown would show the default height
            // for one frame and then visibly shrink the window.
            FormLayout.FitToWorkingArea(this);
            FitForDisplay();
        }

        // Separated from OnLoad so layout tests do not need an interactive desktop.
        internal void FitForDisplay()
        {
            // The runtime table has a fixed small number of rows. The software
            // group lives in the Percent row and fills the remaining height; only
            // the runtime table needs a content-derived height.
            FitGridHeight(_runtimeGrid);
            FitInitialHeight();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_refreshOnShown) RefreshReport();
            else RefreshPortOwner();
        }

        private static int GridContentHeight(DataGridView grid)
        {
            int rowHeight = grid.Rows.GetRowsHeight(DataGridViewElementStates.Visible);
            if (rowHeight <= 0) rowHeight = grid.Rows.Count * grid.RowTemplate.Height;
            int headerHeight = grid.ColumnHeadersHeight;
            if (headerHeight <= 0) headerHeight = grid.RowTemplate.Height;
            return Math.Max(60, headerHeight + rowHeight + 4);
        }

        private static void FitGridHeight(DataGridView grid)
        {
            try
            {
                grid.Dock = DockStyle.Top;
                grid.Height = GridContentHeight(grid);
                GroupBox group = grid.Parent as GroupBox;
                if (group != null)
                {
                    group.AutoSize = true;
                    group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    group.PerformLayout();
                }
            }
            catch (Exception)
            {
            }
        }

        // The software table is deliberately NOT fitted to its rows. It lives in
        // the Percent row and must keep filling the space above the button row.
        // Instead, shrink the whole dialog to its current content so the default
        // window does not open with a large empty grid area. Resizing the window
        // later still lets the table absorb the extra height.
        private void FitInitialHeight()
        {
            try
            {
                if (WindowState != FormWindowState.Normal) return;
                PerformLayout();
                GroupBox softwareGroup = _softwareGrid.Parent as GroupBox;
                if (softwareGroup == null) return;

                int groupChrome = Math.Max(0, softwareGroup.Height - _softwareGrid.Height);
                int desiredGroup = Math.Max(softwareGroup.MinimumSize.Height,
                    GridContentHeight(_softwareGrid) + groupChrome);
                int desiredClient = ClientSize.Height + (desiredGroup - softwareGroup.Height);

                int chrome = Height - ClientSize.Height;
                int maxClient = Math.Max(300,
                    Screen.FromControl(this).WorkingArea.Height - chrome - 20);
                int minClient = Math.Max(300, MinimumSize.Height - chrome);
                int height = Math.Max(minClient, Math.Min(desiredClient, maxClient));
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

        private void RefreshPortOwner()
        {
            if (_refreshing) return;
            _refreshing = true;
            _refresh.Enabled = false;
            UseWaitCursor = true;

            Thread worker = new Thread(delegate ()
            {
                bool listening = false;
                PortOwnerInfo owner = null;
                try
                {
                    listening = Net.IsPortOpen(_config.Port, 300);
                    ServiceProbe.Invalidate();
                    owner = ServiceProbe.Inspect(_config.Port, ExpectedScript());
                }
                catch (Exception)
                {
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate ()
                        {
                            if (IsDisposed) return;
                            _refreshing = false;
                            _refresh.Enabled = true;
                            UseWaitCursor = false;
                            PopulateSoftwareGrid(_softwareGrid, listening, owner);
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

        private void PopulateRuntimeGrid(DataGridView grid)
        {
            grid.Rows.Clear();
            AddComponentRow(grid, L.T("probe.node"), _probe.NodeVersion, _probe.NodePath, NodeStatus());
            AddComponentRow(grid, L.T("probe.npm"), _probe.NpmVersion, _probe.NpmCliPath,
                _probe.NpmFound ? L.T("check.ok") : L.T("check.missing"));
            AddComponentRow(grid, L.T("probe.piweb"), _probe.PiWebVersion, _probe.PiWebScript,
                _probe.PiWebFound ? L.T("check.ok") : L.T("check.missing"));
            AddComponentRow(grid, L.T("probe.picli"), _probe.PiCliVersion, _probe.PiCliPath,
                _probe.PiCliFound ? L.T("check.ok") : L.T("check.missing"));
        }

        private void PopulateSoftwareGrid(DataGridView grid, bool listening, PortOwnerInfo owner)
        {
            PopulateSoftwareGrid(grid, listening, owner, false);
        }

        private void PopulateSoftwareGrid(DataGridView grid, bool listening, PortOwnerInfo owner,
            bool checking)
        {
            grid.Rows.Clear();
            AddInfoRow(grid, L.T("sc.config"), _config.IniPath);
            AddInfoRow(grid, L.T("check.port"),
                checking
                    ? L.T("check.portState", _config.Port,
                        L.T("check.checking"), L.T("check.checking"))
                    : L.T("check.portState", _config.Port,
                        listening ? L.T("bool.yes") : L.T("bool.no"),
                        ServiceProbe.OwnerText(owner)));
            AddInfoRow(grid, L.T("sc.workDir"), _config.WorkingDir);
            AddInfoRow(grid, L.T("sc.dataDir"), _config.OutputDir);
            AddInfoRow(grid, L.T("sc.admin"), _elevated ? L.T("bool.yes") : L.T("bool.no"));
            AddInfoRow(grid, L.T("sc.language"), L.Current);
            AddInfoRow(grid, L.T("probe.npmGlobal"),
                string.IsNullOrEmpty(_probe.NpmPrefix) ? L.T("probe.unknown") : _probe.NpmPrefix);
            AddInfoRow(grid, L.T("sc.serverLog"), _config.LogFile);
            AddInfoRow(grid, L.T("sc.installLog"), _config.InstallLogFile);
            AddInfoRow(grid, L.T("sc.updateLog"), _config.UpdateLogFile);
        }

        // Apply a probe result without running the background probe. Used by the
        // refresh callback and by layout tests.
        internal void ApplyProbe(ProbeResult probe, bool listening, PortOwnerInfo owner)
        {
            _probe = probe;
            PopulateRuntimeGrid(_runtimeGrid);
            PopulateSoftwareGrid(_softwareGrid, listening, owner);
            FitGridHeight(_runtimeGrid);
            _summary.Text = BuildSummary();
        }

        private void RefreshReport()
        {
            if (_refreshing) return;
            _refreshing = true;
            _refresh.Enabled = false;
            _summary.Text = L.T("check.refreshing");
            UseWaitCursor = true;

            Thread worker = new Thread(delegate ()
            {
                ProbeResult result = null;
                string error = null;
                bool listening = false;
                PortOwnerInfo owner = null;
                try
                {
                    result = EnvironmentProbe.Detect(_config);
                    listening = Net.IsPortOpen(_config.Port, 300);
                    ServiceProbe.Invalidate();
                    // Use the fresh probe (not the pre-refresh _probe) for the expected
                    // script; otherwise a package that was just installed/updated can be
                    // classified against the old path.
                    string expectedScript = result != null && result.PiWebFound
                        ? result.PiWebScript
                        : ExpectedScript();
                    owner = ServiceProbe.Inspect(_config.Port, expectedScript);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate ()
                        {
                            if (IsDisposed) return;
                            _refreshing = false;
                            _refresh.Enabled = true;
                            UseWaitCursor = false;
                            if (result == null)
                            {
                                _summary.Text = BuildSummary();
                                MessageBox.Show(
                                    L.T("notify.selfCheckError", error ?? ""),
                                    L.T("sc.title"),
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                                return;
                            }

                            ApplyProbe(result, listening, owner);
                            // The summary can grow after the probe. Re-fit unless the
                            // user has already chosen a window size.
                            if (!_userResized) FitInitialHeight();
                            else PerformLayout();
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

        private string ExpectedScript()
        {
            if (_probe != null && _probe.PiWebFound && !string.IsNullOrEmpty(_probe.PiWebScript))
                return _probe.PiWebScript;
            return "";
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

            string issue = _probe.NeedsNode ? _probe.NodeIssue()
                : _probe.NeedsPiWeb ? L.T("probe.needPiweb")
                : L.T("probe.needPiCli");
            if (!string.IsNullOrEmpty(issue)) summary += "\r\n" + issue;
            return summary;
        }

        private static void CopyRow(DataGridView grid)
        {
            try
            {
                DataGridViewRow row = grid.CurrentRow;
                if (row == null) return;
                StringBuilder text = new StringBuilder();
                for (int i = 0; i < row.Cells.Count; i++)
                {
                    if (i > 0) text.Append('	');
                    text.Append(row.Cells[i].Value == null ? "" : row.Cells[i].Value.ToString());
                }
                Clipboard.SetText(text.ToString());
            }
            catch (Exception)
            {
            }
        }

        private static void CopyTable(DataGridView grid)
        {
            try
            {
                StringBuilder text = new StringBuilder();
                for (int i = 0; i < grid.Columns.Count; i++)
                {
                    if (i > 0) text.Append('	');
                    text.Append(grid.Columns[i].HeaderText);
                }
                text.AppendLine();

                foreach (DataGridViewRow row in grid.Rows)
                {
                    for (int i = 0; i < row.Cells.Count; i++)
                    {
                        if (i > 0) text.Append('	');
                        text.Append(row.Cells[i].Value == null ? "" : row.Cells[i].Value.ToString());
                    }
                    text.AppendLine();
                }
                Clipboard.SetText(text.ToString());
            }
            catch (Exception)
            {
            }
        }
    }
}
