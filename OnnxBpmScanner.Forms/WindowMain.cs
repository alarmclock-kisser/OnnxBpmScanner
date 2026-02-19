using OnnxBpmScanner.Core;
using OnnxBpmScanner.Runtime;
using System.ComponentModel;
using System.Diagnostics;
using Timer = System.Windows.Forms.Timer;

namespace OnnxBpmScanner.Forms
{
    public partial class WindowMain : Form
    {
        internal readonly OnnxService Onnx = new();

        internal List<string> ImportedAudioFiles = [];
        internal readonly BindingList<AudioFileEntry> AudioFileEntries = [];

        internal record AudioFileEntry(bool scanned = false, double? bpm = null, string name = "", double? elapsed = null);


        private Timer? InferenceTimer = null;
        private DateTime? InferenceStartTime = null;


        public WindowMain()
        {
            this.InitializeComponent();

            this.ListBox_BindToLog();
            this.ListBox_BindAudioFiles();

            this.Load += this.WindowMain_Load;

        }


        // Methods
        private void ListBox_BindToLog()
        {
            this.listBox_log.SuspendLayout();

            this.listBox_log.DataSource = null;
            this.listBox_log.DataSource = StaticLogger.LogEntriesBindingList;

            StaticLogger.LogAdded += (message) =>
            {
                if (this.listBox_log.InvokeRequired)
                {
                    this.listBox_log.Invoke(new Action(() =>
                    {
                        this.listBox_log.TopIndex = this.listBox_log.Items.Count - 1;
                    }));
                }
                else
                {
                    this.listBox_log.TopIndex = this.listBox_log.Items.Count - 1;
                }
            };

            this.listBox_log.ResumeLayout();
        }

        private void ListBox_BindAudioFiles()
        {
            this.listBox_audios.SuspendLayout();

            this.listBox_audios.DataSource = null;
            this.listBox_audios.DataSource = this.AudioFileEntries;

            this.listBox_audios.DrawMode = DrawMode.OwnerDrawFixed;
            this.listBox_audios.DrawItem += this.ListBox_Audios_DrawItem;

            this.listBox_audios.ResumeLayout();
        }




        // Events
        private void WindowMain_Load(object? sender, EventArgs e)
        {
            this.comboBox_devices.Items.Clear();
            this.comboBox_devices.Items.AddRange(this.Onnx.DirectMlDevices.ToArray());
            if (this.comboBox_devices.Items.Count > 0)
            {
                this.comboBox_devices.SelectedIndex = 0;
            }

            this.button_initialize.Text = this.Onnx.IsModelLoaded ? "Dispose" : "Initialize";
            this.comboBox_devices.Enabled = !this.Onnx.IsModelLoaded;

            StaticLogger.Log("Application started.");
        }

        private void ListBox_Audios_DrawItem(object? sender, DrawItemEventArgs e)
        {
            // Draw items like: <[x] or [✓] if scanned> <[bpm if not null]> <name>
            if (e.Index < 0 || e.Index >= this.listBox_audios.Items.Count)
            {
                return;
            }

            e.DrawBackground();

            // Obtain the item (it's the record AudioFileEntry)
            var item = this.listBox_audios.Items[e.Index];
            if (item is not AudioFileEntry entry)
            {
                // fallback to default drawing
                using var fore = new SolidBrush(SystemColors.WindowText);
                var font = e.Font ?? SystemFonts.DefaultFont;
                e.Graphics.DrawString(item?.ToString() ?? string.Empty, font, fore, e.Bounds.Location);
                e.DrawFocusRectangle();
                return;
            }

            // Build display text
            var status = entry.scanned ? "[✓]" : "[x]";
            var bpmPart = entry.bpm.HasValue ? $" [{entry.bpm.Value:0.###}]" : string.Empty;
            var text = $"{status}{bpmPart} {entry.name}";

            // Choose color depending on selection
            var textColor = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : SystemColors.WindowText;

            System.Windows.Forms.TextRenderer.DrawText(e.Graphics, text, e.Font, e.Bounds, textColor, System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.VerticalCenter);

            e.DrawFocusRectangle();
        }

