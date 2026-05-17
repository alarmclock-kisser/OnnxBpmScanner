using System.Drawing;
using System.Globalization;
using System.Text.Json;

namespace OnnxBpmScanner.Forms.Service
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const string AppName = "Onnx BPM Scanner";
        private const string StartupRegistryValueName = "OnnxBpmScanner";

        private readonly string _settingsPath;
        private readonly Settings _settings;
        private readonly ScannerWorker _worker;
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _lastTrackItem;
        private readonly ContextMenuStrip _contextMenu;
        private WindowMain? _mainWindow;
        private bool _isExiting;

        public TrayApplicationContext()
        {
            this._settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            this._settings = LoadSettings(this._settingsPath);

            if (this._settings.EnableAtStartup)
            {
                SetStartup();
            }
            else
            {
                RemoveStartup();
            }

            this._worker = new ScannerWorker(this._settings);
            this._worker.Start();

            this._statusItem = new ToolStripMenuItem("Status: Idle") { Enabled = false };
            this._lastTrackItem = new ToolStripMenuItem() { Enabled = false, Visible = false };
            this._contextMenu = new ContextMenuStrip();
            this._notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = AppName
            };

            this.BuildMenu();
            this._notifyIcon.ContextMenuStrip = this._contextMenu;
            this._notifyIcon.DoubleClick += (_, _) => this.ShowMainWindow();

            this._worker.StateChanged += this.OnWorkerStateChanged;
            this.UpdateStatus();
        }

        private void BuildMenu()
        {
            this._contextMenu.Items.Clear();

            var showGuiItem = new ToolStripMenuItem("Show GUI");
            showGuiItem.Click += (_, _) => this.ShowMainWindow();

            var settingsMenu = new ToolStripMenuItem("Settings");
            settingsMenu.DropDownItems.Add(this.CreateStartupItem());
            settingsMenu.DropDownItems.Add(this.CreateDirectMlDeviceMenu());
            settingsMenu.DropDownItems.Add(this.CreateExtensionsMenu());
            settingsMenu.DropDownItems.Add(this.CreateDirectoriesMenu());
            settingsMenu.DropDownItems.Add(this.CreateExcludeDirectoriesMenu());
            settingsMenu.DropDownItems.Add(this.CreateMaxDurationMenu());
            settingsMenu.DropDownItems.Add(this.CreateRoundMenu());
            settingsMenu.DropDownItems.Add(this.CreateThreadsMenu());

            var rescanItem = new ToolStripMenuItem("Rescan All");
            rescanItem.Click += (_, _) => this._worker.RescanAll();

            var stopMenuItem = new ToolStripMenuItem("Stop Service");
            stopMenuItem.Click += (_, _) => this.ExitApplication();

            this._contextMenu.Items.Add(showGuiItem);
            this._contextMenu.Items.Add(new ToolStripSeparator());
            this._contextMenu.Items.Add(this._statusItem);
            this._contextMenu.Items.Add(this._lastTrackItem);
            this._contextMenu.Items.Add(new ToolStripSeparator());
            this._contextMenu.Items.Add(settingsMenu);
            this._contextMenu.Items.Add(rescanItem);
            this._contextMenu.Items.Add(stopMenuItem);
        }

        private ToolStripMenuItem CreateStartupItem()
        {
            var startupItem = new ToolStripMenuItem("Run at Startup")
            {
                CheckOnClick = true,
                Checked = this._settings.EnableAtStartup
            };

            startupItem.CheckedChanged += (_, _) =>
            {
                this._settings.EnableAtStartup = startupItem.Checked;
                if (this._settings.EnableAtStartup)
                {
                    SetStartup();
                }
                else
                {
                    RemoveStartup();
                }

                this.SaveSettings();
            };

            return startupItem;
        }

        private ToolStripMenuItem CreateExtensionsMenu()
        {
            var extensionsMenu = new ToolStripMenuItem("Extensions to Watch");
            string[] supportedExtensions = [".mp3", ".wav", ".flac", ".ogg", ".m4a"];

            foreach (string ext in supportedExtensions)
            {
                var extItem = new ToolStripMenuItem(ext)
                {
                    CheckOnClick = true,
                    Checked = this._settings.ExtensionsToWatch.Contains(ext, StringComparer.OrdinalIgnoreCase)
                };

                extItem.CheckedChanged += (_, _) =>
                {
                    var list = this._settings.ExtensionsToWatch.ToList();
                    if (extItem.Checked)
                    {
                        if (!list.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(ext);
                        }
                    }
                    else
                    {
                        list.RemoveAll(value => string.Equals(value, ext, StringComparison.OrdinalIgnoreCase));
                    }

                    this._settings.ExtensionsToWatch = list.ToArray();
                    this.SaveSettings();
                };

                extensionsMenu.DropDownItems.Add(extItem);
            }

            return extensionsMenu;
        }

        private ToolStripMenuItem CreateDirectMlDeviceMenu()
        {
            string currentText = this._worker.InitializedDeviceId.HasValue
                ? $"Set DirectML Device ID... (current: {this._worker.InitializedDeviceId.Value})"
                : $"Set DirectML Device ID... (configured: {this._settings.DirectMlDeviceId})";

            var deviceMenu = new ToolStripMenuItem("DirectML Device ID");
            var setDeviceItem = new ToolStripMenuItem(currentText);
            setDeviceItem.Click += (_, _) =>
            {
                int availableCount = this._worker.DirectMlDevices.Count;
                string availableDevices = availableCount == 0
                    ? "No DirectML devices reported. The service will fall back as needed."
                    : string.Join(Environment.NewLine, this._worker.DirectMlDevices.Select((name, index) => $"{index}: {name}"));

                string prompt = $"Available DirectML Device IDs:{Environment.NewLine}{availableDevices}{Environment.NewLine}{Environment.NewLine}Enter the device id to use ({(availableCount > 0 ? $"0-{availableCount - 1}" : "0")}).";
                string input = Microsoft.VisualBasic.Interaction.InputBox(prompt, "DirectML Device ID", this._settings.DirectMlDeviceId.ToString(CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(input))
                {
                    return;
                }

                if (!int.TryParse(input, out int result))
                {
                    MessageBox.Show("Please enter a valid integer device id.", "DirectML Device ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (result < 0 || (availableCount > 0 && result >= availableCount))
                {
                    MessageBox.Show($"Please enter a device id between 0 and {Math.Max(0, availableCount - 1)}.", "DirectML Device ID", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int previousDeviceId = this._settings.DirectMlDeviceId;
                this._settings.DirectMlDeviceId = result;

                if (!this._worker.InitializeModel())
                {
                    this._settings.DirectMlDeviceId = previousDeviceId;
                    this._worker.InitializeModel();
                    MessageBox.Show("The selected DirectML device could not initialize the model.", "DirectML Device ID", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                this.SaveSettings();
                setDeviceItem.Text = $"Set DirectML Device ID... (current: {this._worker.InitializedDeviceId ?? this._settings.DirectMlDeviceId})";

                if (this._mainWindow != null && !this._mainWindow.IsDisposed)
                {
                    this._mainWindow.ApplyServiceSettings(this._settings, this._worker.InitializedDeviceId);
                }
            };

            deviceMenu.DropDownItems.Add(setDeviceItem);
            return deviceMenu;
        }

        private ToolStripMenuItem CreateDirectoriesMenu()
        {
            var dirsMenu = new ToolStripMenuItem("Directories to Watch");

            void Rebuild()
            {
                dirsMenu.DropDownItems.Clear();
                foreach (string dir in this._settings.DirectoriesToWatch)
                {
                    var dirItem = new ToolStripMenuItem(dir) { ToolTipText = "Click to remove" };
                    dirItem.Click += (_, _) =>
                    {
                        this._settings.DirectoriesToWatch = this._settings.DirectoriesToWatch
                            .Where(value => !string.Equals(value, dir, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        this.SaveSettings();
                        Rebuild();
                    };
                    dirsMenu.DropDownItems.Add(dirItem);
                }

                dirsMenu.DropDownItems.Add(new ToolStripSeparator());
                var addDirItem = new ToolStripMenuItem("Add Directory...");
                addDirItem.Click += (_, _) =>
                {
                    using var dialog = new FolderBrowserDialog();
                    if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    {
                        if (!this._settings.DirectoriesToWatch.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                        {
                            this._settings.DirectoriesToWatch = [.. this._settings.DirectoriesToWatch, dialog.SelectedPath];
                            this.SaveSettings();
                            Rebuild();
                        }
                    }
                };

                dirsMenu.DropDownItems.Add(addDirItem);
            }

            Rebuild();
            return dirsMenu;
        }

        private ToolStripMenuItem CreateExcludeDirectoriesMenu()
        {
            var excludeDirsMenu = new ToolStripMenuItem("Directories to Exclude");

            void Rebuild()
            {
                excludeDirsMenu.DropDownItems.Clear();
                foreach (string dir in this._settings.DirectoriesToExclude)
                {
                    var dirItem = new ToolStripMenuItem(dir) { ToolTipText = "Click to remove" };
                    dirItem.Click += (_, _) =>
                    {
                        this._settings.DirectoriesToExclude = this._settings.DirectoriesToExclude
                            .Where(value => !string.Equals(value, dir, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        this.SaveSettings();
                        Rebuild();
                    };
                    excludeDirsMenu.DropDownItems.Add(dirItem);
                }

                excludeDirsMenu.DropDownItems.Add(new ToolStripSeparator());
                var addItem = new ToolStripMenuItem("Add Exclude Directory...");
                addItem.Click += (_, _) =>
                {
                    using var dialog = new FolderBrowserDialog();
                    if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    {
                        if (!this._settings.DirectoriesToExclude.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                        {
                            this._settings.DirectoriesToExclude = [.. this._settings.DirectoriesToExclude, dialog.SelectedPath];
                            this.SaveSettings();
                            Rebuild();
                        }
                    }
                };

                excludeDirsMenu.DropDownItems.Add(addItem);
            }

            Rebuild();
            return excludeDirsMenu;
        }

        private ToolStripMenuItem CreateMaxDurationMenu()
        {
            var durationMenu = new ToolStripMenuItem("Max Duration (seconds)");
            var setDurationItem = new ToolStripMenuItem($"Set Max Duration... ({this._settings.MaxDurationSeconds}s)");
            setDurationItem.Click += (_, _) =>
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox("Enter max duration in seconds:", "Max Duration", this._settings.MaxDurationSeconds.ToString(CultureInfo.InvariantCulture));
                if (int.TryParse(input, out int result) && result > 0)
                {
                    this._settings.MaxDurationSeconds = result;
                    setDurationItem.Text = $"Set Max Duration... ({this._settings.MaxDurationSeconds}s)";
                    this.SaveSettings();
                }
            };

            durationMenu.DropDownItems.Add(setDurationItem);
            return durationMenu;
        }

        private ToolStripMenuItem CreateRoundMenu()
        {
            var roundMenu = new ToolStripMenuItem("Round to");
            var setRoundItem = new ToolStripMenuItem($"Set Round to... ({this._settings.Round.ToString("0.###", CultureInfo.InvariantCulture)})");
            setRoundItem.Click += (_, _) =>
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox("Enter the BPM rounding step (for example 0, 0.25, 0.5 or 1.0):", "Round to", this._settings.Round.ToString("0.###", CultureInfo.InvariantCulture));
                if (string.IsNullOrWhiteSpace(input))
                {
                    return;
                }

                if (!float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) || result < 0)
                {
                    MessageBox.Show("Please enter a valid non-negative number using '.' as decimal separator.", "Round to", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                this._settings.Round = result;
                this.SaveSettings();
                setRoundItem.Text = $"Set Round to... ({this._settings.Round.ToString("0.###", CultureInfo.InvariantCulture)})";

                if (this._mainWindow != null && !this._mainWindow.IsDisposed)
                {
                    this._mainWindow.ApplyServiceSettings(this._settings, this._worker.InitializedDeviceId);
                }
            };

            roundMenu.DropDownItems.Add(setRoundItem);
            return roundMenu;
        }

        private ToolStripMenuItem CreateThreadsMenu()
        {
            var threadsMenu = new ToolStripMenuItem("Parallel Threads");
            var setThreadsItem = new ToolStripMenuItem($"Set Max Threads... ({this._settings.MaxThreads})");
            setThreadsItem.Click += (_, _) =>
            {
                string input = Microsoft.VisualBasic.Interaction.InputBox("Enter max parallel threads:", "Parallel Threads", this._settings.MaxThreads.ToString(CultureInfo.InvariantCulture));
                if (int.TryParse(input, out int result) && result > 0)
                {
                    this._settings.MaxThreads = result;
                    setThreadsItem.Text = $"Set Max Threads... ({this._settings.MaxThreads})";
                    this.SaveSettings();
                    this._worker.ApplyThreads();
                }
            };

            threadsMenu.DropDownItems.Add(setThreadsItem);
            return threadsMenu;
        }

        private void OnWorkerStateChanged()
        {
            if (this._contextMenu.IsDisposed)
            {
                return;
            }

            if (this._contextMenu.InvokeRequired)
            {
                this._contextMenu.BeginInvoke(new MethodInvoker(this.UpdateStatus));
                return;
            }

            this.UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (this._worker.QueueCount == 0)
            {
                this._statusItem.Text = $"Status: Idle (Processed: {this._worker.ProcessedCount})";
                this._notifyIcon.Text = Ellipsize(AppName + " - Idle", 63);
            }
            else
            {
                double progressPercent = Math.Clamp(this._worker.CurrentAnalysisProgress, 0.0, 1.0) * 100.0;
                this._statusItem.Text = $"Status: Processing... {progressPercent:F0}% ({this._worker.QueueCount} pending / {this._worker.ProcessedCount} processed)";
                this._notifyIcon.Text = Ellipsize($"{AppName} - {progressPercent:F0}% ({this._worker.QueueCount} left)", 63);
            }

            if (this._worker.LastScannedTrack is null)
            {
                this._lastTrackItem.Visible = false;
            }
            else
            {
                this._lastTrackItem.Visible = true;
                this._lastTrackItem.Text = FormatLastScannedTrack(this._worker.LastScannedTrack.FileName, this._worker.LastScannedTrack.Bpm);
            }
        }

        private void ShowMainWindow()
        {
            if (this._mainWindow == null || this._mainWindow.IsDisposed)
            {
                this._mainWindow = new WindowMain();
                this._mainWindow.FormClosed += (_, _) =>
                {
                    if (this._isExiting)
                    {
                        this._mainWindow = null;
                    }
                };
            }

            this._mainWindow.ApplyServiceSettings(this._settings, this._worker.InitializedDeviceId);
            this._mainWindow.ShowFromTray();
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(this._settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(this._settingsPath, json);
                this._worker.ApplyWatchers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save settings: {ex.Message}", "Settings Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExitApplication()
        {
            this._isExiting = true;
            this._worker.StateChanged -= this.OnWorkerStateChanged;
            this._worker.Stop();
            this._notifyIcon.Visible = false;
            this._mainWindow?.CloseForExit();
            this.ExitThread();
        }

        protected override void ExitThreadCore()
        {
            this._worker.Dispose();
            this._notifyIcon.Dispose();
            this._contextMenu.Dispose();
            base.ExitThreadCore();
        }

        private static Settings LoadSettings(string settingsPath)
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    Settings? settings = JsonSerializer.Deserialize<Settings>(json);
                    if (settings != null)
                    {
                        settings.Normalize();
                        return settings;
                    }
                }
            }
            catch
            {
            }

            Settings fallback = new();
            fallback.Normalize();
            return fallback;
        }

        private static string FormatLastScannedTrack(string fileName, double bpm)
        {
            string suffix = $" [{bpm.ToString("F2", CultureInfo.InvariantCulture)}]";
            int availableTitleLength = Math.Max(1, 48 - suffix.Length);
            return Ellipsize(fileName, availableTitleLength) + suffix;
        }

        private static string Ellipsize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            if (maxLength <= 3)
            {
                return value[..maxLength];
            }

            return value[..(maxLength - 3)].TrimEnd() + "...";
        }

        private static void SetStartup()
        {
            try
            {
                string execPath = Application.ExecutablePath;
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
                key?.SetValue(StartupRegistryValueName, execPath);
            }
            catch
            {
            }
        }

        private static void RemoveStartup()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", writable: true);
                if (key?.GetValue(StartupRegistryValueName) != null)
                {
                    key.DeleteValue(StartupRegistryValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
            }
        }
    }
}
