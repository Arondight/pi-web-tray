// Pi Web Tray — notifications
//
// Tray balloons (supported on Windows 7/8.1/10/11). They never steal focus and do not depend on
// Windows 10 toast packaging, which suits background events like "install started/finished/ready".
//
// NotifyIcon limits: title <= 63 chars, body <= 255 chars; longer values throw.

using System;
using System.Windows.Forms;

namespace PiWebTray
{
    internal static class Notifications
    {
        // One-at-a-time queueing is managed by TrayApp; this method reports whether
        // the balloon was actually handed to the shell. A suppressed/failed balloon
        // lets the caller advance to the next queued notification.
        internal static bool Show(NotifyIcon tray, string title, string body,
            ToolTipIcon icon, int timeout)
        {
            if (tray == null) return false;
            try
            {
                tray.BalloonTipTitle = Shell.Clip(title, 63);
                tray.BalloonTipText = Shell.Clip(body, 255);
                tray.BalloonTipIcon = icon;
                tray.ShowBalloonTip(timeout);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

    }
}