        private void listBox_log_DoubleClick(object sender, EventArgs e)
        {
            // If single item selected, copy its text to clipboard
            if (this.listBox_log.SelectedItems.Count == 1)
            {
                var item = this.listBox_log.SelectedItem;
                if (item != null)
                {
                    Clipboard.SetText(item.ToString() ?? string.Empty);
                    StaticLogger.Log("Log entry copied to clipboard.");
                }
            }
            else
            {
                string concat = string.Join(Environment.NewLine, this.listBox_log.SelectedItems.Cast<object>().Select(i => i.ToString()));
                Clipboard.SetText(concat);
                StaticLogger.Log("Log entries copied to clipboard.");
            }
        }

        private void Timer_Inference_Tick(object? sender, EventArgs e)
        {
            if (this.InferenceStartTime.HasValue)
            {
                this.label_elapsed.Text = $"Elapsed: {(DateTime.Now - this.InferenceStartTime.Value).TotalSeconds:0.0}s";
            }
            else
            {
                this.label_elapsed.Text = "Elapsed: -:--";
            }
        }


        // Initialize ONNX
        private void button_initialize_Click(object sender, EventArgs e)
        {
            int index = this.comboBox_devices.SelectedIndex;
            if (index < 0 || index >= this.Onnx.DirectMlDevices.Count)
            {
                StaticLogger.Log("Invalid device selection.");
                return;
            }

            if (this.Onnx.IsModelLoaded)
            {
                // Dispose
                this.Onnx.DisposeSession();
                if (this.Onnx.IsModelLoaded)
                {
                    StaticLogger.Log("Failed to dispose existing model session.");
                    return;
                }
                else
                {
                    StaticLogger.Log("Existing model session disposed.");
                }

                this.button_initialize.Text = "Initialize";
                this.comboBox_devices.Enabled = true;
            }
            else
            {
                // Initialize
                try
                {
                    bool success = this.Onnx.LoadModelFromRessource(index);
                    if (success)
                    {
                        StaticLogger.Log($"Model initialized on device: {this.Onnx.DirectMlDevices[index]}");
                        this.button_initialize.Text = "Dispose";
                        this.comboBox_devices.Enabled = false;
                    }
                    else
                    {
                        StaticLogger.Log("Failed to initialize model.");
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Error initializing model: {ex.Message}");
                    return;
                }
            }
        }

        private void comboBox_devices_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (this.comboBox_devices.SelectedIndex < 0 || this.comboBox_devices.SelectedIndex >= this.Onnx.DirectMlDevices.Count)
            {
                this.button_initialize.Enabled = false;
                return;
            }

            this.button_initialize.Enabled = true;
        }


        // Audio File Import
        private void button_import_Click(object sender, EventArgs e)
        {
            // OFD at MyMusic
            var ofd = new OpenFileDialog()
            {
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Filter = "Audio Files|*.mp3;*.wav;*.flac",
                Multiselect = true,
                RestoreDirectory = true
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                foreach (var file in ofd.FileNames)
                {
                    this.ImportedAudioFiles.Add(file);
                    this.AudioFileEntries.Add(new AudioFileEntry(name: Path.GetFileNameWithoutExtension(file)));
                    StaticLogger.Log($"Added file: {file}");
                }
            }
        }

        private void button_browse_Click(object sender, EventArgs e)
        {
            // FBD in MyMusic
            var fbd = new FolderBrowserDialog()
            {
                Description = "Select a folder containing audio files",
                SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                ShowNewFolderButton = false
            };

            if (fbd.ShowDialog() == DialogResult.OK)
            {
                var audioFiles = Directory.GetFiles(fbd.SelectedPath, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var file in audioFiles)
                {
                    this.ImportedAudioFiles.Add(file);
                    this.AudioFileEntries.Add(new AudioFileEntry(name: Path.GetFileNameWithoutExtension(file)));
                    StaticLogger.Log($"Added file: {file}");
                }
            }
        }

