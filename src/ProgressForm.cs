//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    //shows progress while the sync runs on a background thread, then the summary when it ends
    internal class ProgressForm : Form, ISyncProgress
    {
        private const int ProgressUpdateIntervalMs = 100;

        private readonly SyncSettings settings;
        private readonly Plugin.MusicBeeApiInterface mb;
        private readonly string logPath;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly Stopwatch progressThrottle = Stopwatch.StartNew();

        private readonly Label phaseLabel;
        private readonly Label detailLabel;
        private readonly ProgressBar bar;
        private readonly TextBox summaryBox;
        private readonly Button closeButton;
        private readonly Button logButton;

        private volatile bool running;
        private string savedLogPath;

        public ProgressForm(SyncSettings settings, Plugin.MusicBeeApiInterface mb, string storageFolder)
        {
            this.settings = settings;
            this.mb = mb;
            logPath = Path.Combine(storageFolder, "last-sync.log");

            Text = settings.PreviewOnly ? "Navidrome Sync (preview only)" : "Navidrome Sync";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 330);
            Padding = new Padding(12);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            phaseLabel = new Label { AutoSize = true, Text = "Starting", Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
            bar = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, Height = 20 };
            detailLabel = new Label { AutoSize = true, Text = "", Margin = new Padding(0, 6, 0, 6) };
            summaryBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Visible = false
            };

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            closeButton = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0) };
            logButton = new Button { Text = "Open log", AutoSize = true, MinimumSize = new Size(90, 0), Enabled = false };
            closeButton.Click += OnCloseClick;
            logButton.Click += OnLogClick;
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(logButton);

            layout.Controls.Add(phaseLabel, 0, 0);
            layout.Controls.Add(bar, 0, 1);
            layout.Controls.Add(detailLabel, 0, 2);
            layout.Controls.Add(summaryBox, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout);
        }

        public void RequestCancel()
        {
            if (running) cts.Cancel();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            running = true;
            var thread = new Thread(RunSync) { IsBackground = true, Name = "NavidromeSync" };
            thread.Start();
        }

        private void RunSync()
        {
            var engine = new SyncEngine(mb, settings, new SyncLog(logPath), this);
            SyncSummary summary = engine.Run(cts.Token);
            OnUiThread(() => Finished(summary));
        }

        private void Finished(SyncSummary summary)
        {
            running = false;
            savedLogPath = summary.LogPath;

            bar.Style = ProgressBarStyle.Continuous;
            bar.Maximum = 1;
            bar.Value = summary.Error == null && !summary.Cancelled ? 1 : 0;

            if (summary.Error != null) phaseLabel.Text = "Sync stopped with an error";
            else if (summary.Cancelled) phaseLabel.Text = "Sync cancelled";
            else if (summary.Preview) phaseLabel.Text = "Preview complete, nothing was changed";
            else phaseLabel.Text = "Sync complete";

            detailLabel.Text = summary.LogPath != null ? "Full details are in the log." : "The log could not be written.";
            summaryBox.Lines = summary.ToText().Replace("\r", "").Split('\n');
            summaryBox.Visible = true;

            closeButton.Text = "Close";
            closeButton.Enabled = true;
            logButton.Enabled = savedLogPath != null;
        }

        public void Phase(string text)
        {
            OnUiThread(() =>
            {
                phaseLabel.Text = text;
                detailLabel.Text = "";
                bar.Style = ProgressBarStyle.Marquee;
            });
        }

        public void Progress(int current, int total, string detail)
        {
            //called from the sync thread, so updates are throttled to keep the window responsive
            bool final = total > 0 && current >= total;
            if (!final && progressThrottle.ElapsedMilliseconds < ProgressUpdateIntervalMs) return;
            progressThrottle.Restart();

            OnUiThread(() =>
            {
                if (total > 0)
                {
                    bar.Style = ProgressBarStyle.Continuous;
                    bar.Maximum = total;
                    bar.Value = Math.Max(0, Math.Min(current, total));
                }
                detailLabel.Text = detail ?? "";
            });
        }

        private void OnCloseClick(object sender, EventArgs e)
        {
            if (running)
            {
                cts.Cancel();
                closeButton.Enabled = false;
                closeButton.Text = "Cancelling";
                return;
            }
            Close();
        }

        private void OnLogClick(object sender, EventArgs e)
        {
            if (savedLogPath == null || !File.Exists(savedLogPath)) return;
            try
            {
                Process.Start("notepad.exe", "\"" + savedLogPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the log: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            //closing mid-sync cancels it, and the window stays open until the sync has stopped cleanly
            //but MusicBee shutting down is never blocked
            if (running && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                cts.Cancel();
                closeButton.Enabled = false;
                closeButton.Text = "Cancelling";
            }
            else if (running)
            {
                cts.Cancel();
            }
            base.OnFormClosing(e);
        }

        private void OnUiThread(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                //the window was closed between the check and the call
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cts.Dispose();
            base.Dispose(disposing);
        }
    }
}
