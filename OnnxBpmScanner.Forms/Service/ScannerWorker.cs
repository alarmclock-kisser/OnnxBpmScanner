using OnnxBpmScanner.Core;
using OnnxBpmScanner.Runtime;
using System.Collections.Concurrent;

namespace OnnxBpmScanner.Forms.Service
{
    public sealed class Settings
    {
        public bool EnableAtStartup { get; set; }
        public string[] DirectoriesToWatch { get; set; } = [];
        public string[] DirectoriesToExclude { get; set; } = [];
        public string[] ExtensionsToWatch { get; set; } = [".mp3", ".wav", ".flac", ".ogg", ".m4a"];
        public int MaxDurationSeconds { get; set; } = 720;
        public int MaxThreads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
        public float Round { get; set; } = 0.25f;
        public int DirectMlDeviceId { get; set; }
        public bool WriteBpmTags { get; set; } = true;
        public string ModelPath { get; set; } = "/ressource";

        public void Normalize()
        {
            this.DirectoriesToWatch = this.DirectoriesToWatch
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            this.DirectoriesToExclude = this.DirectoriesToExclude
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            this.ExtensionsToWatch = this.ExtensionsToWatch
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.StartsWith('.') ? value : $".{value}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (this.ExtensionsToWatch.Length == 0)
            {
                this.ExtensionsToWatch = [".mp3", ".wav", ".flac", ".ogg", ".m4a"];
            }

            this.MaxDurationSeconds = this.MaxDurationSeconds <= 0 ? 720 : this.MaxDurationSeconds;
            this.MaxThreads = this.MaxThreads <= 0 ? 1 : this.MaxThreads;
            this.Round = this.Round < 0 ? 0 : this.Round;
            this.ModelPath = string.IsNullOrWhiteSpace(this.ModelPath) ? "/ressource" : this.ModelPath;
        }
    }

    public sealed class ScannerWorker : IDisposable
    {
        private readonly Settings _settings;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<(string FilePath, bool Force)> _fileQueue = new();
        private readonly HashSet<string> _queuedOrProcessingFiles = [];
        private readonly object _stateLock = new();
        private readonly object _workerLock = new();
        private readonly List<Task> _workerTasks = [];
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly OnnxService _onnx;

        private int _currentWorkerCount;
        private double _currentAnalysisProgress;
        private int? _initializedDeviceId;

        public record LastScannedTrackInfo(string FileName, double Bpm);

        public int QueueCount => this._fileQueue.Count;
        public int ProcessedCount { get; private set; }
        public LastScannedTrackInfo? LastScannedTrack { get; private set; }
        public IReadOnlyList<string> DirectMlDevices => this._onnx.DirectMlDevices;
        public bool IsModelLoaded => this._onnx.IsModelLoaded;
        public int? InitializedDeviceId => this._initializedDeviceId;

