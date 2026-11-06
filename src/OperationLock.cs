// Pi Web Tray — cross-process operation locks
//
// Install and update operations touch shared state (the global npm prefix, the
// installation directory and its backup folders). The tray and the CLI can otherwise
// run the same operation at the same time. These locks are intentionally short-lived
// and non-blocking: a second request fails with a clear message instead of silently
// starting a competing npm or file-copy operation.

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PiWebTray
{
    internal sealed class OperationLock : IDisposable
    {
        // Global\ is deliberate: two sessions (or two users updating the same
        // machine-wide installation) must not run these operations concurrently.
        // A different user may receive access denied, in which case TryAcquire
        // fails closed instead of allowing a competing install/update.
        internal const string InstallName = @"Global\PiWebTray.Install";

        private readonly Mutex _mutex;
        private bool _owned;

        private OperationLock(Mutex mutex, bool owned)
        {
            _mutex = mutex;
            _owned = owned;
        }

        // The update lock is scoped to the installation directory, not to one tray
        // instance. Two instances that share an executable must not replace it at the
        // same time, and a new helper must not race an older helper that is still alive.
        internal static string UpdateName(string target)
        {
            string normalized = (target ?? "").TrimEnd('\\', '/').ToLowerInvariant();
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            }

            StringBuilder sb = new StringBuilder(@"Global\PiWebTray.Update.");
            for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        // Returns null when another process or thread owns the lock. An abandoned
        // mutex means the previous owner died and the lock is now ours.
        internal static OperationLock TryAcquire(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, name);
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        mutex.Close();
                        mutex = null;
                        return null;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The lock was acquired by the exception.
                }
                return new OperationLock(mutex, true);
            }
            catch (Exception)
            {
                if (mutex != null)
                {
                    try { mutex.Close(); } catch (Exception) { }
                }
                return null;
            }
        }

        public void Dispose()
        {
            if (_owned)
            {
                _owned = false;
                try { _mutex.ReleaseMutex(); } catch (Exception) { }
            }
            try { _mutex.Close(); } catch (Exception) { }
        }
    }
}
