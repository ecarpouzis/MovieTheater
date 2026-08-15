using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MovieTheater.Services.Jellyfin
{
    /// <summary>
    /// The last mile of a sync: turning classified candidates into finished review cards.
    ///
    /// <para>Declared here, implemented in the web project. The sync job lives in this assembly but
    /// the resolution needs the title cascade, the normalizers and the poster store, which do not —
    /// so the job asks for this by interface and simply skips the step when nothing is registered
    /// (a CLI sync stays a sync). Inverting it this way is what lets one operation finish the whole
    /// job server-side instead of leaving the queue half-built behind a manual button.</para>
    /// </summary>
    public interface ISyncCandidateResolver
    {
        /// <summary>
        /// Drives new-title and series resolution to completion, chunk by chunk, reporting progress.
        /// Bounded and resumable: each chunk's result is durable, so a run that dies part-way
        /// continues rather than restarting.
        /// </summary>
        Task<SyncResolveSummary> ResolveAllAsync(Action<string>? progress, CancellationToken cancel);
    }

    /// <summary>What a full resolution pass accomplished, for the sync report.</summary>
    public class SyncResolveSummary
    {
        public int MoviesCreated { get; set; }
        public int MoviesConvertedToUpgrade { get; set; }
        public int SeriesIdentified { get; set; }
        public int SeriesEnriched { get; set; }
        public int EpisodesCatalogued { get; set; }
        public int EpisodeFilesMapped { get; set; }
        /// <summary>Folders left for a person — a lookup that found nothing, a numbering
        /// disagreement, a duplicate. Not failures of the run; decisions it declined to make.</summary>
        public int NeedsAttention { get; set; }
        public List<string> Notes { get; } = new();

        public bool DidAnything =>
            MoviesCreated + MoviesConvertedToUpgrade + SeriesIdentified + SeriesEnriched
            + EpisodesCatalogued + EpisodeFilesMapped > 0;
    }
}
