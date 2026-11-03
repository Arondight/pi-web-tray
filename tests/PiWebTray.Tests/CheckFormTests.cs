using System;
using System.Collections.Generic;
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
            }
        }

        [TestMethod]
        public void AboutFormCanBeConstructed()
        {
            using (AboutForm form = new AboutForm())
            {
                Assert.AreEqual(L.T("about.title"), form.Text);
                Assert.IsTrue(form.Controls.Count > 0);
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
                Assert.IsTrue(form.MinimumSize.Width > 0);
            }
        }

        [TestMethod]
        public void UpdateFormCanBeConstructed()
        {
            using (UpdateForm form = new UpdateForm(null))
            {
                Assert.AreEqual(L.T("update.progressTitle"), form.Text);
            }
        }

        [TestMethod]
        public void InstallFormCanBeConstructed()
        {
            using (InstallForm form = new InstallForm())
            {
                Assert.AreEqual(L.T("install.progressTitle"), form.Text);
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
                }
            }
            finally
            {
                try { File.Delete(path); } catch (Exception) { }
            }
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
