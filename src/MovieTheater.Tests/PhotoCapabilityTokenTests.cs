using System;
using System.IO;
using MovieTheater.Core;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// The photo data plane's capability (docs/photos-plan.md §2.2). Unlike movie posters, which
    /// /Image serves openly, every family photo pixel needs one of these — so the interesting tests are
    /// all about what it must REFUSE.
    /// </summary>
    public class PhotoCapabilityTokenTests
    {
        private const string Secret = "photo-token-secret-for-tests";

        private static PhotoCapabilityToken.Payload Valid(string relativePath = "Album/photo.jpg") =>
            new PhotoCapabilityToken.Payload(7, 42, relativePath, PhotoStreamRoutes.SizeGrid,
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());

        [Fact]
        public void A_minted_token_round_trips()
        {
            var token = PhotoCapabilityToken.Mint(Secret, Valid());
            Assert.True(PhotoCapabilityToken.TryValidate(Secret, token, out var payload));
            Assert.NotNull(payload);
            Assert.Equal(7, payload!.UserId);
            Assert.Equal(42, payload.AssetId);
            Assert.Equal("Album/photo.jpg", payload.RelativePath);
            Assert.Equal(PhotoStreamRoutes.SizeGrid, payload.Size);
        }

        [Fact]
        public void A_tampered_payload_is_refused()
        {
            var token = PhotoCapabilityToken.Mint(Secret, Valid());
            var dot = token.IndexOf('.');
            // Flip one character of the payload half; the signature no longer covers it.
            var payloadHalf = token[..dot].ToCharArray();
            payloadHalf[0] = payloadHalf[0] == 'A' ? 'B' : 'A';
            var tampered = new string(payloadHalf) + token[dot..];

            Assert.False(PhotoCapabilityToken.TryValidate(Secret, tampered, out var parsed));
            Assert.Null(parsed);
        }

        [Fact]
        public void A_token_from_another_secret_is_refused()
        {
            var token = PhotoCapabilityToken.Mint("some-other-secret", Valid());
            Assert.False(PhotoCapabilityToken.TryValidate(Secret, token, out _));
        }

        [Fact]
        public void An_expired_token_is_refused()
        {
            var expired = new PhotoCapabilityToken.Payload(7, 42, "Album/photo.jpg",
                PhotoStreamRoutes.SizeView, DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds());
            Assert.False(PhotoCapabilityToken.TryValidate(Secret, PhotoCapabilityToken.Mint(Secret, expired), out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("no-dot")]
        [InlineData(".")]
        [InlineData("abc.")]
        [InlineData("!!!.!!!")]
        public void Malformed_tokens_are_refused_without_throwing(string token)
        {
            Assert.False(PhotoCapabilityToken.TryValidate(Secret, token, out _));
        }

        [Fact]
        public void A_payload_with_extra_delimiters_is_refused_rather_than_reinterpreted()
        {
            // A '|' cannot occur in a Windows file name, so this can only come from an attempt to
            // shift the fields. The count check must reject it rather than parse a five-field prefix.
            var token = PhotoCapabilityToken.Mint(Secret, Valid("Album/ph|oto.jpg"));
            Assert.False(PhotoCapabilityToken.TryValidate(Secret, token, out _));
        }

        // ── Confinement: the half of the boundary a valid signature does NOT cover ───────────────

        [Theory]
        [InlineData("../secrets.jpg")]
        [InlineData("Album/../../secrets.jpg")]
        [InlineData("..\\secrets.jpg")]
        [InlineData("/etc/passwd")]
        [InlineData("")]
        public void Traversal_out_of_the_root_resolves_to_nothing(string relativePath)
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "photo-root"));
            Assert.Null(PhotoPathConfinement.Resolve(root, relativePath));
        }

        [Fact]
        public void An_absolute_windows_path_cannot_replace_the_root()
        {
            // Path.Combine DISCARDS its first argument when the second is rooted — the trap this check
            // exists for. Only meaningful where drive letters are a thing.
            if (Path.DirectorySeparatorChar != '\\') return;
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "photo-root"));
            Assert.Null(PhotoPathConfinement.Resolve(root, "C:/Windows/win.ini"));
        }

        [Fact]
        public void A_path_inside_the_root_resolves()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "photo-root"));
            var resolved = PhotoPathConfinement.Resolve(root, "Album/2004/photo.jpg");
            Assert.NotNull(resolved);
            Assert.StartsWith(root, resolved!, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("photo.jpg", resolved);
        }

        [Fact]
        public void A_sibling_directory_sharing_the_roots_prefix_is_not_inside_it()
        {
            // "photo-root-backup" starts with "photo-root" as a STRING but is a different directory;
            // the boundary check appends the separator for exactly this reason.
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "photo-root"));
            Assert.Null(PhotoPathConfinement.Resolve(root, "../photo-root-backup/photo.jpg"));
        }

        // ── "Is this root configured?" — the startup half of the same boundary ───────────────────

        /// <summary>
        /// The DEPLOY-fatal case: a host takes the appsettings that now carries <c>"PhotoRootDir": null</c>
        /// but configures no photo collection. ASP.NET's configuration binder answers a JSON null with the
        /// EMPTY STRING, so the natural <c>is string</c> / <c>!= null</c> spellings match it and
        /// <see cref="Path.GetFullPath(string)"/> throws — during startup, killing the whole gateway and
        /// taking movies and music down with photos. Unconfigured must mean null OR blank.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void An_unconfigured_root_is_null_rather_than_a_startup_crash(string? configured)
        {
            Assert.Null(ConfiguredRoot.FullPathOrNull(configured));
        }

        [Fact]
        public void A_configured_root_is_made_absolute()
        {
            var root = Path.Combine(Path.GetTempPath(), "photo-root");
            var resolved = ConfiguredRoot.FullPathOrNull("  " + root + "  ");
            Assert.Equal(Path.GetFullPath(root), resolved);
        }
    }
}
