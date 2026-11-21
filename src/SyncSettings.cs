//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace MusicBeePlugin
{
    //user settings, stored as xml in the MusicBee persistent storage folder
    public class SyncSettings
    {
        public string ServerUrl = "";
        public string Username = "";

        //password encrypted with Windows DPAPI for the current Windows user, never stored in plain text
        public string ProtectedPassword = "";

        public bool SyncPlayCounts = true;
        public bool SyncRatings = true;
        public bool SyncLoved = true;
        public bool SyncAlbumRatings = true;
        public bool SyncArtistRatings = true;
        public RatingRounding Rounding = RatingRounding.Up;
        public bool ForceRatings = false;
        public bool PreviewOnly = false;
        public int RequestDelayMs = 50;

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NavidromeSync.MusicBee");

        [XmlIgnore]
        public string Password
        {
            get
            {
                if (string.IsNullOrEmpty(ProtectedPassword)) return "";
                try
                {
                    byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(data);
                }
                catch (Exception)
                {
                    //happens if the settings file was copied from another Windows user or machine, so the password must be re-entered
                    return "";
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    ProtectedPassword = "";
                    return;
                }
                byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
                ProtectedPassword = Convert.ToBase64String(data);
            }
        }

        [XmlIgnore]
        public bool IsConfigured
        {
            get
            {
                return !string.IsNullOrWhiteSpace(ServerUrl)
                    && !string.IsNullOrWhiteSpace(Username)
                    && !string.IsNullOrEmpty(Password);
            }
        }

        public SyncSettings Clone()
        {
            return (SyncSettings)MemberwiseClone();
        }

        public static SyncSettings Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new SyncSettings();
                var serialiser = new XmlSerializer(typeof(SyncSettings));
                using (var stream = File.OpenRead(path))
                {
                    var loaded = serialiser.Deserialize(stream) as SyncSettings;
                    return loaded ?? new SyncSettings();
                }
            }
            catch (Exception)
            {
                //an unreadable settings file falls back to defaults rather than stopping MusicBee from loading the plugin
                return new SyncSettings();
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            //write to a temporary file first so a failed save can't leave a half-written settings file behind
            string tempPath = path + ".tmp";
            var serialiser = new XmlSerializer(typeof(SyncSettings));
            using (var stream = File.Create(tempPath))
            {
                serialiser.Serialize(stream, this);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }
    }
}
