namespace MovieTheater.Services.Arcade
{
    /// <summary>
    /// The single seam between the site and the emulator stack (arcade-plan.md §2 hedge). CloudRetro
    /// is the v1 implementation; if it stagnates, the room model, DB schema, page, and auth don't
    /// change — only this adapter and the in-browser client shim do.
    ///
    /// In v1 this is deliberately small: mint a signed WS-join capability and assemble the join
    /// descriptor the browser needs (gateway URL, seat, ICE config). It holds no room state — that
    /// lives in ArcadeRoomService — and it never calls Ziggy, because CloudRetro has no server room
    /// API (§2 box): the creator's browser drives room lifecycle.
    /// </summary>
    public interface IArcadeHost
    {
        /// <summary>True when the arcade is configured (gateway URL + token secret present). When false,
        /// the controller hides/503s the arcade, mirroring how streaming 501s when unconfigured.</summary>
        bool IsConfigured { get; }

        /// <summary>The best-effort concurrent-room cap (= deployed worker count). CloudRetro's t=112 is
        /// the authoritative backstop; this is the friendly pre-check.</summary>
        int MaxConcurrentRooms { get; }

        /// <summary>
        /// Build the descriptor a browser needs to open its arcade WebSocket. For the creator, pass an
        /// empty <paramref name="cloudRetroRoomId"/> (they will create the CloudRetro room, then Bind the
        /// id it returns); for a joiner, pass the bound id so the token confines them to that room and the
        /// gateway routes them to the same worker.
        /// </summary>
        ArcadeJoinDescriptor BuildJoinDescriptor(
            int userId, ArcadeGameDescriptor game, string roomCode, string cloudRetroRoomId, int playerSlot, bool isCreator);

        /// <summary>
        /// Mint a FRESH capability token for a live room's control REST — the in-room quicksave / snapshot /
        /// load endpoints on the gateway. Those endpoints reuse the same signed token the WS join carried, but
        /// (unlike the WS, which is opened once and then held open past expiry) they re-validate its expiry on
        /// EVERY call. So on a play session longer than <c>ArcadeJoinTokenTtlSeconds</c> the original token
        /// lapses and saves start failing — surfacing in the browser as a bogus CORS error, because the gateway
        /// returned a 500 for the rejected token and a 500 carries no CORS header. The heartbeat re-mints this
        /// every ~12 s so a present player's save token never goes stale. Returns null when unconfigured.
        /// </summary>
        string? MintControlToken(int userId, int gameId, string roomCode, string cloudRetroRoomId, int playerSlot);
    }

    /// <summary>The catalog facts the host needs to build a launch descriptor, decoupled from the EF entity.
    /// <paramref name="System"/> is the short system code (n64, snes, ps1, …) the browser uses to pick a
    /// per-system input profile.</summary>
    public sealed record ArcadeGameDescriptor(int Id, string CloudRetroGameKey, string System);

    /// <summary>
    /// What the room page needs to connect. Mirrors the shape in §6: a tokened gateway WS URL, the
    /// 0-based controller port, the CloudRetro launch key, the ICE servers, and whether this browser
    /// is the room creator (who must send t=104 with an empty room_id, then call Bind).
    /// </summary>
    /// <param name="PlayerSlot">The 0-based controller port, or
    /// <c>ArcadeRoomService.SpectatorSlot</c> (-1) for a watch-only seat: no controller port, the shim
    /// sends no input and skips t=108.</param>
    /// <param name="CoreOptions">Per-room libretro core-option cheats the creator enabled (e.g.
    /// <c>pcsx2_widescreen_hint</c>). Empty for a joiner: the room's emulator is already running.</param>
    /// <param name="CheatCodes">Per-room cheat codes for <c>retro_cheat_set</c>. These ride the descriptor
    /// rather than the WS URL (as <c>?vbr</c>/<c>?fec</c> do) because a code list is far too long for a query
    /// string. They are not security-sensitive — the creator is choosing cheats for their own room.</param>
    /// <param name="RaUser">The room creator's linked RetroAchievements username, when they have one. The
    /// worker logs rcheevos into RA under this account (a room runs one emulator, so RA is the creator's).
    /// Null/empty = RA off for this room — byte-identical <c>t=104</c> to before the feature. Creator-only
    /// (like <see cref="CoreOptions"/>): only the creator's GAME_START boots the emulator.</param>
    /// <param name="RaToken">The creator's RetroAchievements CONNECT token (not their password), decrypted
    /// from storage just before the descriptor is built. Rides the descriptor body — the user's own token
    /// for their own session, over the tokened gateway WS, same trust path the save/cheat descriptor uses.
    /// Never logged. Null when the creator hasn't linked RA.</param>
    /// <param name="Hardcore">True to run rcheevos in HARDCORE mode — set for a competitive room with a
    /// linked creator. Hardcore is what makes RA count the unlocks/leaderboard runs as legit.</param>
    /// <param name="CoreKey">The ALTERNATE core this room booted (<c>parallel_n64</c>), or empty for the
    /// system's default core. Distinct from <see cref="System"/> on purpose: the two together are the
    /// room's identity, and folding the core into the system string is what broke a joiner's input
    /// profile (see the Join path's note on the save namespace).</param>
    /// <param name="CanRewind">Whether the worker has this room's rewind ring armed. Server-computed
    /// from (system, core) because the arming is per-CORE and the client cannot know which core booted
    /// — see <c>ArcadeRewindSupport</c>. False means the room page must not offer rewind at all: the
    /// packet would be accepted and silently do nothing.</param>
    public sealed record ArcadeJoinDescriptor(
        string RoomCode,
        string WsUrl,
        int PlayerSlot,
        string GameKey,
        IReadOnlyList<ArcadeIceServer> IceConfig,
        bool IsCreator,
        string System,
        IReadOnlyDictionary<string, string>? CoreOptions = null,
        IReadOnlyList<string>? CheatCodes = null,
        string? RaUser = null,
        string? RaToken = null,
        bool Hardcore = false,
        string? CoreKey = null,
        bool CanRewind = false);

    /// <summary>One ICE server for the client's RTCPeerConnection. STUN entries carry only
    /// <paramref name="Urls"/>; a TURN entry additionally carries the ephemeral
    /// <paramref name="Username"/>/<paramref name="Credential"/> minted per join
    /// (<see cref="Core.ArcadeTurnCredential"/>).</summary>
    public sealed record ArcadeIceServer(string Urls, string? Username = null, string? Credential = null);
}
