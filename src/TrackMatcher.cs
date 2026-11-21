//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MusicBeePlugin
{
    //matches MusicBee tracks to Navidrome songs, first by file path and then by tags
    internal class TrackMatcher
    {
        private const int DurationToleranceSeconds = 3;
        private const char KeySeparator = '\u0001';

        private readonly Dictionary<string, List<NdSong>> byFileName = new Dictionary<string, List<NdSong>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<NdSong>> byTitleAlbum = new Dictionary<string, List<NdSong>>(StringComparer.Ordinal);

        public TrackMatcher(IEnumerable<NdSong> songs)
        {
            foreach (NdSong song in songs)
            {
                string[] segments = Segments(song.Path);
                if (segments.Length > 0) AddTo(byFileName, segments[segments.Length - 1], song);

                string key = MetadataKey(song.Title, song.Album);
                if (key != null) AddTo(byTitleAlbum, key, song);
            }
        }

        public MatchResult Match(MbTrack track)
        {
            return MatchByPath(track) ?? MatchByMetadata(track);
        }

        //only succeeds when Navidrome reports real file paths, otherwise its paths are generated from tags and rarely line up
        private MatchResult MatchByPath(MbTrack track)
        {
            string[] mbSegments = Segments(track.FileUrl);
            if (mbSegments.Length == 0) return null;

            List<NdSong> candidates;
            if (!byFileName.TryGetValue(mbSegments[mbSegments.Length - 1], out candidates)) return null;

            NdSong best = null;
            int bestScore = 0;
            bool tie = false;
            foreach (NdSong candidate in candidates)
            {
                int score = TrailingMatch(mbSegments, Segments(candidate.Path));
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                    tie = false;
                }
                else if (score == bestScore)
                {
                    tie = true;
                }
            }

            //the file name plus at least its parent folder must agree, and only one song may win
            if (best == null || bestScore < 2 || tie) return null;
            return new MatchResult(MatchKind.Path, best, 1);
        }

        private MatchResult MatchByMetadata(MbTrack track)
        {
            string key = MetadataKey(track.Title, track.Album);
            List<NdSong> candidates;
            if (key == null || !byTitleAlbum.TryGetValue(key, out candidates))
                return new MatchResult(MatchKind.NotFound, null, 0);

            List<NdSong> matches = candidates
                .Where(c => ArtistMatches(c.Artist, track.Artist) || ArtistMatches(c.Artist, track.AlbumArtist))
                .ToList();

            //each narrowing step is skipped if it would rule out every remaining candidate
            if (matches.Count > 1)
                matches = Narrow(matches, c => track.TrackNo <= 0 || c.Track <= 0 || c.Track == track.TrackNo);
            if (matches.Count > 1)
                matches = Narrow(matches, c => track.DiscNo <= 0 || c.Disc <= 0 || c.Disc == track.DiscNo);
            if (matches.Count > 1)
                matches = Narrow(matches, c => track.DurationSeconds <= 0 || c.DurationSeconds <= 0
                    || Math.Abs(c.DurationSeconds - track.DurationSeconds) <= DurationToleranceSeconds);

            if (matches.Count == 1) return new MatchResult(MatchKind.Metadata, matches[0], 1);
            if (matches.Count == 0) return new MatchResult(MatchKind.NotFound, null, 0);
            return new MatchResult(MatchKind.Ambiguous, null, matches.Count);
        }

        private static List<NdSong> Narrow(List<NdSong> songs, Func<NdSong, bool> predicate)
        {
            List<NdSong> narrowed = songs.Where(predicate).ToList();
            return narrowed.Count > 0 ? narrowed : songs;
        }

        private static bool ArtistMatches(string a, string b)
        {
            string na = Normalise(a);
            string nb = Normalise(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            if (na == nb) return true;

            //allows "artist" to match "artist feat. someone", but not on very short names where it would match too loosely
            string shorter = na.Length < nb.Length ? na : nb;
            string longer = na.Length < nb.Length ? nb : na;
            return shorter.Length >= 3 && longer.Contains(shorter);
        }

        private static string MetadataKey(string title, string album)
        {
            string normalisedTitle = Normalise(title);
            if (normalisedTitle.Length == 0) return null;
            return normalisedTitle + KeySeparator + Normalise(album);
        }

        //lower case, accents removed, punctuation collapsed to single spaces
        internal static string Normalise(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string decomposed = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            bool pendingSpace = false;
            foreach (char ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch))
                {
                    if (pendingSpace && sb.Length > 0) sb.Append(' ');
                    sb.Append(char.ToLowerInvariant(ch));
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = true;
                }
            }
            return sb.ToString();
        }

        private static string[] Segments(string path)
        {
            if (string.IsNullOrEmpty(path)) return new string[0];
            return path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToLowerInvariant())
                .ToArray();
        }

        private static int TrailingMatch(string[] a, string[] b)
        {
            int count = 0;
            while (count < a.Length && count < b.Length && a[a.Length - 1 - count] == b[b.Length - 1 - count]) count++;
            return count;
        }

        private static void AddTo(Dictionary<string, List<NdSong>> index, string key, NdSong song)
        {
            List<NdSong> list;
            if (!index.TryGetValue(key, out list))
            {
                list = new List<NdSong>();
                index[key] = list;
            }
            list.Add(song);
        }
    }
}
