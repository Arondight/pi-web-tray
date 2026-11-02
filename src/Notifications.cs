// Pi Web Tray — notifications
//
// Tray balloons (supported on Windows 7/8/10/11). They never steal focus and do not depend on
// Windows 10 toast packaging, which suits background events like "install started/finished/ready".
//
// NotifyIcon limits: title <= 63 chars, body <= 255 chars; longer values throw.

using System;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class Notifications
    {
        internal static void Show(NotifyIcon tray, string title, string body)
        {
            if (tray == null) return;
            try
            {
                tray.BalloonTipTitle = Shell.Clip(title, 63);
                tray.BalloonTipText = Shell.Clip(body, 255);
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(5000);
            }
            catch (Exception)
            {
            }
        }

        internal static void ShowWarning(NotifyIcon tray, string title, string body)
        {
            if (tray == null) return;
            try
            {
                tray.BalloonTipTitle = Shell.Clip(title, 63);
                tray.BalloonTipText = Shell.Clip(body, 255);
                tray.BalloonTipIcon = ToolTipIcon.Warning;
                tray.ShowBalloonTip(8000);
            }
            catch (Exception)
            {
            }
        }

        internal static void ShowError(NotifyIcon tray, string title, string body)
        {
            if (tray == null) return;
            try
            {
                tray.BalloonTipTitle = Shell.Clip(title, 63);
                tray.BalloonTipText = Shell.Clip(body, 255);
                tray.BalloonTipIcon = ToolTipIcon.Error;
                tray.ShowBalloonTip(8000);
            }
            catch (Exception)
            {
            }
        }
    }
}
