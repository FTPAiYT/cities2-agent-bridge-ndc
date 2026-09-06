using System;

namespace CitiesIIAgentBridge
{
    // No game dependencies: deadlines and stall detection can be tested with a fake clock.
    internal sealed class SimulationWindow
    {
        internal readonly ulong StartFrame, TargetFrames;
        internal readonly double WallSeconds, StallSeconds;
        private ulong lastFrame;
        private double lastAdvance;
        internal SimulationWindow(ulong frame, ulong frames, double wall, double stall)
        {
            if (frames < 1 || frames > 262144 || double.IsNaN(wall) || double.IsNaN(stall) || wall < 1 || wall > 60 || stall < 1 || stall > wall)
                throw new ArgumentException("invalid_simulation_bounds");
            StartFrame = lastFrame = frame; TargetFrames = frames; WallSeconds = wall; StallSeconds = stall;
        }
        internal string Check(ulong frame, double elapsed, bool allowed, bool sameCity, bool selectedPaused, string eventReason)
        {
            if (!sameCity || frame < StartFrame) return "city_changed";
            if (!allowed) return "control_stopped";
            if (frame != lastFrame) { lastFrame = frame; lastAdvance = elapsed; }
            if (selectedPaused) return "game_paused";
            if (eventReason != null) return eventReason;
            if (frame - StartFrame >= TargetFrames) return "frame_limit";
            if (elapsed >= WallSeconds) return "wall_deadline";
            if (elapsed - lastAdvance >= StallSeconds) return "simulation_stalled";
            return null;
        }
    }
}
