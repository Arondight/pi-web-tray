// Pi Web Tray — cancel an in-flight update request
//
// A generation counter already makes late worker results harmless, but without
// aborting the HttpWebRequest a cancelled download keeps using bandwidth and keeps
// the staging file open until the timeout. This small token closes that gap.

using System;
using System.Net;

namespace PiWebTray
{
    internal sealed class UpdateCancellation
    {
        private readonly object _lock = new object();
        private HttpWebRequest _request;
        private bool _cancelled;

        internal bool IsCancelled
        {
            get
            {
                lock (_lock) return _cancelled;
            }
        }

        internal void Register(HttpWebRequest request)
        {
            if (request == null) return;
            lock (_lock)
            {
                if (_cancelled)
                {
                    try { request.Abort(); } catch (Exception) { }
                    return;
                }
                _request = request;
            }
        }

        internal void Release(HttpWebRequest request)
        {
            if (request == null) return;
            lock (_lock)
            {
                if (ReferenceEquals(_request, request)) _request = null;
            }
        }

        internal void Cancel()
        {
            HttpWebRequest request;
            lock (_lock)
            {
                if (_cancelled) return;
                _cancelled = true;
                request = _request;
                _request = null;
            }

            if (request != null)
            {
                try { request.Abort(); } catch (Exception) { }
            }
        }
    }
}
