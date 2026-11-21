//part of Navidrome Sync for MusicBee, released under the GNU General Public Licence v3.0

using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    //settings window, opened from the Configure button in MusicBee's plugin preferences
    internal class SettingsForm : Form
    {
        private const int FieldWidth = 320;
        private const int WrapWidth = 440;

        private readonly TextBox urlBox;
        private readonly TextBox userBox;
        private readonly TextBox passwordBox;
        private readonly Button testButton;
        private readonly Label testResultLabel;

        private readonly CheckBox playCountsBox;
        private readonly CheckBox ratingsBox;
        private readonly CheckBox lovedBox;
        private readonly CheckBox albumRatingsBox;
        private readonly CheckBox artistRatingsBox;

        private readonly ComboBox roundingBox;
        private readonly CheckBox forceBox;
        private readonly CheckBox previewBox;
        private readonly NumericUpDown delayBox;

        private readonly SyncSettings original;

        public SyncSettings Result { get; private set; }

        public SettingsForm(SyncSettings current)
        {
            original = current;

            Text = "Navidrome Sync settings";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };

            //server connection
            var serverGrid = NewGrid(2);
            urlBox = AddTextRow(serverGrid, "Server URL:", current.ServerUrl, false);
            AddHintRow(serverGrid, "For example https://music.example.com");
            userBox = AddTextRow(serverGrid, "Username:", current.Username, false);
            passwordBox = AddTextRow(serverGrid, "Password:", current.Password, true);

            testButton = new Button { Text = "Test connection", AutoSize = true };
            testButton.Click += OnTestClick;
            testResultLabel = new Label { AutoSize = true, MaximumSize = new Size(FieldWidth - 120, 0), Margin = new Padding(8, 8, 0, 0) };
            var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            testRow.Controls.Add(testButton);
            testRow.Controls.Add(testResultLabel);
            serverGrid.Controls.Add(new Label(), 0, serverGrid.RowCount);
            serverGrid.Controls.Add(testRow, 1, serverGrid.RowCount);
            serverGrid.RowCount++;
            root.Controls.Add(NewGroup("Navidrome server", serverGrid));

            //what to sync
            var syncPanel = NewStack();
            playCountsBox = AddCheck(syncPanel, "Track play counts (also moves last played forward)", current.SyncPlayCounts);
            ratingsBox = AddCheck(syncPanel, "Track ratings", current.SyncRatings);
            lovedBox = AddCheck(syncPanel, "Loved tracks (added as starred, never removed)", current.SyncLoved);
            albumRatingsBox = AddCheck(syncPanel, "Album ratings (average of the album's track ratings)", current.SyncAlbumRatings);
            artistRatingsBox = AddCheck(syncPanel, "Artist ratings (average of the artist's track ratings)", current.SyncArtistRatings);
            root.Controls.Add(NewGroup("What to sync", syncPanel));

            //behaviour
            var optionsGrid = NewGrid(2);
            roundingBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = FieldWidth };
            roundingBox.Items.AddRange(new object[]
            {
                "Round up (3.5 stars becomes 4)",
                "Round down (3.5 stars becomes 3)",
                "Round to nearest (halves round up, mainly affects averages)"
            });
            roundingBox.SelectedIndex = Math.Max(0, Math.Min(2, (int)current.Rounding));
            AddRow(optionsGrid, "Half-star ratings:", roundingBox);

            delayBox = new NumericUpDown { Minimum = 0, Maximum = 5000, Increment = 10, Width = 90 };
            delayBox.Value = Math.Max(0, Math.Min(5000, current.RequestDelayMs));
            var delayRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            delayRow.Controls.Add(delayBox);
            delayRow.Controls.Add(new Label { Text = "milliseconds", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
            AddRow(optionsGrid, "Delay between requests:", delayRow);

            forceBox = new CheckBox { Text = "Allow lowering ratings already set in Navidrome", AutoSize = true, Checked = current.ForceRatings };
            previewBox = new CheckBox { Text = "Preview only: log what would change without changing anything", AutoSize = true, Checked = current.PreviewOnly };
            optionsGrid.Controls.Add(forceBox, 0, optionsGrid.RowCount);
            optionsGrid.SetColumnSpan(forceBox, 2);
            optionsGrid.RowCount++;
            optionsGrid.Controls.Add(previewBox, 0, optionsGrid.RowCount);
            optionsGrid.SetColumnSpan(previewBox, 2);
            optionsGrid.RowCount++;
            root.Controls.Add(NewGroup("Options", optionsGrid));

            //warning about scrobbles being forwarded elsewhere
            var warning = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(WrapWidth + 60, 0),
                Margin = new Padding(3, 8, 3, 0),
                Text = "Play counts are added to Navidrome as scrobbles, one per missing play. If this Navidrome account "
                    + "forwards scrobbles to Last.fm or ListenBrainz, turn that off in Navidrome's personal settings "
                    + "before syncing play counts, otherwise every backfilled play is forwarded too."
            };
            root.Controls.Add(warning);

            //ok and cancel
            var okButton = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 0) };
            var cancelButton = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel };
            okButton.Click += OnOkClick;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            root.Controls.Add(buttons);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            Controls.Add(root);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            string error = ValidateUrl(urlBox.Text);
            if (error != null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                urlBox.Focus();
                return;
            }

            SyncSettings updated = original.Clone();
            updated.ServerUrl = urlBox.Text.Trim();
            updated.Username = userBox.Text.Trim();
            updated.Password = passwordBox.Text;
            updated.SyncPlayCounts = playCountsBox.Checked;
            updated.SyncRatings = ratingsBox.Checked;
            updated.SyncLoved = lovedBox.Checked;
            updated.SyncAlbumRatings = albumRatingsBox.Checked;
            updated.SyncArtistRatings = artistRatingsBox.Checked;
            updated.Rounding = (RatingRounding)roundingBox.SelectedIndex;
            updated.ForceRatings = forceBox.Checked;
            updated.PreviewOnly = previewBox.Checked;
            updated.RequestDelayMs = (int)delayBox.Value;

            Result = updated;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnTestClick(object sender, EventArgs e)
        {
            string error = ValidateUrl(urlBox.Text);
            if (error != null)
            {
                ShowTestResult(error, false);
                return;
            }

            string url = urlBox.Text.Trim();
            string user = userBox.Text.Trim();
            string password = passwordBox.Text;
            testButton.Enabled = false;
            ShowTestResult("Connecting...", true);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string message;
                bool ok;
                try
                {
                    new SubsonicClient(url, user, password, 0).Ping(CancellationToken.None);
                    message = "Connected";
                    ok = true;
                }
                catch (Exception ex)
                {
                    message = SubsonicClient.Describe(ex);
                    ok = false;
                }

                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        testButton.Enabled = true;
                        ShowTestResult(message, ok);
                    }));
                }
                catch (InvalidOperationException)
                {
                    //the window was closed while the test was running
                }
            });
        }

        private void ShowTestResult(string message, bool ok)
        {
            testResultLabel.ForeColor = ok ? SystemColors.ControlText : Color.Firebrick;
            testResultLabel.Text = message;
        }

        private static string ValidateUrl(string text)
        {
            Uri uri;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "Enter the full server URL, starting with https:// or http://";
            return null;
        }

        private static TableLayoutPanel NewGrid(int columns)
        {
            var grid = new TableLayoutPanel { ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        private static FlowLayoutPanel NewStack()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        }

        private static GroupBox NewGroup(string title, Control content)
        {
            var group = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 0, 8)
            };
            group.Controls.Add(content);
            return group;
        }

        private static TextBox AddTextRow(TableLayoutPanel grid, string label, string value, bool isPassword)
        {
            var box = new TextBox { Width = FieldWidth, Text = value ?? "", UseSystemPasswordChar = isPassword };
            AddRow(grid, label, box);
            return box;
        }

        private static void AddHintRow(TableLayoutPanel grid, string hint)
        {
            var label = new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 4) };
            grid.Controls.Add(new Label { AutoSize = true }, 0, grid.RowCount);
            grid.Controls.Add(label, 1, grid.RowCount);
            grid.RowCount++;
        }

        private static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) }, 0, grid.RowCount);
            grid.Controls.Add(control, 1, grid.RowCount);
            grid.RowCount++;
        }

        private static CheckBox AddCheck(FlowLayoutPanel panel, string text, bool isChecked)
        {
            var box = new CheckBox { Text = text, AutoSize = true, Checked = isChecked };
            panel.Controls.Add(box);
            return box;
        }
    }
}
