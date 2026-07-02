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
    }

    /// <summary>The catalog facts the host needs to build a launch descriptor, decoupled from the EF entity.</summary>
    public sealed record ArcadeGameDescriptor(int Id, string CloudRetroGameKey);

    /// <summary>
    /// What the room page needs to connect. Mirrors the shape in §6: a tokened gateway WS URL, the
    /// 0-based controller port, the CloudRetro launch key, the ICE servers, and whether this browser
    /// is the room creator (who must send t=104 with an empty room_id, then call Bind).
    /// </summary>
    public sealed record ArcadeJoinDescriptor(
        string RoomCode,
        string WsUrl,
        int PlayerSlot,
        string GameKey,
        IReadOnlyList<ArcadeIceServer> IceConfig,
        bool IsCreator);

    public sealed record ArcadeIceServer(string Urls);
}