        public double CurrentAnalysisProgress
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._currentAnalysisProgress;
                }
            }
        }

        public event Action? StateChanged;

        public ScannerWorker(Settings settings)
        {
            this._settings = settings;
            this._settings.Normalize();
            this._onnx = new OnnxService();
            this.InitializeModel();
        }

        public bool InitializeModel()
        {
            this._initializedDeviceId = null;
            this._onnx.DisposeSession();

            bool loaded = this._onnx.LoadModel(this._settings.ModelPath, this._settings.DirectMlDeviceId);
            if (!loaded && this._settings.DirectMlDeviceId != 0)
            {
                loaded = this._onnx.LoadModel(this._settings.ModelPath, 0);
                if (loaded)
                {
                    this._settings.DirectMlDeviceId = 0;
                }
            }

            if (loaded)
            {
                this._initializedDeviceId = this._settings.DirectMlDeviceId;
            }

            return loaded;
        }

        public void Start()
        {
            this.ApplyThreads();
            this.ApplyWatchers();

            foreach (string dirRaw in this._settings.DirectoriesToWatch)
            {
                string dir = Environment.ExpandEnvironmentVariables(dirRaw);
                if (Directory.Exists(dir))
                {
                    this.ScanDirectory(dir, force: false);
                }
            }
        }

        public void ApplyThreads()
        {
            this._settings.Normalize();

            lock (this._workerLock)
            {
                int target = this._settings.MaxThreads <= 0 ? 1 : this._settings.MaxThreads;
                while (this._currentWorkerCount < target)
                {
                    this._currentWorkerCount++;
                    this._workerTasks.Add(Task.Run(this.ProcessQueueAsync));
                }
            }
        }

        public void ApplyWatchers()
        {
            this._settings.Normalize();

            foreach (FileSystemWatcher watcher in this._watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            this._watchers.Clear();

            foreach (string dirRaw in this._settings.DirectoriesToWatch)
            {
                string dir = Environment.ExpandEnvironmentVariables(dirRaw);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true,
                    Filter = "*.*"
                };

                watcher.Created += this.OnFileDetected;
                watcher.Renamed += this.OnFileDetected;
                watcher.Changed += this.OnFileDetected;
                watcher.EnableRaisingEvents = true;
                this._watchers.Add(watcher);
            }
        }

        public void RescanAll()
        {
            this.ProcessedCount = 0;
            this.LastScannedTrack = null;
            this.SetAnalysisProgress(0.0);
            this.StateChanged?.Invoke();

            foreach (string dirRaw in this._settings.DirectoriesToWatch)
            {
                string dir = Environment.ExpandEnvironmentVariables(dirRaw);
                if (Directory.Exists(dir))
                {
                    this.ScanDirectory(dir, force: true);
                }
            }
        }

        private void ScanDirectory(string dir, bool force)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                foreach (string file in files)
                {
                    if (!this.ShouldHandleFile(file))
                    {
                        continue;
                    }

                    this.EnqueueFile(file, force);
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error scanning directory {dir}: {ex.Message}");
            }
        }

        private bool ShouldHandleFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            if (this.IsPathExcluded(filePath))
            {
                return false;
            }

            string extension = Path.GetExtension(filePath);
            return this._settings.ExtensionsToWatch.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private bool IsPathExcluded(string targetPath)
        {
            if (this._settings.DirectoriesToExclude.Length == 0)
            {
                return false;
            }

            string targetNormal = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string excludedPath in this._settings.DirectoriesToExclude)
            {
                string excludedNormal = Path.GetFullPath(Environment.ExpandEnvironmentVariables(excludedPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!targetNormal.StartsWith(excludedNormal, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (targetNormal.Length == excludedNormal.Length)
                {
                    return true;
                }

                char nextChar = targetNormal[excludedNormal.Length];
                if (nextChar == Path.DirectorySeparatorChar || nextChar == Path.AltDirectorySeparatorChar)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnFileDetected(object sender, FileSystemEventArgs e)
        {
            if (!this.ShouldHandleFile(e.FullPath))
            {
                return;
            }

            this.EnqueueFile(e.FullPath, force: false);
        }

        private void EnqueueFile(string filePath, bool force)
        {
            lock (this._stateLock)
            {
                if (this._queuedOrProcessingFiles.Add(filePath))
                {
                    this._fileQueue.Enqueue((filePath, force));
                    this.StateChanged?.Invoke();
                }
            }
        }

        private async Task ProcessQueueAsync()
        {
            while (!this._cts.Token.IsCancellationRequested)
            {
                lock (this._workerLock)
                {
                    int target = this._settings.MaxThreads <= 0 ? 1 : this._settings.MaxThreads;
                    if (this._currentWorkerCount > target)
                    {
                        this._currentWorkerCount--;
                        return;
                    }
                }

                if (!this._fileQueue.TryDequeue(out (string FilePath, bool Force) queueItem))
                {
                    try
                    {
                        await Task.Delay(1000, this._cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    if (!this.WaitForFileReady(queueItem.FilePath, TimeSpan.FromSeconds(30)))
                    {
                        continue;
                    }

                    bool scanned = await this.ProcessAudioFileAsync(queueItem.FilePath, queueItem.Force);
                    if (scanned)
                    {
                        this.ProcessedCount++;
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Error processing file {queueItem.FilePath}: {ex.Message}");
                }
                finally
                {
                    lock (this._stateLock)
                    {
                        this._queuedOrProcessingFiles.Remove(queueItem.FilePath);
                    }

                    this.SetAnalysisProgress(0.0);
                    this.StateChanged?.Invoke();
                }
            }
        }

        private async Task<bool> ProcessAudioFileAsync(string filePath, bool force)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                if (!force && tagFile.Tag.BeatsPerMinute > 0)
                {
                    return false;
                }

                if (tagFile.Properties.Duration.TotalSeconds > this._settings.MaxDurationSeconds)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Skipping {filePath}: {ex.Message}");
                return false;
            }

            if (!this._onnx.IsModelLoaded)
            {
                bool loaded = this.InitializeModel();
                if (!loaded)
                {
                    StaticLogger.Log("ONNX model could not be loaded for service scan.");
                    return false;
                }
            }

            var progress = new Progress<double>(value =>
            {
                this.SetAnalysisProgress(value);
                this.StateChanged?.Invoke();
            });

            double? bpm = await this._onnx.RunInferenceBpmEstimateAsync(filePath, this._settings.Round, this._settings.MaxDurationSeconds, progress);
            if (!bpm.HasValue || bpm.Value <= 0)
            {
                return false;
            }

            if (this._settings.WriteBpmTags)
            {
                try
                {
                    if (!AudioObj.WriteBpmTagToFile(filePath, bpm.Value))
                    {
                        StaticLogger.Log($"Failed to write BPM tag for {filePath}");
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Error writing BPM tag for {filePath}: {ex.Message}");
                }
            }

            this.LastScannedTrack = new LastScannedTrackInfo(Path.GetFileNameWithoutExtension(filePath), bpm.Value);
            return true;
        }

        private void SetAnalysisProgress(double value)
        {
            lock (this._stateLock)
            {
                this._currentAnalysisProgress = Math.Clamp(value, 0.0, 1.0);
            }
        }

        private bool WaitForFileReady(string filename, TimeSpan timeout)
        {
            DateTime end = DateTime.Now.Add(timeout);
            while (DateTime.Now < end && !this._cts.Token.IsCancellationRequested)
            {
                try
                {
                    using FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.None);
                    return stream.Length > 0;
                }
                catch (IOException)
                {
                    Thread.Sleep(500);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(500);
                }
            }

            return false;
        }

        public void Stop()
        {
            this._cts.Cancel();
            foreach (FileSystemWatcher watcher in this._watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            this._watchers.Clear();

            try
            {
                Task.WaitAll(this._workerTasks.ToArray(), 2000);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            this.Stop();
            this._onnx.Dispose();
            this._cts.Dispose();
        }
    }
}
