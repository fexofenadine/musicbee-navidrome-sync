//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MusicBeePlugin
{
    //collects everything a run did, written to a text file when the run ends
    internal class SyncLog
    {
        private readonly string path;
        private readonly List<string> info = new List<string>();
        private readonly List<string> changes = new List<string>();
        private readonly List<string> notFound = new List<string>();
        private readonly List<string> ambiguous = new List<string>();
        private readonly List<string> problems = new List<string>();

        public SyncLog(string path)
        {
            this.path = path;
        }

        public void Info(string line) { info.Add(Stamp(line)); }
        public void Change(string line) { changes.Add(line); }
        public void NotFound(string line) { notFound.Add(line); }
        public void Ambiguous(string line) { ambiguous.Add(line); }
        public void Problem(string line) { problems.Add(Stamp(line)); }

        //returns the log path, or null if it could not be written
        public string Save(SyncSummary summary)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Navidrome Sync log");
                sb.AppendLine("==================");
                foreach (string line in info) sb.AppendLine(line);

                sb.AppendLine();
                sb.AppendLine("== Summary ==");
                sb.AppendLine(summary.ToText());

                AppendSection(sb, summary.Preview ? "Changes that would be made" : "Changes (any that failed are listed under Problems)", changes);
                AppendSection(sb, "Problems", problems);
                AppendSection(sb, "Not found in Navidrome", notFound);
                AppendSection(sb, "More than one possible match in Navidrome (skipped)", ambiguous);

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AppendSection(StringBuilder sb, string title, List<string> lines)
        {
            sb.AppendLine();
            sb.AppendLine("== " + title + " (" + lines.Count + ") ==");
            foreach (string line in lines) sb.AppendLine(line);
        }

        private static string Stamp(string line)
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line;
        }
    }
}
