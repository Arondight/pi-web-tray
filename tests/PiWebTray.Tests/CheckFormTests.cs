using System;
using System.Collections.Generic;
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
                    if (table.Columns.Count == 2 && table.Rows.Count == 9) softwareTable = true;
                }
                Assert.IsTrue(runtimeTable, "Runtime components table has an unexpected shape.");
                Assert.IsTrue(softwareTable, "Application information table has an unexpected shape.");
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
