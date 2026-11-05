// Pi Web Tray — Node.js presence state
//
// Pure state machine used by the background fingerprint watcher. A missing node.exe is
// confirmed over several samples so an installer can replace it without a false warning.

namespace PiWebTray
{
    internal sealed class NodePresenceState
    {
        private const int MissingSamples = 2;

        internal bool Known { get; private set; }
        internal bool Present { get; private set; }
        private int _missingSamples;

        // Returns true only when the observable state changed (present -> missing or
        // missing -> present). The initial sample establishes the baseline silently.
        internal bool Update(string signature)
        {
            bool present = !string.IsNullOrEmpty(signature);
            if (!Known)
            {
                Known = true;
                Present = present;
                _missingSamples = 0;
                return false;
            }

            if (present)
            {
                _missingSamples = 0;
                if (Present) return false;
                Present = true;
                return true;
            }

            if (!Present) return false;
            _missingSamples++;
            if (_missingSamples < MissingSamples) return false;
            _missingSamples = 0;
            Present = false;
            return true;
        }
    }
}
