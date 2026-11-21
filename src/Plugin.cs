//Navidrome Sync for MusicBee
//one-way sync of play counts, ratings and loved tracks from MusicBee to a Navidrome server
//inspired by rombat/musicbee-navidrome-sync: https://github.com/rombat/musicbee-navidrome-sync
//released under the GNU General Public Licence v3.0, see LICENCE

using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private const string PluginName = "Navidrome Sync";
        private const string MenuPath = "mnuTools/Sync ratings and playcounts to Navidrome";
        private const string HotkeyDescription = "Navidrome Sync: sync ratings and playcounts to Navidrome";

        private MusicBeeApiInterface mbApiInterface;
        private readonly PluginInfo about = new PluginInfo();
        private string storageFolder;
        private string settingsPath;
        private SyncSettings settings;
        private ProgressForm activeSync;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);

            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = PluginName;
            about.Description = "Syncs play counts, ratings and loved tracks to a Navidrome server (Tools menu)";
            about.Author = "Papi";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = (short)version.Major;
            about.VersionMinor = (short)version.Minor;
            about.Revision = (short)version.Build;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.StartupOnly;
            //0 tells MusicBee the plugin opens its own settings window from Configure
            about.ConfigurationPanelHeight = 0;

            storageFolder = Path.Combine(mbApiInterface.Setting_GetPersistentStoragePath(), "NavidromeSync");
            settingsPath = Path.Combine(storageFolder, "settings.xml");
            settings = SyncSettings.Load(settingsPath);

            //the hotkey description also lets the sync be bound to a key in MusicBee's hotkey preferences
            mbApiInterface.MB_AddMenuItem(MenuPath, HotkeyDescription, OnSyncMenuClick);
            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            ShowSettings();
            return true;
        }

        public void SaveSettings()
        {
            //nothing to do, settings are saved when the settings window is closed with OK
        }

        public void Close(PluginCloseReason reason)
        {
            if (activeSync != null && !activeSync.IsDisposed) activeSync.RequestCancel();
        }

        public void Uninstall()
        {
            try
            {
                if (Directory.Exists(storageFolder)) Directory.Delete(storageFolder, true);
            }
            catch (Exception)
            {
                //leftover files are harmless if they can't be removed
            }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            //no notifications are used
        }

        private void OnSyncMenuClick(object sender, EventArgs e)
        {
            //only one sync at a time, a second click brings the running one to the front
            if (activeSync != null && !activeSync.IsDisposed)
            {
                activeSync.Activate();
                return;
            }

            if (!settings.IsConfigured)
            {
                MessageBox.Show(MainWindow(), PluginName + " needs your Navidrome server details before it can run.",
                    PluginName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (!ShowSettings() || !settings.IsConfigured) return;
            }

            activeSync = new ProgressForm(settings.Clone(), mbApiInterface, storageFolder);
            activeSync.FormClosed += (s, args) => activeSync = null;
            activeSync.Show(MainWindow());
        }

        //returns true if the user saved changes
        private bool ShowSettings()
        {
            using (var form = new SettingsForm(settings.Clone()))
            {
                if (form.ShowDialog(MainWindow()) != DialogResult.OK || form.Result == null) return false;
                settings = form.Result;
                try
                {
                    settings.Save(settingsPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(MainWindow(), "The settings could not be saved: " + ex.Message,
                        PluginName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return true;
            }
        }

        private IWin32Window MainWindow()
        {
            IntPtr handle = mbApiInterface.MB_GetWindowHandle != null ? mbApiInterface.MB_GetWindowHandle() : IntPtr.Zero;
            return handle == IntPtr.Zero ? null : new WindowHandle(handle);
        }

        private class WindowHandle : IWin32Window
        {
            public WindowHandle(IntPtr handle)
            {
                Handle = handle;
            }

            public IntPtr Handle { get; private set; }
        }
    }
}
