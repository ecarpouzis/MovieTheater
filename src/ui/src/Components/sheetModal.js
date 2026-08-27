/**
 * The site's dialog layer, as ONE number instead of a literal repeated in a dozen files.
 *
 * The stack it has to clear, bottom up: the tweaks panel (1200, catalog-views.css), the fixed phone
 * top bar (1300, NavBar.css), the facet rail's phone sheet (1350, catalog/rail/rail.css). Above it:
 * the immersive routes that own the whole screen and carry their own Back (the watch room and the TV
 * room at 1400, the Books reader at 1400) — those are ROUTES, not dialogs, and a dialog opened from
 * one still has to sit over it, which is why the band starts at 1500 rather than 1350.
 *
 * `SHEET_Z` is every section's detail modal: the movie sheet, the game sheet, the boardgame sheet,
 * the album sheet, the photo lightbox, the Books item/series sheets, the TV admin and playlist
 * surfaces. One value means a modal can never open under the bar or under the rail sheet, which is
 * exactly the bug each of those files fixed separately (and the ones still at antd's default 1000
 * had not).
 *
 * `SHEET_STACK_Z` is for the second dialog a sheet OPENS WITHOUT CLOSING ITSELF — the playlist
 * picker over the movie sheet, the album sheet's "＋ Playlist", the arcade's ⚙ Configure over the
 * game sheet, a manage dialog over the playlists list. At `SHEET_Z` those would open behind the
 * sheet that raised them and read as dead buttons.
 *
 * Floating antd overlays (Select/Tooltip/Dropdown popups) are NOT in this band at all — they ride
 * the one popup layer at 10000 (antdPopupLayer.css), which is above antd 6's static-dialog ceiling.
 */

/** Every section's detail modal / full-page sheet. */
export const SHEET_Z = 1500;

/** A dialog raised BY a sheet while that sheet stays on screen. */
export const SHEET_STACK_Z = 1600;
