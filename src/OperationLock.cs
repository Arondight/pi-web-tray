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

        // Held while the tray's `pi-web stop` child is running. The CLI install path
        // waits for this lock after the port closes, so npm cannot start while the
        // stop command still holds the package directory. Local\ is enough: only a
        // tray in the current Windows session can serve this instance's port.
        internal static string StopName
        {
            // Keep this distinct from Instance.StopSignalName (Local\PiWebTray.Stop...).
            // The stop lock is a semaphore (see TryAcquireCrossThread), so its name must
            // not collide with the named stop event or with the Mutex-based locks.
            get { return @"Local\PiWebTray.StopLock" + Instance.Suffix; }
        }

        private readonly Mutex _mutex;
        // A Semaphore is not owned by a thread, so a worker may release a lock
        // that was acquired by the UI thread (used by the stop lock).
        private readonly Semaphore _semaphore;
        private bool _owned;

        private OperationLock(Mutex mutex, Semaphore semaphore, bool owned)
        {
            _mutex = mutex;
            _semaphore = semaphore;
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
                return new OperationLock(mutex, null, true);
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

        // The stop lock is acquired on the UI thread and released by the stop worker.
        // Mutex ownership is thread-affine, so the stop lock uses a named semaphore.
        internal static OperationLock TryAcquireCrossThread(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            Semaphore semaphore = null;
            try
            {
                semaphore = new Semaphore(1, 1, name);
                if (!semaphore.WaitOne(0))
                {
                    semaphore.Close();
                    return null;
                }
                return new OperationLock(null, semaphore, true);
            }
            catch (Exception)
            {
                if (semaphore != null)
                {
                    try { semaphore.Close(); } catch (Exception) { }
                }
                return null;
            }
        }

        public void Dispose()
        {
            if (_owned)
            {
                _owned = false;
                if (_semaphore != null)
                {
                    try { _semaphore.Release(); } catch (Exception) { }
                }
                else if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch (Exception) { }
                }
            }
            if (_semaphore != null)
            {
                try { _semaphore.Close(); } catch (Exception) { }
            }
            else if (_mutex != null)
            {
                try { _mutex.Close(); } catch (Exception) { }
            }
        }
    }
}
