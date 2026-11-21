# Changelog

## 0.1.0 (2026-09-26)

Initial release.

- Adds **Tools → Sync ratings and playcounts to Navidrome** to MusicBee, with a hotkey binding.
- Syncs track play counts, track ratings and loved tracks from MusicBee to Navidrome through the Subsonic API.
- Calculates album and artist ratings from track rating averages.
- Only applies favourable changes:
  - ratings are only raised, unless forcing is enabled
  - loved tracks are only ever starred
  - play counts are only increased
- Fetches the whole Navidrome library in pages and matches tracks locally, by file path first and then by tags.
- Merges duplicate MusicBee files that match the same Navidrome song, so plays aren't sent twice.
- Sends play counts as batched scrobbles stamped with MusicBee's last played date.
- Settings window with:
  - connection test
  - password encrypted with Windows DPAPI
  - choice of half-star rounding
  - request delay
  - option to allow lowering ratings
  - preview-only mode
- Progress window with cancel.
- Writes a log of every change, every failed request and every unmatched track.
