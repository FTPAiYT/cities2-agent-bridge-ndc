using System;
using System.IO;

namespace CitiesIIAgentBridge
{
    // Kept independent of Unity so fault routing can be exercised with real file locks.
    internal static class BridgeTick
    {
        internal static bool Run(Action enforceStop, Action simulationTick, Action publish,
            Action pump, Action workflowTick, Action<IOException> contention)
        {
            // A blocked heartbeat must never postpone a stop or a simulation deadline.
            enforceStop();
            simulationTick();
            if (!Communicate(publish, contention)) return false;
            if (!Communicate(pump, contention)) return false;
            workflowTick();
            return true;
        }

        private static bool Communicate(Action action, Action<IOException> contention)
        {
            try { action(); return true; }
            catch (IOException e) when (Mailbox.IsSharingViolation(e))
            {
                // Skip new work this tick. Permission is untouched; next tick retries only
                // transport. Mailbox retains already-dispatched results until published.
                contention(e);
                return false;
            }
        }
    }
}
