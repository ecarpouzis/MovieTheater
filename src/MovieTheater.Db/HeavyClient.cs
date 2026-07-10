using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MovieTheater.Db
{
    /// <summary>
    /// A paired Moonlight/Artemis client device mapped to the site user who paired it
    /// (docs/arcade-heavy-lane-plan.md §7.3). Moonlight identity is a per-client TLS cert + friendly
    /// name and knows nothing of site users; this table is the bridge. The person who paired the
    /// device owns its heavy-lane sessions — save seeding (H4) keys off this mapping, and the lobby's
    /// "In use by …" resolves the Apollo client name through it. Rows are written by the pairing flow
    /// (editor-gated, PIN-completed); the device's Apollo permissions are managed in Apollo's own web
    /// UI on the host, never here.
    /// </summary>
    [Table("HeavyClient")]
    public class HeavyClient
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The friendly device name given at pairing — the join key against what Apollo
        /// reports (SUNSHINE_CLIENT_NAME). Unique: re-pairing the same name re-owns the device.</summary>
        [MaxLength(100)]
        public string ClientName { get; set; } = default!;

        /// <summary>The site user who paired (and therefore owns sessions from) this device.</summary>
        public int UserId { get; set; }

        public DateTime PairedUtc { get; set; }

        public string? Notes { get; set; }
    }
}
