using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// One arcade streaming session's link measurement, mirrored from the CloudRetro worker at room close
    /// (Phase 0 of the ABR quality plan, <c>docs/arcade-abr-quality-plan.md</c>).
    ///
    /// <para>Why this table exists: the worker already knows, by the end of every room, the rate a peer's
    /// link actually sustained — and then forgets it. So every room re-certifies from a cold 6000 kbps
    /// opener the same capacity it certified yesterday on the same link, which is the 17–35 second ramp
    /// that reads as "low-quality YouTube" at the start of every session. These rows are that memory.</para>
    ///
    /// <para>Two distinct jobs. Immediately (Phase 0) they make a complaint attributable: the ramp-tax
    /// population (high <see cref="RampTicks"/>, high <see cref="AtCeilPct"/>, <see cref="CutsSteady"/> ≈ 0)
    /// separates automatically from the genuine-wireless population (low <see cref="SustainedKbps"/>,
    /// steady-state starves, elevated <see cref="RttMeanMs"/>/<see cref="RttSdMs"/>) — and no ABR change
    /// fixes the second kind. Later (Phase 1) <see cref="SustainedKbps"/> becomes the warm-start value a
    /// returning room opens at.</para>
    ///
    /// <para>Keyed on <see cref="UserId"/> + <see cref="DeviceId"/> and never on user alone: one person's
    /// wired desktop, tablet and phone must never share link history, because a warm value learned on the
    /// desktop and applied to the Wi-Fi tablet is exactly the collapse the conservative opener exists to
    /// prevent. <see cref="Path"/> is a second axis that must never be mixed across — a relay session's
    /// history speaks only for relay sessions. Same-host sessions are never stored at all (they measure our
    /// own encoder and CPU, not a link), so the worker drops those rows before POSTing.</para>
    ///
    /// <para>Written only by <c>/API/Arcade/Internal/LinkStat</c>, the secret-gated worker→site callback
    /// (sibling of the RetroAchievements mirror). Purely observational: nothing reads these rows yet.</para>
    /// </summary>
    [Table("ArcadeLinkStat")]
    public class ArcadeLinkStat
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The site user this peer authenticated as. Resolved server-side from the username the
        /// worker relays — a row that does not resolve to a real user is dropped rather than stored, since
        /// an unattributable measurement can never be looked up again. Plain id with no navigation/FK,
        /// matching <see cref="ArcadeLeaderboardEntry"/> and <see cref="ArcadeAchievementUnlock"/>.</summary>
        public int UserId { get; set; }

        /// <summary>Opaque per-device key: a GUID the site frontend mints once into <c>localStorage</c>
        /// (<c>arcade.deviceId</c>) and sends with every room create/join. Never parsed — only grouped by.
        /// Sanitised at both ends to <c>[A-Za-z0-9-]</c> and length-capped.</summary>
        [MaxLength(64)]
        public string DeviceId { get; set; } = default!;

        /// <summary>Arcade system key the room ran (<c>n64</c>, <c>ps2</c>, <c>capture</c>, …). Part of the
        /// context a stored rate is only valid within — a ceiling derived for one system's frame says
        /// little about another's.</summary>
        [MaxLength(40)]
        public string? System { get; set; }

        /// <summary>Video codec the room negotiated (<c>av1</c>/<c>h264</c>). A warm value must never cross
        /// a codec boundary: the two temporal-SVC ladders differ enough (base share 28% vs 80%) that a rate
        /// proven on one does not transfer to the other.</summary>
        [MaxLength(20)]
        public string? Codec { get; set; }

        /// <summary>The room's bitrate CEILING — a permission, not a target. Explicit lobby pick, or derived
        /// per-frame for "Auto".</summary>
        public int CeilingKbps { get; set; }

        /// <summary>The rate the room OPENED at (today <c>min(6000, ceiling × 60%)</c>). The number warm
        /// start is intended to replace.</summary>
        public int OpenKbps { get; set; }

        /// <summary>THE warm-start datum: the highest room rate that survived at least 5 consecutive healthy
        /// ticks while this peer was the binding estimator. See the worker's <c>abrPeerStat</c> for the exact
        /// definition.</summary>
        public int SustainedKbps { get; set; }

        /// <summary>Ticks (= seconds) from room open to the first tick at ≥90% of the ceiling.
        /// NULL means the room never got there, which is a genuinely different — and more interesting —
        /// outcome than reaching it immediately, so it is not collapsed onto a sentinel.</summary>
        public int? RampTicks { get; set; }

        /// <summary>Percent of ticks the room spent at ≥90% of its ceiling. High alongside a high
        /// <see cref="RampTicks"/> is the ramp-tax signature.</summary>
        public int AtCeilPct { get; set; }

        /// <summary>Confirmed bitrate cuts AFTER the room first reached its ceiling. Deliberately excludes
        /// cuts during the climb: those are the probe finding the ceiling and reading them as faults is how
        /// a healthy ramp gets misdiagnosed as thrashing.</summary>
        public int CutsSteady { get; set; }

        /// <summary>Confirmed starve episodes after the first ceiling touch. Same ramp/steady discipline as
        /// <see cref="CutsSteady"/>.</summary>
        public int StarvesSteady { get; set; }

        /// <summary>Distinct congestion-memory episodes (a remembered wall being learned from nothing).</summary>
        public int CongEpisodes { get; set; }

        /// <summary>Mean ICE round-trip time over the session, milliseconds. The discriminator between a
        /// wired desktop and a Wi-Fi tablet on the same subnet (measured: 0.91 ms vs 3.86 ms), which address
        /// and bandwidth estimate both fail to separate.</summary>
        public double RttMeanMs { get; set; }

        /// <summary>Standard deviation of the ICE RTT samples. The VARIANCE matters more than the mean: a
        /// radio under load jitters long before its average latency looks bad.</summary>
        public double RttSdMs { get; set; }

        /// <summary>ICE path class: <c>direct</c> or <c>relay</c>. (<c>samehost</c> is never stored — those
        /// sessions bypass ABR and measure our own hardware.) A stored rate is only valid within its class.</summary>
        [MaxLength(20)]
        public string? Path { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
