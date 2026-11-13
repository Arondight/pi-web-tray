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
            // The stop lock is a cross-thread mutex (see TryAcquireCrossThread), so its
            // name must not collide with the named stop event or the other locks.
            get { return @"Local\PiWebTray.StopLock" + Instance.Suffix; }
        }

        private readonly Mutex _mutex;
        // A mutex is thread-affine. TryAcquireCrossThread keeps ownership on a
        // dedicated holder thread so the UI thread may acquire and a worker may
        // release it later. Unlike a semaphore, the kernel abandons a dead owner
        // and the next TryAcquireCrossThread can recover the lock.
        private readonly ManualResetEvent _release;
        private readonly Thread _holder;
        private bool _owned;

        private OperationLock(Mutex mutex, bool owned, Thread holder, ManualResetEvent release)
        {
            _mutex = mutex;
            _owned = owned;
            _holder = holder;
            _release = release;
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
                return new OperationLock(mutex, true, null, null);
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

        // Non-destructive probe for wait loops. The caller may not own the lock;
        // this only reports whether it can be acquired right now.
        internal static bool IsAvailable(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Mutex ownership is recursive for the owning thread. Testing WaitOne(0)
            // directly on the caller's thread would report a lock held by that same
            // thread as available. Probe from a separate thread so the answer means
            // "another thread/process can acquire it now".
            bool acquired = false;
            Thread probe = new Thread(delegate ()
            {
                Mutex mutex = null;
                try
                {
                    mutex = new Mutex(false, name);
                    try
                    {
                        if (!mutex.WaitOne(0)) return;
                    }
                    catch (AbandonedMutexException)
                    {
                        // Ownership was transferred to this thread.
                    }
                    acquired = true;
                    try { mutex.ReleaseMutex(); } catch (Exception) { }
                }
                catch (Exception)
                {
                }
                finally
                {
                    try { if (mutex != null) mutex.Close(); } catch (Exception) { }
                }
            });
            probe.IsBackground = true;
            probe.Name = "pi-web-tray lock availability";
            try
            {
                probe.Start();
                if (!probe.Join(2000)) return false;
                return acquired;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The stop lock is acquired on the UI thread and released by the stop worker.
        // Mutex ownership is thread-affine, so a dedicated holder thread owns it.
        internal static OperationLock TryAcquireCrossThread(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            Mutex mutex = null;
            ManualResetEvent acquired = null;
            ManualResetEvent release = null;
            Thread holder = null;
            bool got = false;
            try
            {
                mutex = new Mutex(false, name);
                acquired = new ManualResetEvent(false);
                release = new ManualResetEvent(false);
                holder = new Thread(delegate ()
                {
                    try
                    {
                        try
                        {
                            if (!mutex.WaitOne(0)) return;
                        }
                        catch (AbandonedMutexException)
                        {
                            // The previous owner died; ownership was transferred here.
                        }
                        got = true;
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        acquired.Set();
                    }

                    if (!got) return;
                    try { release.WaitOne(); } catch (Exception) { }
                    try { mutex.ReleaseMutex(); } catch (Exception) { }
                });
                holder.IsBackground = true;
                holder.Name = "pi-web-tray cross-thread lock";
                holder.Start();

                if (!acquired.WaitOne(5000) || !got)
                {
                    try { holder.Join(1000); } catch (Exception) { }
                    try { mutex.Close(); } catch (Exception) { }
                    try { acquired.Close(); } catch (Exception) { }
                    try { release.Close(); } catch (Exception) { }
                    return null;
                }
                return new OperationLock(mutex, true, holder, release);
            }
            catch (Exception)
            {
                try { if (holder != null) holder.Join(1000); } catch (Exception) { }
                try { if (mutex != null) mutex.Close(); } catch (Exception) { }
                try { if (acquired != null) acquired.Close(); } catch (Exception) { }
                try { if (release != null) release.Close(); } catch (Exception) { }
                return null;
            }
        }

        public void Dispose()
        {
            if (_owned)
            {
                _owned = false;
                if (_release != null)
                {
                    try { _release.Set(); } catch (Exception) { }
                    try { if (_holder != null) _holder.Join(5000); } catch (Exception) { }
                }
                else if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch (Exception) { }
                }
            }
            try { if (_release != null) _release.Close(); } catch (Exception) { }
            try { if (_mutex != null) _mutex.Close(); } catch (Exception) { }
        }
    }
}
