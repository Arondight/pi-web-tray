using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
                AssertDpiScaling(form);

                List<Button> buttons = FindControls<Button>(form);
                Assert.AreEqual(1, buttons.Count, "About window should only contain the close button.");

                List<LinkLabel> links = FindControls<LinkLabel>(form);
                Assert.AreEqual(2, links.Count, "About window should contain the description and the project link.");

                LinkLabel description = null;
                LinkLabel project = null;
                foreach (LinkLabel link in links)
                {
                    if (link.Links.Count == 2) description = link;
                    else if (link.Links.Count == 1) project = link;
                }
                Assert.IsNotNull(description, "The description should link Pi and Pi Web.");
                Assert.IsNotNull(project, "The project URL should be a separate link.");
                Assert.AreEqual(SystemColors.HotTrack, description.LinkColor,
                    "The description must use a high-contrast-aware link colour.");
                List<string> urls = new List<string>();
                foreach (LinkLabel.Link link in description.Links)
                    urls.Add(link.LinkData as string);
                Assert.IsTrue(urls.Contains("https://github.com/agegr/pi-web"),
                    "The description should link Pi Web.");
                Assert.IsTrue(urls.Contains("https://github.com/earendil-works/pi"),
                    "The description should link Pi.");

                LinkArea area = project.LinkArea;
                string text = project.Text;
                Assert.AreEqual(L.T("about.link", "").Length, area.Start,
                    "The project label must not be part of the link.");
                Assert.AreEqual(Program.ProjectUrl, text.Substring(area.Start, area.Length),
                    "Only the project URL should be a link.");
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
                    form.Show();
                    Application.DoEvents();
                    Button restart = null;
                    foreach (Button button in FindControls<Button>(form))
                    {
                        if (button.Text == L.T("settings.saveRestart")) restart = button;
                    }
                    Assert.IsNotNull(restart, "Settings must expose the save-and-restart action.");
                    restart.PerformClick();
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
                form.Show();
                Application.DoEvents();
                try
                {
                    foreach (DataGridView table in FindControls<DataGridView>(form))
                    {
                        Assert.IsTrue(table.Height > 0, "A report table must have a visible height.");
                        Assert.IsTrue(table.Parent != null, "A report table must live inside a group.");
                        Assert.IsTrue(table.Parent.Bottom <= form.ClientSize.Height + 1,
                            "A report table must remain inside the client area.");
                    }
                    foreach (Button button in FindControls<Button>(form))
                    {
                        Assert.IsTrue(button.Parent != null, "A button must have a parent.");
                        Assert.IsTrue(button.Parent.Bottom <= form.ClientSize.Height + 1,
                            "The button row must remain inside the client area.");
                    }
                }
                finally
                {
                    form.Close();
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
