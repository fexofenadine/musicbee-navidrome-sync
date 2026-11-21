//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MusicBeePlugin
{
    //error reported by the server inside a subsonic-response with status "failed"
    internal class SubsonicException : Exception
    {
        public int Code { get; private set; }

        public SubsonicException(int code, string message) : base(message)
        {
            Code = code;
        }
    }

    //minimal client covering only the Subsonic API calls this plugin needs
    internal class SubsonicClient
    {
        private const string ApiVersion = "1.16.1";
        private const string ClientName = "MusicBeeNavidromeSync";
        private const int PageSize = 500;
        private const int TimeoutMs = 60000;
        private const int MaxAttempts = 3;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly string restUrl;
        private readonly string username;
        private readonly string password;
        private readonly int delayMs;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public int RequestCount { get; private set; }

        static SubsonicClient()
        {
            //.NET Framework can default to old TLS versions, which most reverse proxies now reject
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public SubsonicClient(string serverUrl, string username, string password, int delayMs)
        {
            string url = (serverUrl ?? "").Trim().TrimEnd('/');
            if (url.EndsWith("/rest", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - 5);
            restUrl = url + "/rest/";
            this.username = username ?? "";
            this.password = password ?? "";
            this.delayMs = Math.Max(0, delayMs);
        }

        public void Ping(CancellationToken ct)
        {
            Call("ping", null, ct);
        }

        //every song in the library, fetched in pages through an empty search
        public List<NdSong> GetAllSongs(Action<int> onProgress, CancellationToken ct)
        {
            //Navidrome returns everything for an empty search, but some versions expect the query sent as "" and others as a blank value
            List<NdSong> songs = FetchAllSongs("\"\"", onProgress, ct);
            if (songs.Count == 0) songs = FetchAllSongs("", onProgress, ct);
            return songs;
        }

        private List<NdSong> FetchAllSongs(string query, Action<int> onProgress, CancellationToken ct)
        {
            var songs = new List<NdSong>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int offset = 0;
            while (true)
            {
                var args = Args("query", query, "artistCount", "0", "albumCount", "0",
                    "songCount", PageSize.ToString(Inv), "songOffset", offset.ToString(Inv));
                Dictionary<string, object> root = Call("search3", args, ct);
                List<object> page = GetList(GetDict(root, "searchResult3"), "song");
                foreach (object item in page)
                {
                    var entry = item as Dictionary<string, object>;
                    if (entry == null) continue;
                    NdSong song = ParseSong(entry);
                    if (!string.IsNullOrEmpty(song.Id) && seen.Add(song.Id)) songs.Add(song);
                }
                if (onProgress != null) onProgress(songs.Count);
                if (page.Count < PageSize) break;
                offset += PageSize;
            }
            return songs;
        }

        //current album ratings keyed by album id, albums without a rating are left out
        public Dictionary<string, int> GetAlbumRatings(CancellationToken ct)
        {
            var ratings = new Dictionary<string, int>(StringComparer.Ordinal);
            int offset = 0;
            while (true)
            {
                var args = Args("type", "alphabeticalByName", "size", PageSize.ToString(Inv), "offset", offset.ToString(Inv));
                Dictionary<string, object> root = Call("getAlbumList2", args, ct);
                List<object> page = GetList(GetDict(root, "albumList2"), "album");
                foreach (object item in page)
                {
                    var album = item as Dictionary<string, object>;
                    if (album == null) continue;
                    string id = GetString(album, "id");
                    int rating = GetInt(album, "userRating");
                    if (!string.IsNullOrEmpty(id) && rating > 0) ratings[id] = rating;
                }
                if (page.Count < PageSize) break;
                offset += PageSize;
            }
            return ratings;
        }

        //current artist ratings keyed by artist id, artists without a rating are left out
        public Dictionary<string, int> GetArtistRatings(CancellationToken ct)
        {
            var ratings = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, object> root = Call("getArtists", null, ct);
            foreach (object indexItem in GetList(GetDict(root, "artists"), "index"))
            {
                foreach (object item in GetList(indexItem as Dictionary<string, object>, "artist"))
                {
                    var artist = item as Dictionary<string, object>;
                    if (artist == null) continue;
                    string id = GetString(artist, "id");
                    int rating = GetInt(artist, "userRating");
                    if (!string.IsNullOrEmpty(id) && rating > 0) ratings[id] = rating;
                }
            }
            return ratings;
        }

        //works for song, album and artist ids
        public void SetRating(string id, int rating, CancellationToken ct)
        {
            Call("setRating", Args("id", id, "rating", rating.ToString(Inv)), ct);
        }

        public void Star(IList<string> songIds, CancellationToken ct)
        {
            var args = new List<KeyValuePair<string, string>>();
            foreach (string id in songIds) args.Add(new KeyValuePair<string, string>("id", id));
            Call("star", args, ct);
        }

        //each id/time pair adds one play, the same id can appear more than once
        public void Scrobble(IList<string> songIds, IList<long> timesMs, CancellationToken ct)
        {
            var args = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < songIds.Count; i++)
            {
                args.Add(new KeyValuePair<string, string>("id", songIds[i]));
                args.Add(new KeyValuePair<string, string>("time", timesMs[i].ToString(Inv)));
            }
            args.Add(new KeyValuePair<string, string>("submission", "true"));
            Call("scrobble", args, ct);
        }

        //readable explanation of an error for the settings window and log
        public static string Describe(Exception ex)
        {
            var subsonic = ex as SubsonicException;
            if (subsonic == null) return ex.Message;
            switch (subsonic.Code)
            {
                case 40: return "Navidrome rejected the login: wrong username or password.";
                case 41: return "Navidrome does not accept token logins for this account (this happens with LDAP or reverse proxy authentication).";
                case 50: return "This Navidrome account is not allowed to do that.";
                case 70: return "Navidrome could not find the requested item.";
                default: return "Navidrome returned error " + subsonic.Code + ": " + subsonic.Message;
            }
        }

        private Dictionary<string, object> Call(string method, List<KeyValuePair<string, string>> extra, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (RequestCount > 0 && delayMs > 0) Wait(delayMs, ct);
                RequestCount++;
                try
                {
                    string body = Download(BuildUrl(method, extra));
                    return ParseResponse(body);
                }
                catch (WebException ex)
                {
                    if (!IsTransient(ex) || attempt >= MaxAttempts)
                        throw new IOException(method + " request failed: " + DescribeWebError(ex), ex);
                    //back off a little longer on each retry
                    Wait(1000 * attempt, ct);
                }
            }
        }

        private string BuildUrl(string method, List<KeyValuePair<string, string>> extra)
        {
            string salt = NewSalt();
            var sb = new StringBuilder(restUrl).Append(method).Append(".view?");
            AppendParam(sb, "u", username);
            AppendParam(sb, "t", Md5Hex(password + salt));
            AppendParam(sb, "s", salt);
            AppendParam(sb, "v", ApiVersion);
            AppendParam(sb, "c", ClientName);
            AppendParam(sb, "f", "json");
            if (extra != null)
            {
                foreach (var kv in extra) AppendParam(sb, kv.Key, kv.Value);
            }
            return sb.ToString();
        }

        private static void AppendParam(StringBuilder sb, string key, string value)
        {
            if (sb[sb.Length - 1] != '?') sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value ?? ""));
        }

        private static string Download(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.UserAgent = ClientName;
            request.Accept = "application/json";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private Dictionary<string, object> ParseResponse(string body)
        {
            Dictionary<string, object> doc = null;
            try
            {
                doc = json.DeserializeObject(body) as Dictionary<string, object>;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }

            Dictionary<string, object> root = doc == null ? null : GetDict(doc, "subsonic-response");
            if (root == null)
                throw new IOException("the server did not return a Subsonic API response, check the server URL");

            if (!string.Equals(GetString(root, "status"), "ok", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> error = GetDict(root, "error");
                throw new SubsonicException(GetInt(error, "code"), GetString(error, "message") ?? "unknown error");
            }
            return root;
        }

        private static bool IsTransient(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout:
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.PipelineFailure:
                    return true;
                case WebExceptionStatus.ProtocolError:
                    var response = ex.Response as HttpWebResponse;
                    return response != null && (int)response.StatusCode >= 500;
                default:
                    return false;
            }
        }

        private static string DescribeWebError(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (ex.Status == WebExceptionStatus.ProtocolError && response != null)
                return "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
            if (ex.Status == WebExceptionStatus.TrustFailure)
                return "the server's certificate is not trusted by Windows";
            if (ex.Status == WebExceptionStatus.NameResolutionFailure)
                return "the server name could not be found";
            return ex.Message;
        }

        private static NdSong ParseSong(Dictionary<string, object> d)
        {
            object starred;
            return new NdSong
            {
                Id = GetString(d, "id"),
                Title = GetString(d, "title"),
                Artist = GetString(d, "artist"),
                Album = GetString(d, "album"),
                AlbumId = GetString(d, "albumId"),
                ArtistId = GetString(d, "artistId"),
                Path = GetString(d, "path"),
                Track = GetInt(d, "track"),
                Disc = GetInt(d, "discNumber"),
                DurationSeconds = GetInt(d, "duration"),
                UserRating = GetInt(d, "userRating"),
                PlayCount = GetInt(d, "playCount"),
                Starred = d.TryGetValue("starred", out starred) && starred != null,
                Played = ParseIsoDate(GetString(d, "played"))
            };
        }

        private static DateTime? ParseIsoDate(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            DateTime parsed;
            if (DateTime.TryParse(value, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                return parsed;
            return null;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        //arrays arrive as object[], but a lone item can arrive as a bare object
        private static List<object> GetList(Dictionary<string, object> d, string key)
        {
            var list = new List<object>();
            object value;
            if (d == null || !d.TryGetValue(key, out value) || value == null) return list;
            if (value is Dictionary<string, object>)
            {
                list.Add(value);
                return list;
            }
            var items = value as IEnumerable;
            if (items != null && !(value is string))
            {
                foreach (object item in items) list.Add(item);
            }
            return list;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToString(value, Inv);
        }

        private static int GetInt(Dictionary<string, object> d, string key)
        {
            object value;
            if (d == null || !d.TryGetValue(key, out value) || value == null) return 0;
            try
            {
                return Convert.ToInt32(value, Inv);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static List<KeyValuePair<string, string>> Args(params string[] pairs)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) list.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
            return list;
        }

        private static string NewSalt()
        {
            var bytes = new byte[8];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return ToHex(bytes);
        }

        private static string Md5Hex(string value)
        {
            using (MD5 md5 = MD5.Create()) return ToHex(md5.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", Inv));
            return sb.ToString();
        }

        private static void Wait(int ms, CancellationToken ct)
        {
            if (ct.WaitHandle.WaitOne(ms)) ct.ThrowIfCancellationRequested();
        }
    }
}
