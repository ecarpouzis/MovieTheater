using MovieTheater.Services.Jellyfin;
using Xunit;

namespace MovieTheater.Tests
{
    /// <summary>
    /// Which audio codecs a client may be handed RAW (direct play) — the gate the site enforces itself
    /// because Jellyfin skips its own audio-codec check on a file whose audio track carries no default
    /// flag (measured on 10.11.11, 2026-09-04: with ac3 absent from the mkv profile, a default-flagged
    /// AC-3 track answers AudioCodecNotSupported, an unflagged one answers direct play). 343 library
    /// files have that shape.
    ///
    /// The failure it prevents is invisible everywhere else: the picture plays and the sound is simply
    /// dropped — no media error, no TranscodeReasons, nothing in any server log. A/B'd through the real
    /// Start path in Edge on Ziggy (advertises only aac,flac — Windows 11 Enterprise, no Dolby
    /// decoder): The Scent of Green Papaya, American Pop and The Producers (1968) each direct-played
    /// with 0 audio bytes decoded on the old controller and played HLS with audio on the fixed one.
    /// The household's other Edge DOES decode Dolby and direct-played AC-3 MKVs all August with sound —
    /// so this must follow the client's own flags, never a blanket rule.
    /// </summary>
    public class MatroskaDirectPlayAudioTests
    {
        // The Dolby-capable Edge: MKV probed, ac-3/ec-3 decodable, FLAC decodable.
        private static readonly ClientCapabilities DolbyEdge =
            new(Hevc: true, Fmp4: true, Mp3: true, Ac3: true, Eac3: true, Mkv: true, Flac: true);

        // The Edge that played Green Papaya silent: same browser, no Dolby decoder on the OS.
        private static readonly ClientCapabilities NoDolbyEdge =
            new(Hevc: true, Fmp4: true, Mp3: true, Ac3: false, Eac3: false, Mkv: true, Flac: true);

        [Theory]
        [InlineData("aac")]
        [InlineData("mp3")]
        [InlineData("opus")]
        [InlineData("vorbis")]
        public void UniversalCodecsDirectPlayEverywhere(string codec)
        {
            Assert.True(DolbyEdge.CanDirectPlayAudio(codec));
            Assert.True(NoDolbyEdge.CanDirectPlayAudio(codec));
            Assert.True(ClientCapabilities.H264Baseline.CanDirectPlayAudio(codec));
        }

        // The Green Papaya case: the exact same file direct-plays on one Edge and must not on the other.
        [Theory]
        [InlineData("ac3")]
        [InlineData("eac3")]
        public void DolbyFollowsTheClientsOwnFlags(string codec)
        {
            Assert.True(DolbyEdge.CanDirectPlayAudio(codec));
            Assert.False(NoDolbyEdge.CanDirectPlayAudio(codec));
        }

        [Fact]
        public void Ac3AndEac3AreGatedSeparately()
        {
            var ac3Only = NoDolbyEdge with { Ac3 = true };
            Assert.True(ac3Only.CanDirectPlayAudio("ac3"));
            Assert.False(ac3Only.CanDirectPlayAudio("eac3"));
        }

        [Fact]
        public void FlacFollowsTheClientsOwnFlag()
        {
            Assert.True(DolbyEdge.CanDirectPlayAudio("flac"));
            Assert.False((DolbyEdge with { Flac = false }).CanDirectPlayAudio("flac"));
        }

        // No browser advertises these, and there is no flag for them: never handed over raw.
        [Theory]
        [InlineData("dts")]
        [InlineData("truehd")]
        [InlineData("mp2")]
        [InlineData("pcm_s16le")]
        public void CodecsNoClientDeclaresNeverDirectPlay(string codec)
        {
            Assert.False(DolbyEdge.CanDirectPlayAudio(codec));
        }

        // An unsynced MediaFile row must decline rather than gamble: declining costs an ffmpeg remux,
        // guessing wrong costs a silent film.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("something-new")]
        public void UnknownAudioDeclines(string? codec)
        {
            Assert.False(DolbyEdge.CanDirectPlayAudio(codec));
        }

        [Fact]
        public void CodecMatchingIsCaseInsensitive()
        {
            Assert.True(DolbyEdge.CanDirectPlayAudio("AC3"));
            Assert.False(NoDolbyEdge.CanDirectPlayAudio("AC3"));
        }
    }
}
