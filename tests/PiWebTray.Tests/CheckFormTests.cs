using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PiWebTray
{
    [TestClass]
    public sealed class CheckFormTests
    {
        [TestMethod]
        public void FormCanBeConstructedWithTwoTables()
        {
            Config config = new Config();
            config.IniPath = "pi-web-tray.ini";
            config.OutputDir = ".";
            config.WorkingDir = ".";
            config.LogFile = "server.log";
            config.InstallLogFile = "install.log";
            config.UpdateLogFile = "update.log";

            ProbeResult probe = new ProbeResult();
            using (CheckForm form = new CheckForm(config, probe, false))
            {
                Assert.AreEqual(L.T("sc.title"), form.Text);
                Assert.IsTrue(form.MinimizeBox, "A resizable taskbar window should minimize.");
                AssertDpiScaling(form);

                List<DataGridView> tables = FindControls<DataGridView>(form);
                Assert.AreEqual(2, tables.Count);

                bool runtimeTable = false;
                bool softwareTable = false;
                foreach (DataGridView table in tables)
                {
                    if (table.Columns.Count == 4 && table.Rows.Count == 4) runtimeTable = true;
                    if (table.Columns.Count == 2 && table.Rows.Count == 10) softwareTable = true;
                }
                Assert.IsTrue(runtimeTable, "Runtime components table has an unexpected shape.");
                Assert.IsTrue(softwareTable, "Application information table has an unexpected shape.");

                foreach (DataGridView table in tables)
                {
                    Assert.IsNotNull(table.ContextMenuStrip, "Each report table needs a context menu.");
                    Assert.AreEqual(2, table.ContextMenuStrip.Items.Count);
                    Assert.AreEqual(L.T("check.copyRow"), table.ContextMenuStrip.Items[0].Text);
                    Assert.AreEqual(L.T("check.copyAll"), table.ContextMenuStrip.Items[1].Text);
                }

                List<Button> buttons = FindControls<Button>(form);
                Assert.AreEqual(2, buttons.Count, "The old copy button must be gone.");

                bool scrolls = false;
                foreach (TableLayoutPanel panel in FindControls<TableLayoutPanel>(form))
                {
                    if (panel.AutoScroll) scrolls = true;
                }
                Assert.IsTrue(scrolls, "The environment report must scroll when content exceeds the window.");
            }
        }

        [TestMethod]
        public void WindowIconSurvivesFormDisposal()
        {
            Config config = new Config();
            config.Port = 30141;
            config.WorkingDir = Path.GetTempPath();
            config.Language = L.Auto;
            config.InstallTag = "latest";
            config.IniPath = "pi-web-tray.ini";
            config.OutputDir = ".";
            config.LogFile = "server.log";
            config.InstallLogFile = "install.log";
            config.UpdateLogFile = "update.log";

            using (SettingsForm form = new SettingsForm(config))
            {
                Assert.IsNotNull(form.Icon);
            }

            using (System.Drawing.Bitmap bitmap = Icons.WindowIcon().ToBitmap())
            {
                Assert.IsTrue(bitmap.Width > 0 && bitmap.Height > 0);
            }
        }

        [TestMethod]
        public void AboutFormCanBeConstructed()
        {
            using (AboutForm form = new AboutForm())
            {
                Assert.AreEqual(L.T("about.title"), form.Text);
                Assert.IsTrue(form.Controls.Count > 0);
                Assert.IsTrue(form.ShowInTaskbar, "About should have a taskbar window.");
                AssertDpiScaling(form);

                List<Button> buttons = FindControls<Button>(form);
                Assert.AreEqual(1, buttons.Count, "About window should only contain the close button.");

                List<Label> labels = FindControls<Label>(form);
                List<Label> links = new List<Label>();
                foreach (Label label in labels)
                {
                    Assert.IsFalse(label.UseCompatibleTextRendering,
                        "About must use the same GDI rendering as the other dialogs.");
                    if (label.Tag is string && !string.IsNullOrEmpty((string)label.Tag))
                        links.Add(label);
                }
                Assert.AreEqual(2, links.Count, "About should link Pi and Pi Web in the description.");

                List<string> urls = new List<string>();
                foreach (Label link in links)
                {
                    Assert.AreEqual(SystemColors.HotTrack, link.ForeColor,
                        "All About links must use the same colour.");
                    Assert.IsTrue(link.Font.Underline,
                        "About links must look like links.");
                    urls.Add((string)link.Tag);
                }
                Assert.IsTrue(urls.Contains("https://github.com/agegr/pi-web"),
                    "The description should link Pi Web.");
                Assert.IsTrue(urls.Contains("https://github.com/earendil-works/pi"),
                    "The description should link Pi.");
                List<DataGridView> tables = FindControls<DataGridView>(form);
                Assert.AreEqual(1, tables.Count, "About should contain the information table.");
                Assert.AreEqual(5, tables[0].Rows.Count, "About table should have five rows.");
                Assert.AreEqual(2, tables[0].Columns.Count);
                bool projectUrl = false;
                foreach (DataGridViewRow row in tables[0].Rows)
                {
                    if (row.Cells.Count > 1 && Program.ProjectUrl.Equals(row.Cells[1].Value))
                        projectUrl = true;
                }
                Assert.IsTrue(projectUrl, "The project URL should be in the table.");
            }
        }

        [TestMethod]
        public void NodePromptFormCanBeConstructed()
        {
            using (NodePromptForm form = new NodePromptForm("Node.js required", "Body", true))
            {
                Assert.AreEqual("Node.js required", form.Text);
                Assert.AreEqual(3, FindControls<Button>(form).Count);
                Assert.IsNotNull(form.CancelButton);
                Assert.AreEqual(DialogResult.Ignore, form.CancelButton.DialogResult,
                    "Esc should continue without exiting the tray.");
                Assert.AreEqual(DialogResult.Ignore, form.AcceptButton.DialogResult,
                    "Enter must not open an unsigned download by default.");
            }
            using (NodePromptForm form = new NodePromptForm("Unsupported", "Body", false))
            {
                Assert.AreEqual("Unsupported", form.Text);
                Assert.AreEqual(2, FindControls<Button>(form).Count);
                Assert.IsNotNull(form.CancelButton);
                Assert.AreEqual(DialogResult.Ignore, form.CancelButton.DialogResult,
                    "Esc should continue without exiting the tray.");
            }
        }

        [TestMethod]
        public void SettingsFormCanBeConstructed()
        {
            Config config = new Config();
            config.Port = 30141;
            config.WorkingDir = Path.GetTempPath();
            config.Language = L.Auto;
            config.InstallTag = "latest";
            using (SettingsForm form = new SettingsForm(config))
            {
                Assert.AreEqual(L.T("settings.title"), form.Text);
                Assert.AreEqual(FormBorderStyle.Sizable, form.FormBorderStyle);
                Assert.IsTrue(form.ShowInTaskbar, "Settings should have a taskbar window.");
                Assert.IsTrue(form.MinimizeBox, "Settings should minimize like the other dialogs.");
                AssertDpiScaling(form);
                Assert.IsTrue(form.MinimumSize.Width > 0);

                List<Button> buttons = FindControls<Button>(form);
                bool save = false;
                bool cancel = false;
                bool saveAndRestart = false;
                foreach (Button button in buttons)
                {
                    if (button.Text == L.T("settings.save")) save = true;
                    if (button.Text == L.T("settings.cancel")) cancel = true;
                    if (button.Text == L.T("settings.saveRestart")) saveAndRestart = true;
                }
                Assert.IsTrue(save, "Settings must expose Save.");
                Assert.IsTrue(cancel, "Settings must expose Cancel.");
                Assert.IsTrue(saveAndRestart, "Settings must expose the save-and-restart action.");
            }
        }

        [TestMethod]
        public void SettingsFormPreservesUnknownConfiguredLanguage()
        {
            Config config = new Config();
            config.WorkingDir = Path.GetTempPath();
            config.Language = "fr";
            config.InstallTag = "latest";

            using (SettingsForm form = new SettingsForm(config))
            {
                List<ComboBox> combos = FindControls<ComboBox>(form);
                Assert.AreEqual(1, combos.Count);
                Assert.IsNotNull(combos[0].SelectedItem);
                object selected = combos[0].SelectedItem;
                FieldInfo value = selected.GetType().GetField("Value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(value, "Language options must expose a selectable value.");
                Assert.AreEqual("fr", Convert.ToString(value.GetValue(selected)),
                    "Saving Settings must not silently replace an installed/configured language.");
            }
        }

        [TestMethod]
        public void SettingsSaveAndRestartRequestsRestart()
        {
            string ini = Path.Combine(Path.GetTempPath(),
                "pi-web-tray-settings-" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                Config config = new Config();
                config.IniPath = ini;
                config.OutputDir = Path.GetTempPath();
                config.WorkingDir = Path.GetTempPath();
                config.Port = 30141;
                config.Language = L.Auto;
                config.InstallTag = "latest";

                using (SettingsForm form = new SettingsForm(config, false))
                {
                    Button restart = null;
                    foreach (Button button in FindControls<Button>(form))
                    {
                        if (button.Text == L.T("settings.saveRestart")) restart = button;
                    }
                    Assert.IsNotNull(restart, "Settings must expose the save-and-restart action.");
                    MethodInfo save = typeof(SettingsForm).GetMethod("SaveSettings",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(save, "Settings must expose the save implementation.");
                    Assert.AreEqual(true, save.Invoke(form, new object[] { true }),
                        "The save-and-restart path must save successfully.");
                    Assert.IsTrue(form.RestartRequested,
                        "Save and restart must ask the tray to restart.");
                    Assert.AreEqual(DialogResult.OK, form.DialogResult);
                }
            }
            finally
            {
                try { File.Delete(ini); } catch (Exception) { }
            }
        }

        [TestMethod]
        public void CheckFormLayoutFitsContent()
        {
            Config config = new Config();
            config.IniPath = "pi-web-tray.ini";
            config.OutputDir = ".";
            config.WorkingDir = ".";
            config.LogFile = "server.log";
            config.InstallLogFile = "install.log";
            config.UpdateLogFile = "update.log";
            ProbeResult probe = new ProbeResult();

            using (CheckForm form = new CheckForm(config, probe, false))
            {
                form.CreateControl();
                form.FitForDisplay();
                foreach (DataGridView table in FindControls<DataGridView>(form))
                {
                    Assert.IsTrue(table.Height > 0, "A report table must have a visible height.");
                    Assert.IsTrue(table.Parent != null, "A report table must live inside a group.");
                    Assert.IsTrue(table.Parent.Bottom <= form.ClientSize.Height + 1,
                        "A report table must remain inside the client area.");
                }

                // The software table occupies the Percent row; growing the form must
                // grow the table instead of leaving an empty row below it.
                form.ClientSize = new Size(1000, 800);
                form.PerformLayout();
                bool softwareTableGrew = false;
                foreach (DataGridView table in FindControls<DataGridView>(form))
                {
                    if (table.Columns.Count == 2 && table.Rows.Count == 10
                        && table.Height > 150)
                    {
                        softwareTableGrew = true;
                    }
                }
                Assert.IsTrue(softwareTableGrew,
                    "The software table must fill the remaining vertical space.");
                foreach (Button button in FindControls<Button>(form))
                {
                    Assert.IsTrue(button.Parent != null, "A button must have a parent.");
                    Assert.IsTrue(button.Parent.Bottom <= form.ClientSize.Height + 1,
                        "The button row must remain inside the client area.");
                }
            }
        }

        [TestMethod]
        public void UpdateFormCanBeConstructed()
        {
            using (UpdateForm form = new UpdateForm(null))
            {
                Assert.AreEqual(L.T("update.progressTitle"), form.Text);
                AssertDpiScaling(form);

                List<ProgressBar> bars = FindControls<ProgressBar>(form);
                Assert.AreEqual(1, bars.Count);
                Assert.AreEqual(ProgressBarStyle.Continuous, bars[0].Style,
                    "The update bar must not loop forever like a marquee.");
                form.SetState("download", 50, true);
                Assert.AreEqual(50, bars[0].Value);
                form.SetState("apply", -1, true);
                Assert.AreEqual(50, bars[0].Value,
                    "Unknown progress must preserve the last known download value.");
            }
        }

        [TestMethod]
        public void InstallFormCanBeConstructed()
        {
            using (InstallForm form = new InstallForm())
            {
                Assert.AreEqual(L.T("install.progressTitle"), form.Text);
                AssertDpiScaling(form);
            }
        }

        [TestMethod]
        public void LogFilterHandlesTextAndBraces()
        {
            string filtered = LogForm.FilterLines(
                "line one" + Environment.NewLine + "line two" + Environment.NewLine + "line three",
                "two");
            Assert.AreEqual("line two", filtered);

            string noMatch = LogForm.FilterLines("line one", "{");
            Assert.AreEqual(L.T("log.noMatches", "{"), noMatch);
        }

        [TestMethod]
        public void LogFormCanBeConstructed()
        {
            string path = Path.Combine(Path.GetTempPath(), "pi-web-tray-log-form-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                File.WriteAllText(path, "line one" + Environment.NewLine);
                using (LogForm form = new LogForm(path, "log"))
                {
                    Assert.AreEqual("log", form.Text);
                    Assert.AreEqual(2, FindControls<TextBox>(form).Count);
                    Assert.AreEqual(L.T("log.refresh"), ((Button)form.AcceptButton).Text,
                        "Enter in the filter box must refresh, not close the viewer.");
                    AssertDpiScaling(form);
                }
            }
            finally
            {
                try { File.Delete(path); } catch (Exception) { }
            }
        }

        private static void AssertDpiScaling(Form form)
        {
            Assert.AreEqual(AutoScaleMode.Dpi, form.AutoScaleMode,
                "Forms must scale with the real DPI, not a font baseline.");
            Assert.AreEqual(form.CurrentAutoScaleDimensions, form.AutoScaleDimensions,
                "DPI scaling must be armed after all controls are created.");
        }

        private static List<T> FindControls<T>(Control parent) where T : Control
        {
            List<T> controls = new List<T>();
            foreach (Control child in parent.Controls)
            {
                if (child is T) controls.Add((T)child);
                controls.AddRange(FindControls<T>(child));
            }
            return controls;
        }
    }
}
