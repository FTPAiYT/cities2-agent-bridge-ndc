using System;
using System.IO;

namespace CitiesIIAgentBridge
{
    // Independent of Unity so recovery can be tested with real Windows file locks.
    internal static class BridgeTick
    {
        internal static bool Run(Action enforceStop, Action simulationTick, Action publish,
            Action pump, Action workflowTick, Action<IOException> contention)
        {
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
                // Defer new work until the next tick. Permission is never changed here.
                // Mailbox retains dispatched results, so publication retries cannot replay actions.
                contention(e);
                return false;
            }
        }
    }
}
