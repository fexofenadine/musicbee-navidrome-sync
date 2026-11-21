//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0
//sync rules inspired by rombat/musicbee-navidrome-sync: https://github.com/rombat/musicbee-navidrome-sync

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MusicBeePlugin
{
    internal interface ISyncProgress
    {
        void Phase(string text);
        void Progress(int current, int total, string detail);
    }

    internal class SyncSummary
    {
        public bool Preview;
        public bool Cancelled;
        public Exception Error;
        public string LogPath;

        public int LibraryTracks;
        public int NavidromeSongs;
        public int MatchedByPath;
        public int MatchedByMetadata;
        public int NotFound;
        public int Ambiguous;
        public int DuplicateFiles;
        public int RatingsSet;
        public int TracksStarred;
        public int TracksWithPlaysAdded;
        public int PlaysAdded;
        public int AlbumRatingsSet;
        public int ArtistRatingsSet;
        public int FailedRequests;
        public int RequestCount;

        public string ToText()
        {
            string verb = Preview ? "would be " : "";
            var sb = new StringBuilder();
            if (Preview) sb.AppendLine("Preview only: nothing was changed on the server.");
            if (Cancelled) sb.AppendLine("The sync was cancelled before it finished.");
            if (Error != null) sb.AppendLine("The sync stopped with an error: " + SubsonicClient.Describe(Error));
            sb.AppendLine("MusicBee tracks read: " + LibraryTracks);
            sb.AppendLine("Navidrome songs fetched: " + NavidromeSongs);
            sb.AppendLine("Matched: " + (MatchedByPath + MatchedByMetadata) + " (" + MatchedByPath + " by path, " + MatchedByMetadata + " by tags)");
            sb.AppendLine("Not found: " + NotFound + ", ambiguous: " + Ambiguous + ", duplicate files merged: " + DuplicateFiles);
            sb.AppendLine("Track ratings " + verb + "set: " + RatingsSet);
            sb.AppendLine("Tracks " + verb + "starred: " + TracksStarred);
            sb.AppendLine("Plays " + verb + "added: " + PlaysAdded + " across " + TracksWithPlaysAdded + " tracks");
            sb.AppendLine("Album ratings " + verb + "set: " + AlbumRatingsSet);
            sb.AppendLine("Artist ratings " + verb + "set: " + ArtistRatingsSet);
            sb.Append("Requests sent: " + RequestCount + ", failed: " + FailedRequests);
            return sb.ToString();
        }
    }

    internal static class RatingMath
    {
        //Navidrome only stores whole stars, so MusicBee half stars and album/artist averages need rounding
        public static int Round(double value, RatingRounding mode)
        {
            double rounded;
            switch (mode)
            {
                case RatingRounding.Down:
                    rounded = Math.Floor(value + 1e-9);
                    break;
                case RatingRounding.Nearest:
                    rounded = Math.Round(value, MidpointRounding.AwayFromZero);
                    break;
                default:
                    rounded = Math.Ceiling(value - 1e-9);
                    break;
            }
            return (int)Math.Max(0, Math.Min(5, rounded));
        }
    }

    internal class SyncEngine
    {
        private const int BatchSize = 50;
        private const int MaxConsecutiveFailures = 10;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly Plugin.MusicBeeApiInterface mb;
        private readonly SyncSettings settings;
        private readonly SyncLog log;
        private readonly ISyncProgress progress;
        private readonly string prefix;

        private SubsonicClient client;
        private SyncSummary summary;
        private int consecutiveFailures;

        public SyncEngine(Plugin.MusicBeeApiInterface mb, SyncSettings settings, SyncLog log, ISyncProgress progress)
        {
            this.mb = mb;
            this.settings = settings;
            this.log = log;
            this.progress = progress;
            prefix = settings.PreviewOnly ? "[preview] " : "";
        }

        public SyncSummary Run(CancellationToken ct)
        {
            summary = new SyncSummary { Preview = settings.PreviewOnly };
            try
            {
                LogOptions();
                client = new SubsonicClient(settings.ServerUrl, settings.Username, settings.Password, settings.RequestDelayMs);

                progress.Phase("Connecting to Navidrome");
                client.Ping(ct);
                log.Info("connected to " + settings.ServerUrl + " as " + settings.Username);

                progress.Phase("Reading MusicBee library");
                List<MbTrack> tracks = ReadMusicBeeLibrary(ct);
                summary.LibraryTracks = tracks.Count;
                log.Info("read " + tracks.Count + " tracks from MusicBee");

                progress.Phase("Fetching Navidrome library");
                List<NdSong> songs = client.GetAllSongs(n => progress.Progress(n, 0, n + " songs fetched"), ct);
                summary.NavidromeSongs = songs.Count;
                log.Info("fetched " + songs.Count + " songs from Navidrome");
                if (songs.Count == 0) throw new InvalidOperationException("Navidrome returned no songs, so there is nothing to match against.");

                progress.Phase("Matching tracks");
                List<MergedSource> matched = MatchTracks(tracks, songs, ct);

                ApplyTrackChanges(matched, ct);

                if (settings.SyncAlbumRatings)
                {
                    progress.Phase("Calculating album ratings");
                    Dictionary<string, int> current = client.GetAlbumRatings(ct);
                    summary.AlbumRatingsSet = ApplyGroupRatings(songs, s => s.AlbumId, s => s.Album, 1, current, "album", ct);
                }

                if (settings.SyncArtistRatings)
                {
                    progress.Phase("Calculating artist ratings");
                    Dictionary<string, int> current = client.GetArtistRatings(ct);
                    summary.ArtistRatingsSet = ApplyGroupRatings(songs, s => s.ArtistId, s => s.Artist, 2, current, "artist", ct);
                }

                log.Info("finished");
            }
            catch (OperationCanceledException)
            {
                summary.Cancelled = true;
                log.Info("cancelled");
            }
            catch (Exception ex)
            {
                summary.Error = ex;
                log.Problem("stopped: " + SubsonicClient.Describe(ex));
            }
            finally
            {
                if (client != null) summary.RequestCount = client.RequestCount;
                summary.LogPath = log.Save(summary);
            }
            return summary;
        }

        private void LogOptions()
        {
            log.Info("server: " + settings.ServerUrl + ", user: " + settings.Username);
            log.Info("options: play counts=" + settings.SyncPlayCounts
                + ", track ratings=" + settings.SyncRatings
                + ", loved=" + settings.SyncLoved
                + ", album ratings=" + settings.SyncAlbumRatings
                + ", artist ratings=" + settings.SyncArtistRatings
                + ", rounding=" + settings.Rounding
                + ", force ratings=" + settings.ForceRatings
                + ", preview only=" + settings.PreviewOnly
                + ", delay=" + settings.RequestDelayMs + "ms");
        }

        private List<MbTrack> ReadMusicBeeLibrary(CancellationToken ct)
        {
            string[] files;
            if (!mb.Library_QueryFilesEx("domain=Library", out files) || files == null)
                throw new InvalidOperationException("MusicBee did not return its library.");

            var fields = new[]
            {
                Plugin.MetaDataType.TrackTitle,
                Plugin.MetaDataType.Artist,
                Plugin.MetaDataType.AlbumArtist,
                Plugin.MetaDataType.Album,
                Plugin.MetaDataType.TrackNo,
                Plugin.MetaDataType.DiscNo,
                Plugin.MetaDataType.Rating,
                Plugin.MetaDataType.RatingLove
            };

            var tracks = new List<MbTrack>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                if (i % 200 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Progress(i, files.Length, i + " of " + files.Length + " tracks read");
                }

                string file = files[i];
                string[] tags;
                if (!mb.Library_GetFileTags(file, fields, out tags) || tags == null || tags.Length < fields.Length)
                {
                    log.Problem("could not read tags for " + file);
                    continue;
                }

                tracks.Add(new MbTrack
                {
                    FileUrl = file,
                    Title = tags[0],
                    Artist = tags[1],
                    AlbumArtist = tags[2],
                    Album = tags[3],
                    TrackNo = ParseLeadingInt(tags[4]),
                    DiscNo = ParseLeadingInt(tags[5]),
                    Rating = ParseRating(tags[6]),
                    //MusicBee uses "L" for loved and "B" for banned
                    Loved = !string.IsNullOrEmpty(tags[7]) && tags[7].StartsWith("L", StringComparison.OrdinalIgnoreCase),
                    PlayCount = ParseLeadingInt(mb.Library_GetFileProperty(file, Plugin.FilePropertyType.PlayCount)),
                    LastPlayed = ParseMusicBeeDate(mb.Library_GetFileProperty(file, Plugin.FilePropertyType.LastPlayed)),
                    DateAdded = ParseMusicBeeDate(mb.Library_GetFileProperty(file, Plugin.FilePropertyType.DateAdded)),
                    DurationSeconds = ParseDuration(mb.Library_GetFileProperty(file, Plugin.FilePropertyType.Duration))
                });
            }
            progress.Progress(files.Length, files.Length, files.Length + " tracks read");
            return tracks;
        }

        private List<MergedSource> MatchTracks(List<MbTrack> tracks, List<NdSong> songs, CancellationToken ct)
        {
            var matcher = new TrackMatcher(songs);
            var bySong = new Dictionary<string, MergedSource>(StringComparer.Ordinal);

            for (int i = 0; i < tracks.Count; i++)
            {
                if (i % 200 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Progress(i, tracks.Count, i + " of " + tracks.Count + " tracks matched");
                }

                MbTrack track = tracks[i];
                MatchResult result = matcher.Match(track);
                switch (result.Kind)
                {
                    case MatchKind.NotFound:
                        summary.NotFound++;
                        log.NotFound(track.Describe() + "  <" + track.FileUrl + ">");
                        continue;
                    case MatchKind.Ambiguous:
                        summary.Ambiguous++;
                        log.Ambiguous(track.Describe() + "  (" + result.CandidateCount + " candidates)  <" + track.FileUrl + ">");
                        continue;
                    case MatchKind.Path:
                        summary.MatchedByPath++;
                        break;
                    default:
                        summary.MatchedByMetadata++;
                        break;
                }

                MergedSource merged;
                if (!bySong.TryGetValue(result.Song.Id, out merged))
                {
                    merged = new MergedSource { Song = result.Song };
                    bySong[result.Song.Id] = merged;
                }
                else
                {
                    summary.DuplicateFiles++;
                    log.Info("merged duplicate file " + track.FileUrl + " into " + result.Song.Describe());
                }
                merged.Add(track);
            }
            progress.Progress(tracks.Count, tracks.Count, tracks.Count + " tracks matched");
            return bySong.Values.ToList();
        }

        private void ApplyTrackChanges(List<MergedSource> matched, CancellationToken ct)
        {
            var ratingChanges = new List<KeyValuePair<NdSong, int>>();
            var toStar = new List<NdSong>();
            var scrobbleSongs = new List<NdSong>();
            var scrobbleTimes = new List<long>();

            foreach (MergedSource m in matched)
            {
                NdSong song = m.Song;

                //ratings only move up unless forcing, and an unrated MusicBee track never clears a Navidrome rating
                if (settings.SyncRatings && m.Rating > 0)
                {
                    int target = RatingMath.Round(m.Rating, settings.Rounding);
                    if (target > 0 && target != song.UserRating && (target > song.UserRating || settings.ForceRatings))
                    {
                        ratingChanges.Add(new KeyValuePair<NdSong, int>(song, target));
                        log.Change(prefix + "rating " + song.UserRating + " -> " + target + "  " + song.Describe());
                    }
                }

                //loved is only ever added, so loves made in Navidrome are never removed
                if (settings.SyncLoved && m.Loved && !song.Starred)
                {
                    toStar.Add(song);
                    log.Change(prefix + "star  " + song.Describe());
                }

                //play counts can only grow through scrobbles, one per missing play
                if (settings.SyncPlayCounts && m.PlayCount > song.PlayCount)
                {
                    int missing = m.PlayCount - song.PlayCount;
                    long time = ToUnixMs(PickScrobbleTime(m));
                    for (int i = 0; i < missing; i++)
                    {
                        scrobbleSongs.Add(song);
                        scrobbleTimes.Add(time);
                    }
                    summary.TracksWithPlaysAdded++;
                    log.Change(prefix + "plays " + song.PlayCount + " -> " + m.PlayCount + "  " + song.Describe());
                }
            }

            int starBatches = (toStar.Count + BatchSize - 1) / BatchSize;
            int scrobbleBatches = (scrobbleSongs.Count + BatchSize - 1) / BatchSize;
            int totalSteps = ratingChanges.Count + starBatches + scrobbleBatches;
            int step = 0;
            log.Info("planned: " + ratingChanges.Count + " ratings, " + toStar.Count + " stars, "
                + scrobbleSongs.Count + " plays across " + summary.TracksWithPlaysAdded + " tracks");

            if (ratingChanges.Count > 0) progress.Phase(settings.PreviewOnly ? "Previewing track ratings" : "Updating track ratings");
            foreach (var change in ratingChanges)
            {
                NdSong song = change.Key;
                int target = change.Value;
                if (Send(() => client.SetRating(song.Id, target, ct), "set rating for " + song.Describe(), ct))
                {
                    song.UserRating = target;
                    summary.RatingsSet++;
                }
                progress.Progress(++step, totalSteps, summary.RatingsSet + " of " + ratingChanges.Count + " ratings");
            }

            if (toStar.Count > 0) progress.Phase(settings.PreviewOnly ? "Previewing loved tracks" : "Starring loved tracks");
            for (int start = 0; start < toStar.Count; start += BatchSize)
            {
                List<NdSong> batch = toStar.Skip(start).Take(BatchSize).ToList();
                if (Send(() => client.Star(batch.Select(s => s.Id).ToList(), ct), "star " + batch.Count + " tracks", ct))
                {
                    foreach (NdSong song in batch) song.Starred = true;
                    summary.TracksStarred += batch.Count;
                }
                progress.Progress(++step, totalSteps, summary.TracksStarred + " of " + toStar.Count + " tracks starred");
            }

            if (scrobbleSongs.Count > 0) progress.Phase(settings.PreviewOnly ? "Previewing play counts" : "Adding plays");
            for (int start = 0; start < scrobbleSongs.Count; start += BatchSize)
            {
                List<NdSong> batch = scrobbleSongs.Skip(start).Take(BatchSize).ToList();
                List<long> times = scrobbleTimes.Skip(start).Take(BatchSize).ToList();
                if (Send(() => client.Scrobble(batch.Select(s => s.Id).ToList(), times, ct), "add " + batch.Count + " plays", ct))
                {
                    foreach (NdSong song in batch) song.PlayCount++;
                    summary.PlaysAdded += batch.Count;
                }
                progress.Progress(++step, totalSteps, summary.PlaysAdded + " of " + scrobbleSongs.Count + " plays");
            }
        }

        //average rating for each album or artist, taken from Navidrome's track ratings after this run's changes
        private int ApplyGroupRatings(List<NdSong> songs, Func<NdSong, string> idOf, Func<NdSong, string> nameOf,
            int minTracks, Dictionary<string, int> currentRatings, string label, CancellationToken ct)
        {
            var changes = new List<KeyValuePair<string, int>>();
            foreach (var group in songs.Where(s => !string.IsNullOrEmpty(idOf(s))).GroupBy(idOf))
            {
                int total = group.Count();
                if (total < minTracks) continue;

                List<int> rated = group.Where(s => s.UserRating > 0).Select(s => s.UserRating).ToList();
                //more than half the tracks must be rated before an average means anything
                if (rated.Count * 2 <= total) continue;

                int target = RatingMath.Round(rated.Average(), settings.Rounding);
                int current;
                currentRatings.TryGetValue(group.Key, out current);
                if (target > 0 && target != current && (target > current || settings.ForceRatings))
                {
                    changes.Add(new KeyValuePair<string, int>(group.Key, target));
                    log.Change(prefix + label + " rating " + current + " -> " + target + "  " + nameOf(group.First())
                        + "  (" + rated.Count + " of " + total + " tracks rated)");
                }
            }

            int applied = 0;
            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (Send(() => client.SetRating(change.Key, change.Value, ct), "set " + label + " rating for id " + change.Key, ct))
                    applied++;
                progress.Progress(i + 1, changes.Count, applied + " of " + changes.Count + " " + label + " ratings");
            }
            return applied;
        }

        //sends one request, or pretends to in preview mode, so a single failure doesn't abort the whole sync
        private bool Send(Action request, string description, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (settings.PreviewOnly) return true;
            try
            {
                request();
                consecutiveFailures = 0;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SubsonicException || ex is IOException)
            {
                summary.FailedRequests++;
                consecutiveFailures++;
                log.Problem("failed to " + description + ": " + SubsonicClient.Describe(ex));
                if (consecutiveFailures >= MaxConsecutiveFailures)
                    throw new InvalidOperationException("Stopped after " + MaxConsecutiveFailures + " failed requests in a row. Last error: " + SubsonicClient.Describe(ex));
                return false;
            }
        }

        //Navidrome keeps whichever play date is newest, so an older timestamp here can't move its last played backwards
        private static DateTime PickScrobbleTime(MergedSource m)
        {
            DateTime now = DateTime.UtcNow;
            DateTime? chosen = m.LastPlayed ?? m.Song.Played ?? m.DateAdded;
            if (!chosen.HasValue) return now;
            DateTime utc = chosen.Value.ToUniversalTime();
            return utc > now ? now : utc;
        }

        private static long ToUnixMs(DateTime utc)
        {
            return (long)(utc - Epoch).TotalMilliseconds;
        }

        private static int ParseLeadingInt(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            //handles values such as "3/12" for track numbers
            int end = 0;
            string trimmed = value.Trim();
            while (end < trimmed.Length && char.IsDigit(trimmed[end])) end++;
            int result;
            return end > 0 && int.TryParse(trimmed.Substring(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static double ParseRating(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            double rating;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out rating)
                && !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rating))
                return 0;
            //MusicBee normally reports stars (0 to 5), but a 0 to 100 value is converted just in case
            if (rating > 5) rating /= 20.0;
            return Math.Max(0, Math.Min(5, rating));
        }

        private static DateTime? ParseMusicBeeDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            DateTime parsed;
            //MusicBee formats dates using the Windows regional settings
            if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed)
                || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                //MusicBee reports unset dates as the minimum date rather than blank
                if (parsed.Year < 1971) return null;
                return parsed;
            }
            return null;
        }

        private static int ParseDuration(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            string[] parts = value.Trim().Split(':');
            if (parts.Length == 1)
            {
                //a bare number is taken as seconds, or milliseconds if it is too large to be seconds
                long raw;
                if (!long.TryParse(parts[0].Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out raw)) return 0;
                return (int)(raw > 86400 ? raw / 1000 : raw);
            }
            int seconds = 0;
            foreach (string part in parts)
            {
                int n;
                if (!int.TryParse(part.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out n)) return 0;
                seconds = seconds * 60 + n;
            }
            return seconds;
        }
    }
}