        private void button_clear_Click(object sender, EventArgs e)
        {
            // Clear list
            this.ImportedAudioFiles.Clear();
            this.AudioFileEntries.Clear();
        }


        // Analysis (BPM scan)
        private async void button_analyze_Click(object sender, EventArgs e)
        {
            this.InferenceStartTime = DateTime.Now;
            this.InferenceTimer = new Timer() { Interval = 100 };
            this.InferenceTimer.Tick += this.Timer_Inference_Tick;
            this.InferenceTimer.Start();

            try
            {
                foreach (var file in this.ImportedAudioFiles)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    var progress = new Progress<double>(p =>
                    {
                        this.progressBar_inferencing.Value = Math.Clamp((int) (p * this.progressBar_inferencing.Maximum), 0, this.progressBar_inferencing.Maximum);
                    });

                    double? bpm = await this.Onnx.RunInferenceBpmEstimateAsync(file, progress);
                    sw.Stop();

                    // Optionally write tag
                    if (bpm.HasValue && this.checkBox_writeTags.Checked)
                    {
                        try
                        {
                            bool success = AudioObj.WriteBpmTagToFile(file, bpm.Value);
                            if (success)
                            {
                                await StaticLogger.LogAsync($"BPM tag written for {file}: {bpm.Value:0.###}");
                            }
                            else
                            {
                                await StaticLogger.LogAsync($"Failed to write BPM tag for {file}");
                            }
                        }
                        catch (Exception ex)
                        {
                            await StaticLogger.LogAsync($"Error writing BPM tag for {file}: {ex.Message}");
                        }
                    }

                    // Update the corresponding AudioFileEntry
                    var entry = this.AudioFileEntries.FirstOrDefault(e => e.name == Path.GetFileNameWithoutExtension(file));
                    if (entry != null)
                    {
                        // Build a new entry with updated bpm and scanned=true
                        var updatedEntry = entry with { bpm = bpm, scanned = true, elapsed = sw.Elapsed.TotalSeconds };
                        int index = this.AudioFileEntries.IndexOf(entry);
                        if (index >= 0)
                        {
                            this.AudioFileEntries[index] = updatedEntry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync($"Error during analysis: {ex.Message}");
            }
            finally
            {
                this.InferenceTimer?.Stop();
                this.InferenceTimer = null;
                this.InferenceStartTime = null;
            }

        }




        // Context menu for audio files listBox
        private void removeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Get selected item or item under mouse
            int index = this.listBox_audios.SelectedIndex >= 0 ? this.listBox_audios.SelectedIndex : this.listBox_audios.IndexFromPoint(this.listBox_audios.PointToClient(Cursor.Position));
            if (index >= 0 && index < this.AudioFileEntries.Count)
            {
                var entry = this.AudioFileEntries[index];
                string fullPath = this.ImportedAudioFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == entry.name) ?? string.Empty;
                // Remove from both lists
                this.AudioFileEntries.RemoveAt(index);
                this.ImportedAudioFiles.Remove(fullPath);
                StaticLogger.Log($"Removed file: {fullPath}");
            }
        }

        private void listBox_audios_MouseDown(object sender, MouseEventArgs e)
        {
            // Show context menu on right-click
            if (e.Button == MouseButtons.Right)
            {
                int index = this.listBox_audios.IndexFromPoint(e.Location);
                if (index >= 0 && index < this.listBox_audios.Items.Count)
                {
                    this.listBox_audios.SelectedIndex = index;
                    this.contextMenuStrip_audios.Show(this.listBox_audios, e.Location);
                }
            }
        }
    }
}
