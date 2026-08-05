using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Music
{
    /// <summary>
    /// "Fetch one album's art from the internet and put it on this process's images mount" — the single
    /// step shared by the lazy <see cref="Controllers.MusicImageController"/> path and the bulk
    /// <c>/API/Admin/Music/BackfillArt</c> warm (music-plan.md §2.5).
    ///
    /// <para>It lives here because <b>only the process that owns the mount can fill it</b>: prod's images
    /// volume isn't reachable from a dev box, so the CLI's local pass can never populate prod. Both prod
    /// paths therefore call this, and neither can drift from the other on throttling or on what counts
    /// as a miss.</para>
    /// </summary>
    public static class MusicArtFill
    {
        /// <summary>
        /// Returns true when art is on the mount for this album afterwards.
        ///
        /// <para><paramref name="spaceRemoteCall"/> is awaited immediately before the network lookup and
        /// is how the caller enforces MusicBrainz's ~1 req/s limit; it is passed in rather than baked in
        /// so the lazy path and the bulk warm can share ONE process-wide spacing clock.</para>
        ///
        /// <para>Every outcome stamps <c>ArtCheckedUtc</c> and sets <c>HasArt</c> to whether a file now
        /// exists <i>on this mount</i>. Setting it false on a miss is what lets a driver loop terminate:
        /// an album that the internet has declined drops out of the work set instead of being retried
        /// forever. (The DB is shared with dev, so an album whose art exists only in the dev box's
        /// Posters folder may get flipped false here — dev re-flips it true the next time that art is
        /// viewed there, and prod is the copy that matters.)</para>
        /// </summary>
        public static async Task<bool> FetchAndStoreAsync(
            MovieDb db,
            MovieTheaterConfiguration config,
            HttpClient http,
            MusicAlbum album,
            Func<Task> spaceRemoteCall)
        {
            var imagesDir = MusicArtStore.ResolveDir(config);
            if (imagesDir == null) return false;

            var mainPath = Path.Combine(imagesDir, MusicArtStore.FileName(album.Id, thumbnail: false));
            var thumbPath = Path.Combine(imagesDir, MusicArtStore.FileName(album.Id, thumbnail: true));

            // Never regenerate art already on the mount (project rule) — just make the DB agree.
            if (File.Exists(mainPath))
            {
                if (!album.HasArt || album.DominantColor == null)
                {
                    album.HasArt = true;
                    if (album.DominantColor == null && File.Exists(thumbPath))
                        album.DominantColor = MusicArtStore.ComputeAverageColor(await File.ReadAllBytesAsync(thumbPath));
                    await db.SaveChangesAsync();
                }
                return true;
            }

            await spaceRemoteCall();
            var source = await MusicRemoteArt.FetchAsync(http, album.Artist.Name, album.Title);

            album.ArtCheckedUtc = DateTime.UtcNow;
            if (source == null)
            {
                album.HasArt = false;
                await db.SaveChangesAsync();
                return false;
            }

            var main = MusicArtStore.Downscale(source, MusicArtStore.MainMaxPx);
            var thumb = MusicArtStore.Downscale(source, MusicArtStore.ThumbMaxPx);
            if (main == null || thumb == null)
            {
                // Found bytes but they aren't a decodable image — treat exactly like a miss.
                album.HasArt = false;
                await db.SaveChangesAsync();
                return false;
            }

            try
            {
                Directory.CreateDirectory(imagesDir);
                await File.WriteAllBytesAsync(mainPath, main);
                await File.WriteAllBytesAsync(thumbPath, thumb);
            }
            catch (IOException)
            {
                // Mount unwritable — don't claim art we couldn't store, and let a later run retry.
                album.ArtCheckedUtc = null;
                return false;
            }

            album.HasArt = true;
            album.DominantColor = MusicArtStore.ComputeAverageColor(thumb);
            await db.SaveChangesAsync();
            return true;
        }
    }
}
