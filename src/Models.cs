//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public enum RatingRounding
    {
        Up = 0,
        Down = 1,
        Nearest = 2
    }

    //a track as read from the MusicBee library
    internal class MbTrack
    {
        public string FileUrl;
        public string Title;
        public string Artist;
        public string AlbumArtist;
        public string Album;
        public int TrackNo;
        public int DiscNo;
        public int DurationSeconds;
        public int PlayCount;
        public double Rating;
        public bool Loved;
        public DateTime? LastPlayed;
        public DateTime? DateAdded;

        public string Describe()
        {
            return (Artist ?? "") + " - " + (Title ?? "") + " [" + (Album ?? "") + "]";
        }
    }

    //a song as returned by the Navidrome Subsonic API, values reflect the server after any changes made this run
    internal class NdSong
    {
        public string Id;
        public string Title;
        public string Artist;
        public string Album;
        public string AlbumId;
        public string ArtistId;
        public string Path;
        public int Track;
        public int Disc;
        public int DurationSeconds;
        public int UserRating;
        public int PlayCount;
        public bool Starred;
        public DateTime? Played;

        public string Describe()
        {
            return (Artist ?? "") + " - " + (Title ?? "") + " [" + (Album ?? "") + "]";
        }
    }

    //MusicBee values merged across every library file that matched the same Navidrome song
    internal class MergedSource
    {
        public NdSong Song;
        public readonly List<MbTrack> Files = new List<MbTrack>();
        public int PlayCount;
        public double Rating;
        public bool Loved;
        public DateTime? LastPlayed;
        public DateTime? DateAdded;

        public void Add(MbTrack track)
        {
            Files.Add(track);
            PlayCount = Math.Max(PlayCount, track.PlayCount);
            Rating = Math.Max(Rating, track.Rating);
            Loved = Loved || track.Loved;
            LastPlayed = Latest(LastPlayed, track.LastPlayed);
            if (track.DateAdded.HasValue && (!DateAdded.HasValue || track.DateAdded.Value < DateAdded.Value))
                DateAdded = track.DateAdded;
        }

        private static DateTime? Latest(DateTime? a, DateTime? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return a.Value.ToUniversalTime() >= b.Value.ToUniversalTime() ? a : b;
        }
    }

    internal enum MatchKind
    {
        Path,
        Metadata,
        NotFound,
        Ambiguous
    }

    internal class MatchResult
    {
        public readonly MatchKind Kind;
        public readonly NdSong Song;
        public readonly int CandidateCount;

        public MatchResult(MatchKind kind, NdSong song, int candidateCount)
        {
            Kind = kind;
            Song = song;
            CandidateCount = candidateCount;
        }
    }
}
