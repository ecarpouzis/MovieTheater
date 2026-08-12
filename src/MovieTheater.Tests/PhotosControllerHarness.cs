using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using MovieTheater.Controllers;
using MovieTheater.Db;
using MovieTheater.Services;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Builds a <see cref="PhotosController"/> over the test fixture's SQLite database and reads its
    /// answers as JSON.
    ///
    /// <para>The controller is exercised DIRECTLY rather than through a host: the family gate is already
    /// proven end to end against the real authorization middleware in <c>FamilyAlbumGateTests</c>, and
    /// re-hosting it here would test ASP.NET twice while making the curation assertions harder to read.
    /// What these tests are for is what the actions DO once the gate has let someone in.</para>
    ///
    /// <para>Answers are compared as serialized JSON — the actual wire shape the SPA consumes — so a
    /// renamed field fails a test instead of quietly becoming <c>undefined</c> in the browser.</para>
    /// </summary>
    internal static class PhotosControllerHarness
    {
        public const string AdminUser = "operator";
        public const string MemberUser = "member";
        public const int MemberUserId = 7;

        /// <param name="playback">
        /// The video-minting seam (docs/photos-plan.md §2.3). Null is the ordinary case — a controller
        /// built without it behaves like a host with no media server, which is what every test that is
        /// about rows rather than streaming wants.
        /// </param>
        /// <param name="dataPlane">
        /// Configures a (fake) gateway base URL and token secret so the actions actually MINT capability
        /// URLs. Off by default — most tests are about rows and flags, and an unconfigured data plane
        /// simply means the cards carry no image URLs. It has to be switchable, though: a test asserting
        /// that some URL is absent passes vacuously against a controller that could not have minted one.
        /// </param>
        public static PhotosController Build(PhotoIngestFixture fixture, MovieDb db, bool admin = false,
            int userId = MemberUserId, MovieTheater.Photos.IPhotoVideoPlayback? playback = null,
            bool dataPlane = false)
        {
            // Built from an empty in-memory source: MovieTheaterConfiguration binds itself from
            // IConfiguration, and a test must never reach the real appsettings — that file's connection
            // string is the live shared production database.
            var config = new MovieTheaterConfiguration(new ConfigurationBuilder().Build())
            {
                PhotosReportDir = fixture.ReportDir,
                PhotosLibraryDir = fixture.Root,
                PhotosThumbCacheDir = fixture.ThumbCache,
                // Deliberately no StreamGatewayBaseUrl/StreamTokenSecret by default: these tests are
                // about rows and flags, and an unconfigured data plane simply means the cards carry no
                // image URLs. A host that resolves to nothing is never contacted either way.
                StreamGatewayBaseUrl = dataPlane ? "https://gateway.invalid" : null,
                StreamTokenSecret = dataPlane ? "harness-token-secret" : null,
                AdminUsernames = new List<string> { AdminUser },
            };

            // A session implies a user row: albums record who created them, and the FK is Restrict
            // precisely so curation outlives account housekeeping (§2.11).
            if (!db.Users.Any(u => u.UserID == userId))
            {
                db.Users.Add(new User { UserID = userId, Username = admin ? AdminUser : MemberUser });
                db.SaveChanges();
            }

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, admin ? AdminUser : MemberUser),
                // The §3 Phase 0 addendum: a password-verified session. Being an admin additionally
                // requires the username to be in the configured list, which is the other half above.
                new Claim("amr", "pwd"),
            }, "TestScheme");

            return new PhotosController(db, config, playback)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
                },
            };
        }

        /// <summary>The action's body as JSON. Throws on anything that is not a JSON result, which is
        /// itself the assertion for "this should have succeeded".</summary>
        public static JsonElement Body(IActionResult result)
        {
            if (result is JsonResult json) return JsonSerializer.SerializeToElement(json.Value);
            throw new InvalidOperationException($"Expected a JsonResult, got {result.GetType().Name}.");
        }

        public static int Int(JsonElement body, string property) => body.GetProperty(property).GetInt32();

        /// <summary>The asset ids in an <c>items</c> array whose elements carry a card (timeline, folder,
        /// album and proposal pages all share the shape).</summary>
        public static List<int> ItemIds(JsonElement body)
        {
            var ids = new List<int>();
            foreach (var item in body.GetProperty("items").EnumerateArray())
            {
                if (item.TryGetProperty("id", out var direct)) ids.Add(direct.GetInt32());
                else if (item.TryGetProperty("card", out var card)) ids.Add(card.GetProperty("id").GetInt32());
            }
            return ids;
        }
    }
}
