using System;

namespace MovieTheater.Core
{
    /// <summary>
    /// The deterministic CloudRetro room/session id that makes arcade saves user-scoped
    /// (docs/arcade-saves-plan.md). CloudRetro names a session's save files (<c>&lt;id&gt;.dat</c> /
    /// <c>.srm</c>) after the room id and, for a fresh room, will use exactly the client-supplied
    /// <c>room_id</c> — <b>provided it is formatted <c>&lt;prefix&gt;___&lt;gameKey&gt;</c></b> (must contain
    /// <c>___</c>; the suffix must resolve to a real game via the library scan; a non-<c>___</c> id is
    /// rejected). We exploit that: the site mints
    /// <c>sv-&lt;userId&gt;-&lt;gameId&gt;-&lt;slotId&gt;-&lt;system&gt;___&lt;gameKey&gt;</c> so the gateway
    /// knows the exact save filename before the game boots and can seed/harvest it per (user, game, slot)
    /// with no emulator patch. The <c>___&lt;gameKey&gt;</c> suffix is what CloudRetro resolves the ROM
    /// from; the <c>sv-…</c> prefix is opaque to CloudRetro and carries our routing.
    /// </summary>
    public static class ArcadeSaveId
    {
        public const string Prefix = "sv-";
        public const string Sep = "___";

        /// <summary>Mint the deterministic id. gameKey MUST be the game's CloudRetroGameKey (the filename
        /// sans extension) so CloudRetro's library scan resolves it from the suffix.</summary>
        public static string Mint(int userId, int gameId, int slotId, string system, string gameKey) =>
            $"{Prefix}{userId}-{gameId}-{slotId}-{system}{Sep}{gameKey}";

        /// <summary>True if <paramref name="id"/> is one of our deterministic save ids (vs a legacy random
        /// room id or empty).</summary>
        public static bool Is(string? id) =>
            !string.IsNullOrEmpty(id) && id.StartsWith(Prefix, StringComparison.Ordinal) && id.Contains(Sep, StringComparison.Ordinal);

        /// <summary>Parse a minted id back into its parts. Robust to a gameKey that itself contains the
        /// separator by splitting on the FIRST <c>___</c>. System codes never contain '-', so the prefix
        /// splits cleanly into userId-gameId-slotId-system.</summary>
        public static bool TryParse(string? id, out int userId, out int gameId, out int slotId, out string system, out string gameKey)
        {
            userId = gameId = slotId = 0;
            system = gameKey = string.Empty;
            if (!Is(id)) return false;

            int sep = id!.IndexOf(Sep, StringComparison.Ordinal);
            gameKey = id[(sep + Sep.Length)..];
            var prefix = id[Prefix.Length..sep]; // "<userId>-<gameId>-<slotId>-<system>"
            var parts = prefix.Split('-', 4);
            if (parts.Length != 4) return false;
            if (!int.TryParse(parts[0], out userId) || !int.TryParse(parts[1], out gameId) || !int.TryParse(parts[2], out slotId))
                return false;
            system = parts[3];
            return gameKey.Length > 0 && system.Length > 0;
        }
    }
}
