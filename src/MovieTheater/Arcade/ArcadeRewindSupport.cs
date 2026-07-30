using System;
using System.Collections.Generic;

namespace MovieTheater.Arcade
{
    /// <summary>
    /// Which rooms can offer REWIND — the site's mirror of the worker's per-core <c>rewind: true</c>
    /// arming in <c>docker/arcade/config.worker-gl.yaml</c>.
    ///
    /// <para>It is keyed by the CORE that will actually boot, not by the system, and that distinction is
    /// the whole reason this class exists. Rewind's cost is one <c>retro_serialize</c> per snapshot with
    /// the emulator stopped for its duration, and that cost is a property of the core: the two N64 cores
    /// hold the same 16 MB state and differ 5x — parallel_n64 at 2.42 ms (free) against
    /// mupen64plus_next at 11.61 ms, which measured as an ~8% frame-rate tax on every N64 room whether
    /// or not anyone ever held rewind (measured 2026-07-29, worker `rewind-diag`). So the ring is armed
    /// on one and not the other, and a system-keyed answer could only be wrong for one of them.</para>
    ///
    /// <para>The client used to guess this from the system alone. It can't: a Rewind button on a
    /// mupen room would send t=115 to a worker whose <c>SetRewind</c> is a no-op for an unarmed core —
    /// a control that silently does nothing. The answer travels on the join descriptor instead.</para>
    ///
    /// <para><b>Keep in step with config.worker-gl.yaml.</b> Arming a core there without adding it here
    /// hides a working feature (harmless); adding it here without arming it there shows a dead button
    /// (not harmless). If they ever disagree, the config is the truth — read a real room's
    /// <c>rewind armed</c> line out of the worker log.</para>
    /// </summary>
    public static class ArcadeRewindSupport
    {
        /// <summary>Systems whose DEFAULT core has the ring armed. The serialize-cheap 2D tier — every
        /// one of these is sub-millisecond, which is why the whole tier could be armed at once.</summary>
        private static readonly HashSet<string> DefaultCoreArmed = new(StringComparer.OrdinalIgnoreCase)
        {
            "nes", "snes", "genesis", "gb", "gbc", "gba", "sms", "gg", "sg1000", "segacd", "sega32x",
            "pce", "ngpc", "wsc", "a2600", "a7800", "lynx", "vb", "fds", "neogeo", "arcade",
            "vectrex", "intv", "coleco", "channelf", "o2em", "arcadia", "supervision", "pokemini", "3do",
        };

        /// <summary>"system/coreKey" pairs armed only on an ALTERNATE core — the system's default core is
        /// deliberately not armed, so the room's core is what decides.</summary>
        private static readonly HashSet<string> AlternateCoreArmed = new(StringComparer.OrdinalIgnoreCase)
        {
            "n64/parallel_n64",
        };

        /// <summary>
        /// True if a room on <paramref name="system"/> running <paramref name="coreKey"/> has the
        /// worker's rewind ring armed. <paramref name="coreKey"/> is empty/null for the system's
        /// default core (that is how the save namespace encodes it too — see ArcadeSaveId).
        /// </summary>
        public static bool IsArmed(string? system, string? coreKey)
        {
            if (string.IsNullOrEmpty(system)) return false;
            return string.IsNullOrEmpty(coreKey)
                ? DefaultCoreArmed.Contains(system)
                : AlternateCoreArmed.Contains(system + "/" + coreKey);
        }
    }
}
