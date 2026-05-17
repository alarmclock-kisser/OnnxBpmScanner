using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxBpmScanner.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace OnnxBpmScanner.Runtime
{
    public partial class OnnxService
    {
        private readonly SemaphoreSlim _gpuInferenceLock = new(1, 1);

        private sealed record AnalysisWindowCandidate(int StartSample, int LengthSamples, double Energy);
        private sealed record PreparedMelWindow(float[] MelFlat, int StartSample, int LengthSamples, double Energy);
        private sealed record PreparedTrackForInference(string FilePath, string Name, int SampleRate, int Hop, IReadOnlyList<PreparedMelWindow> Windows, double IntroSkipSeconds, double DurationSeconds);
        private sealed record WindowInferenceResult(double Bpm, double Confidence, double BestPeak, double SecondaryPeak, double StartSeconds, double DurationSeconds);
        private sealed record TrackInferenceSummary(double RawMedianBpm, double FinalBpm, double Spread, double Confidence, IReadOnlyList<WindowInferenceResult> Windows);

        public string InputDirectory { get; set; } = Directory.GetCurrentDirectory();
        public List<string> AudioFiles { get; set; } = [];

        public AudioHandling AudioHandler { get; set; } = new AudioHandling();



        public string[] GetAudioFiles(string? customDirectory = null, string[]? extensions = null)
        {
            extensions ??= new[] { ".wav", ".mp3", ".flac" };

            string audioPath = this.InputDirectory;
            if (customDirectory != null)
            {
                audioPath = customDirectory;

                if (customDirectory.StartsWith("/repo", StringComparison.OrdinalIgnoreCase))
                {
                    audioPath = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                }
                if (customDirectory.StartsWith("/mymusic", StringComparison.OrdinalIgnoreCase))
                {
                    audioPath = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                }
            }

            // Get wav, mp3, flac files
            var audioFiles = Directory.GetFiles(audioPath, "*.*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray();

            this.AudioFiles.AddRange(audioFiles);

            return this.AudioFiles.ToArray();
        }



        public async Task<Dictionary<string, double?>> EstimateBpmForAllAudioFilesAsync(IProgress<double>? progress = null, int maxFiles = 0, int maxAudioDurationMinutes = 0, float round = 0f, int maxAnalysisDurationSeconds = 60, int maxCpuParallelism = 4)
        {
            var results = new ConcurrentDictionary<string, double?>();
            List<string> filesToProcess = [];
            foreach (var file in this.AudioFiles)
            {
                if (maxFiles > 0 && filesToProcess.Count >= maxFiles)
                {
                    StaticLogger.Log("Max files limit reached.");
                    break;
                }

                if (maxAudioDurationMinutes > 0)
                {
                    double minutes = AudioHandling.GetAudioDuration(file)?.TotalMinutes ?? 0;
                    if (minutes > maxAudioDurationMinutes)
                    {
                        StaticLogger.Log($"Skipping {file} due to duration {minutes:F2} min exceeding limit.");
                        continue;
                    }
                }

                filesToProcess.Add(file);
            }

            int totalFiles = filesToProcess.Count;
            if (totalFiles == 0)
            {
                return new Dictionary<string, double?>();
            }

            int processedFiles = 0;
            int cpuParallelism = Math.Max(1, Math.Min(Math.Min(Environment.ProcessorCount, maxCpuParallelism), 4));
            using SemaphoreSlim cpuSemaphore = new(cpuParallelism, cpuParallelism);
            List<Task<(string FilePath, PreparedTrackForInference? PreparedTrack, Stopwatch Stopwatch)>> preprocessingTasks = [];

            foreach (string file in filesToProcess)
            {
                await cpuSemaphore.WaitAsync().ConfigureAwait(false);
                preprocessingTasks.Add(Task.Run(async () =>
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        PreparedTrackForInference? preparedTrack = await this.PrepareTrackForInferenceAsync(file, maxAnalysisDurationSeconds).ConfigureAwait(false);
                        return (file, preparedTrack, sw);
                    }
                    finally
                    {
                        cpuSemaphore.Release();
                    }
                }));
            }

            while (preprocessingTasks.Count > 0)
            {
                Task<(string FilePath, PreparedTrackForInference? PreparedTrack, Stopwatch Stopwatch)> completedTask = await Task.WhenAny(preprocessingTasks).ConfigureAwait(false);
                preprocessingTasks.Remove(completedTask);

                var (filePath, preparedTrack, stopwatch) = await completedTask.ConfigureAwait(false);
                double? bpm = null;
                try
                {
                    if (preparedTrack != null)
                    {
                        TrackInferenceSummary? summary = await this.RunPreparedTrackInferenceAsync(preparedTrack, round).ConfigureAwait(false);
                        bpm = summary?.FinalBpm;
                    }
                }
                catch (Exception ex)
                {
                    await StaticLogger.LogAsync(ex).ConfigureAwait(false);
                }

                results[filePath] = bpm;
                processedFiles++;
                stopwatch.Stop();
                progress?.Report(processedFiles / (double) totalFiles);
                Console.WriteLine($"Audio file {processedFiles}/{totalFiles} processed within {stopwatch.Elapsed.TotalSeconds:F3} sec.");
            }

            return new Dictionary<string, double?>(results);
        }


        /// <summary>
        /// If model is loaded, runs inference to estimate BPM for the given audio file. Returns estimated BPM or null if error occurs.
        /// Progress can be reported via the IProgress<double> parameter, which should report values from 0.0 to 1.0 indicating inference progress, if given.
        /// </summary>
        /// <param name="audioFilePath">Required: File path to audio file (wav, mp3, flac)</param>
        /// <param name="progress">Optional: Double Progress to report from 0.0 to 1.0 (finished)</param>
        /// <returns></returns>
        public async Task<double?> RunInferenceBpmEstimateAsync(string audioFilePath, float round, int maxAnalysisDurationSeconds = 60, IProgress<double>? progress = null)
        {
            if (!this.IsModelLoaded || this._session == null)
            {
                StaticLogger.Log("No model loaded.");
                return null;
            }

            try
            {
                var preparedTrack = await this.PrepareTrackForInferenceAsync(audioFilePath, maxAnalysisDurationSeconds, progress).ConfigureAwait(false);
                if (preparedTrack == null)
                {
                    return null;
                }

                var summary = await this.RunPreparedTrackInferenceAsync(preparedTrack, round, progress).ConfigureAwait(false);
                if (summary == null || summary.FinalBpm <= 0)
                {
                    await StaticLogger.LogAsync($"{preparedTrack.Name}: BPM inference did not produce a valid result after preprocessing.").ConfigureAwait(false);
                    return null;
                }

                await StaticLogger.LogAsync($"{preparedTrack.Name}: {summary.FinalBpm:F3} BPM detected").ConfigureAwait(false);
                progress?.Report(1.0);
                return Math.Round(summary.FinalBpm, 3);
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex).ConfigureAwait(false);
                return null;
            }
        }

        private async Task<PreparedTrackForInference?> PrepareTrackForInferenceAsync(string audioFilePath, int maxAnalysisDurationSeconds, IProgress<double>? progress = null)
        {
            const int nFft = 1024;
            const int hop = 512;
            const int nMels = 128;
            const int targetSR = 22050;
            const int maxWindows = 3;

            AudioObj? audioObj = null;
            try
            {
                audioObj = await Task.Run(() => new AudioObj(audioFilePath)).ConfigureAwait(false);
                if (audioObj.Data.Length == 0 || audioObj.SampleRate <= 0)
                {
                    return null;
                }

                await StaticLogger.LogAsync($"Processing {audioObj.Name}...").ConfigureAwait(false);
                progress?.Report(0.01);

                if (audioObj.Channels != 1)
                {
                    await audioObj.RechannelAsync(1).ConfigureAwait(false);
                }

                if (audioObj.SampleRate != targetSR)
                {
                    await audioObj.ResampleAsync(targetSR).ConfigureAwait(false);
                }

                await audioObj.TrimLeadingAndTrailingSilenceAsync().ConfigureAwait(false);

                float[] mono = PrepareMonoForInference(audioObj.Data);
                if (mono.Length < nFft)
                {
                    await StaticLogger.LogAsync($"{audioObj.Name}: audio too short after preprocessing for BPM inference.").ConfigureAwait(false);
                    return null;
                }

                progress?.Report(0.10);

                int introSkipSamples = FindIntroSkipSamples(mono, targetSR);
                double introSkipSeconds = introSkipSamples / (double) targetSR;

                List<AnalysisWindowCandidate> windows = SelectAnalysisWindows(mono, targetSR, maxAnalysisDurationSeconds, introSkipSamples, maxWindows);
                if (windows.Count == 0)
                {
                    return null;
                }

                List<PreparedMelWindow> preparedWindows = new(windows.Count);
                for (int i = 0; i < windows.Count; i++)
                {
                    AnalysisWindowCandidate candidate = windows[i];
                    float[] monoWindow = new float[candidate.LengthSamples];
                    Array.Copy(mono, candidate.StartSample, monoWindow, 0, candidate.LengthSamples);
                    float[] melFlat = await Task.Run(() => ExtractMelFeaturesFromMono(monoWindow, targetSR, nFft, hop, nMels)).ConfigureAwait(false);
                    preparedWindows.Add(new PreparedMelWindow(melFlat, candidate.StartSample, candidate.LengthSamples, candidate.Energy));

                    progress?.Report(0.10 + (0.30 * (i + 1) / windows.Count));
                }

                return new PreparedTrackForInference(
                    audioFilePath,
                    audioObj.Name,
                    targetSR,
                    hop,
                    preparedWindows,
                    introSkipSeconds,
                    mono.Length / (double) targetSR);
            }
            finally
            {
                audioObj?.Dispose();
            }
        }

        private async Task<TrackInferenceSummary?> RunPreparedTrackInferenceAsync(PreparedTrackForInference preparedTrack, float round, IProgress<double>? progress = null)
        {
            const int nMels = 128;

            List<WindowInferenceResult> windowResults = new(preparedTrack.Windows.Count);
            for (int i = 0; i < preparedTrack.Windows.Count; i++)
            {
                PreparedMelWindow window = preparedTrack.Windows[i];
                List<float> activationCurve = await this.RunOnnxActivationInferenceAsync(window.MelFlat, nMels).ConfigureAwait(false);
                WindowInferenceResult? result = await Task.Run(() => EstimateBpmFromActivationCurve(activationCurve, preparedTrack.SampleRate, preparedTrack.Hop, window.StartSample, window.LengthSamples)).ConfigureAwait(false);
                if (result != null && result.Bpm > 0)
                {
                    windowResults.Add(result);
                }

                progress?.Report(0.40 + (0.30 * (i + 1) / preparedTrack.Windows.Count));
            }

            if (windowResults.Count == 0)
            {
                return null;
            }

            double rawMedianBpm = ComputeMedian(windowResults.Select(w => w.Bpm));
            double spread = ComputeMedianAbsoluteDeviation(windowResults.Select(w => w.Bpm), rawMedianBpm);
            double meanConfidence = windowResults.Average(w => w.Confidence);
            double finalBpm = SnapBpmToStep(rawMedianBpm, round);

            string windowStarts = string.Join(",", windowResults.Select(w => $"{w.StartSeconds:F1}s"));
            await StaticLogger.LogAsync($"{preparedTrack.Name}: introSkip={preparedTrack.IntroSkipSeconds:F2}s windows={windowResults.Count} starts=[{windowStarts}] median={rawMedianBpm:F3} spread={spread:F3} conf={meanConfidence:F2} final={finalBpm:F3}").ConfigureAwait(false);

            progress?.Report(0.90);
            return new TrackInferenceSummary(rawMedianBpm, finalBpm, spread, meanConfidence, windowResults);
        }

        private async Task<List<float>> RunOnnxActivationInferenceAsync(float[] melFlat, int nMels)
        {
            const int chunkSize = 1024;
            const int chunkHop = 1024;

            if (this._session == null)
            {
                return [];
            }

            int totalFramesInMel = melFlat.Length / nMels;
            List<float> activationCurve = new();

            await this._gpuInferenceLock.WaitAsync().ConfigureAwait(false);
            try
            {
                for (int start = 0; start < totalFramesInMel; start += chunkHop)
                {
                    int currentSize = Math.Min(chunkSize, totalFramesInMel - start);
                    float[] chunk = new float[currentSize * nMels];
                    Array.Copy(melFlat, start * nMels, chunk, 0, currentSize * nMels);

                    var tensor = new DenseTensor<float>(chunk, [1, currentSize, nMels]);
                    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(this._session.InputMetadata.Keys.First(), tensor) };

                    using var results = this._session.Run(inputs);
                    activationCurve.AddRange(results.First().AsEnumerable<float>());

                    if (totalFramesInMel - start <= chunkSize)
                    {
                        break;
                    }
                }
            }
            finally
            {
                this._gpuInferenceLock.Release();
            }

            return activationCurve;
        }


        private static float[,] BuildMelFilterBank(int nMels, int fftBins, int sampleRate, int nFft)
        {
            float HzToMel(float hz) => 2595f * (float) Math.Log10(1 + hz / 700f);
            float MelToHz(float mel) => 700f * ((float) Math.Pow(10, mel / 2595f) - 1);

            float fMin = 0;
            float fMax = sampleRate / 2f;

            float melMin = HzToMel(fMin);
            float melMax = HzToMel(fMax);

            float[] melPoints = new float[nMels + 2];
            for (int i = 0; i < melPoints.Length; i++)
            {
                melPoints[i] = melMin + (melMax - melMin) * i / (nMels + 1);
            }

            float[] hz = melPoints.Select(MelToHz).ToArray();

            int[] bins = hz
                .Select(f => (int) Math.Floor((nFft + 1) * f / sampleRate))
                .ToArray();

            float[,] filter = new float[nMels, fftBins];

            for (int m = 1; m <= nMels; m++)
            {
                for (int k = bins[m - 1]; k < bins[m]; k++)
                {
                    if (k >= 0 && k < fftBins)
                    {
                        filter[m - 1, k] =
                            (float) (k - bins[m - 1]) /
                            (bins[m] - bins[m - 1]);
                    }
                }

                for (int k = bins[m]; k < bins[m + 1]; k++)
                {
                    if (k >= 0 && k < fftBins)
                    {
                        filter[m - 1, k] =
                            (float) (bins[m + 1] - k) /
                            (bins[m + 1] - bins[m]);
                    }
                }
            }

            return filter;
        }

        private static float[] PrepareMonoForInference(float[] mono)
        {
            if (mono.Length == 0)
            {
                return Array.Empty<float>();
            }

            float[] prepared = new float[mono.Length];
            Array.Copy(mono, prepared, mono.Length);

            double mean = 0;
            for (int i = 0; i < prepared.Length; i++)
            {
                mean += prepared[i];
            }

            mean /= prepared.Length;

            float peak = 0f;
            for (int i = 0; i < prepared.Length; i++)
            {
                float centered = (float) (prepared[i] - mean);
                prepared[i] = centered;

                float abs = Math.Abs(centered);
                if (abs > peak)
                {
                    peak = abs;
                }
            }

            if (peak > 1e-4f)
            {
                float gain = Math.Clamp(0.98f / peak, 0.25f, 4f);
                for (int i = 0; i < prepared.Length; i++)
                {
                    prepared[i] *= gain;
                }
            }

            return prepared;
        }

        private static double[] PrepareActivationCurve(List<float> activationCurve)
        {
            if (activationCurve.Count == 0)
            {
                return Array.Empty<double>();
            }

            double[] smoothed = new double[activationCurve.Count];
            for (int i = 0; i < activationCurve.Count; i++)
            {
                double weightedSum = activationCurve[i] * 2.0;
                double weight = 2.0;

                if (i > 0)
                {
                    weightedSum += activationCurve[i - 1];
                    weight += 1.0;
                }

                if (i < activationCurve.Count - 1)
                {
                    weightedSum += activationCurve[i + 1];
                    weight += 1.0;
                }

                smoothed[i] = weightedSum / weight;
            }

            double mean = 0;
            for (int i = 0; i < smoothed.Length; i++)
            {
                mean += smoothed[i];
            }

            mean /= smoothed.Length;

            double variance = 0;
            for (int i = 0; i < smoothed.Length; i++)
            {
                double centered = smoothed[i] - mean;
                smoothed[i] = centered;
                variance += centered * centered;
            }

            double stdDev = Math.Sqrt(variance / smoothed.Length);
            if (stdDev > 1e-9)
            {
                for (int i = 0; i < smoothed.Length; i++)
                {
                    smoothed[i] /= stdDev;
                }
            }

            return smoothed;
        }

        private static List<AnalysisWindowCandidate> SelectAnalysisWindows(float[] mono, int sampleRate, int maxAnalysisDurationSeconds, int introSkipSamples, int maxWindows)
        {
            if (mono.Length == 0)
            {
                return [];
            }

            int safeIntroSkip = Math.Clamp(introSkipSamples, 0, Math.Max(0, mono.Length - 1));
            int availableSamples = mono.Length - safeIntroSkip;
            if (availableSamples <= 0)
            {
                return [new AnalysisWindowCandidate(0, mono.Length, 0d)];
            }

            int preferredWindowSamples = Math.Min(availableSamples, Math.Max(sampleRate * 30, maxAnalysisDurationSeconds * sampleRate));
            preferredWindowSamples = Math.Min(preferredWindowSamples, sampleRate * Math.Max(30, maxAnalysisDurationSeconds));
            preferredWindowSamples = Math.Max(Math.Min(preferredWindowSamples, availableSamples), Math.Min(sampleRate * 8, availableSamples));

            double[] prefixSquares = BuildPrefixSquares(mono);
            if (availableSamples <= preferredWindowSamples)
            {
                double fullEnergy = ComputeWindowEnergy(prefixSquares, safeIntroSkip, availableSamples);
                return [new AnalysisWindowCandidate(safeIntroSkip, availableSamples, fullEnergy)];
            }

            int hopSamples = Math.Max(sampleRate * 10, preferredWindowSamples / 3);
            List<AnalysisWindowCandidate> candidates = [];
            for (int start = safeIntroSkip; start + preferredWindowSamples <= mono.Length; start += hopSamples)
            {
                double energy = ComputeWindowEnergy(prefixSquares, start, preferredWindowSamples);
                candidates.Add(new AnalysisWindowCandidate(start, preferredWindowSamples, energy));
            }

            int lastStart = mono.Length - preferredWindowSamples;
            if (candidates.Count == 0 || candidates[^1].StartSample != lastStart)
            {
                double lastEnergy = ComputeWindowEnergy(prefixSquares, lastStart, preferredWindowSamples);
                candidates.Add(new AnalysisWindowCandidate(lastStart, preferredWindowSamples, lastEnergy));
            }

            int minimumSpacing = Math.Max(sampleRate * 15, preferredWindowSamples / 2);
            List<AnalysisWindowCandidate> selected = [];
            foreach (AnalysisWindowCandidate candidate in candidates.OrderByDescending(c => c.Energy))
            {
                bool overlaps = selected.Any(existing => Math.Abs(existing.StartSample - candidate.StartSample) < minimumSpacing);
                if (overlaps)
                {
                    continue;
                }

                selected.Add(candidate);
                if (selected.Count >= maxWindows)
                {
                    break;
                }
            }

            if (selected.Count == 0)
            {
                selected.Add(candidates.OrderByDescending(c => c.Energy).First());
            }

            return selected.OrderBy(c => c.StartSample).ToList();
        }

        private static int FindIntroSkipSamples(float[] mono, int sampleRate)
        {
            if (mono.Length < sampleRate * 12)
            {
                return 0;
            }

            int windowSamples = Math.Min(mono.Length, Math.Max(sampleRate, sampleRate * 2));
            int hopSamples = Math.Max(1, sampleRate / 2);
            double[] prefixSquares = BuildPrefixSquares(mono);
            List<double> rmsFrames = [];

            for (int start = 0; start + windowSamples <= mono.Length; start += hopSamples)
            {
                rmsFrames.Add(ComputeWindowEnergy(prefixSquares, start, windowSamples));
            }

            if (rmsFrames.Count < 3)
            {
                return 0;
            }

            double peak = rmsFrames.Max();
            double[] sorted = rmsFrames.OrderBy(v => v).ToArray();
            double noiseFloor = sorted[Math.Clamp((int) Math.Floor((sorted.Length - 1) * 0.2), 0, sorted.Length - 1)];
            double threshold = Math.Max(noiseFloor * 2.5, peak * 0.15);

            for (int frame = 0; frame < rmsFrames.Count - 1; frame++)
            {
                if (rmsFrames[frame] >= threshold && rmsFrames[frame + 1] >= threshold)
                {
                    int skipSamples = frame * hopSamples;
                    return skipSamples >= sampleRate * 2 ? skipSamples : 0;
                }
            }

            return 0;
        }

        private static float[] ExtractMelFeaturesFromMono(float[] mono, int sampleRate, int nFft, int hop, int nMels)
        {
            float[] padded = new float[mono.Length + nFft];
            Array.Copy(mono, 0, padded, nFft / 2, mono.Length);

            int fftBins = nFft / 2 + 1;
            int totalFrames = 1 + (padded.Length - nFft) / hop;
            float[,] melFilter = BuildMelFilterBank(nMels, fftBins, sampleRate, nFft);
            float[] output = new float[totalFrames * nMels];

            float[] window = new float[nFft];
            for (int i = 0; i < nFft; i++)
            {
                window[i] = 0.5f - 0.5f * (float) Math.Cos(2 * Math.PI * i / nFft);
            }

            System.Numerics.Complex[] fft = new System.Numerics.Complex[nFft];
            for (int frame = 0; frame < totalFrames; frame++)
            {
                int offset = frame * hop;
                for (int i = 0; i < nFft; i++)
                {
                    fft[i] = new System.Numerics.Complex(padded[offset + i] * window[i], 0);
                }

                MathNet.Numerics.IntegralTransforms.Fourier.Forward(fft, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);

                for (int m = 0; m < nMels; m++)
                {
                    double sum = 0;
                    for (int b = 0; b < fftBins; b++)
                    {
                        double magSq = fft[b].Real * fft[b].Real + fft[b].Imaginary * fft[b].Imaginary;
                        sum += magSq * melFilter[m, b];
                    }

                    output[frame * nMels + m] = (float) Math.Log10(sum + 1e-10);
                }
            }

            return output;
        }

        private static WindowInferenceResult? EstimateBpmFromActivationCurve(List<float> activationCurve, int sampleRate, int hop, int startSample, int lengthSamples)
        {
            double[] preparedActivationCurve = PrepareActivationCurve(activationCurve);
            if (preparedActivationCurve.Length == 0)
            {
                return null;
            }

            double secondsPerFrame = (double) hop / sampleRate;
            int minLag = (int) (60.0 / (220.0 * secondsPerFrame));
            int maxLag = Math.Min((int) (60.0 / (50.0 * secondsPerFrame)), preparedActivationCurve.Length - 2);
            if (maxLag <= minLag)
            {
                return null;
            }

            double[] acf = new double[maxLag + 2];
            double maxVal = double.MinValue;
            int bestLag = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                int limit = preparedActivationCurve.Length - lag;
                if (limit <= 0)
                {
                    continue;
                }

                double sum = 0;
                for (int i = 0; i < limit; i++)
                {
                    sum += preparedActivationCurve[i] * preparedActivationCurve[i + lag];
                }

                acf[lag] = sum / limit;
                if (acf[lag] > maxVal)
                {
                    maxVal = acf[lag];
                    bestLag = lag;
                }
            }

            if (bestLag <= 0)
            {
                return null;
            }

            List<(int Lag, double Value)> peaks = [];
            for (int lag = minLag + 1; lag < maxLag; lag++)
            {
                if (acf[lag] >= acf[lag - 1] && acf[lag] >= acf[lag + 1])
                {
                    peaks.Add((lag, acf[lag]));
                }
            }

            if (peaks.Count == 0)
            {
                peaks.Add((bestLag, maxVal));
            }

            (int Lag, double Value) bestPeak = peaks.OrderByDescending(p => p.Value).First();
            (int Lag, double Value) secondaryPeak = peaks
                .Where(p => Math.Abs(p.Lag - bestPeak.Lag) > Math.Max(2, bestPeak.Lag / 10))
                .OrderByDescending(p => p.Value)
                .FirstOrDefault();

            if (secondaryPeak == default)
            {
                secondaryPeak = (bestPeak.Lag, bestPeak.Value * 0.5);
            }

            double p = 0;
            if (bestPeak.Lag > minLag && bestPeak.Lag < maxLag)
            {
                double alpha = acf[bestPeak.Lag - 1];
                double beta = acf[bestPeak.Lag];
                double gamma = acf[bestPeak.Lag + 1];
                double denominator = alpha - 2 * beta + gamma;
                if (Math.Abs(denominator) > 1e-12)
                {
                    p = 0.5 * (alpha - gamma) / denominator;
                }
            }

            double refinedLag = bestPeak.Lag + p;
            if (refinedLag <= 0)
            {
                return null;
            }

            double bpm = 60.0 / (refinedLag * secondsPerFrame);
            while (bpm < 85)
            {
                bpm *= 2;
            }

            while (bpm > 175)
            {
                bpm /= 2;
            }

            double sharpness = 0;
            if (bestPeak.Lag > minLag && bestPeak.Lag < maxLag)
            {
                sharpness = bestPeak.Value - ((acf[bestPeak.Lag - 1] + acf[bestPeak.Lag + 1]) * 0.5);
            }

            double peakGap = Math.Max(0, bestPeak.Value - secondaryPeak.Value);
            double gapScore = Math.Clamp(peakGap / (Math.Abs(bestPeak.Value) + 1e-9), 0, 1);
            double sharpnessScore = Math.Clamp(sharpness / (Math.Abs(bestPeak.Value) + 1e-9), 0, 1);
            double confidence = Math.Clamp((gapScore * 0.7) + (sharpnessScore * 0.3), 0, 1);

            return new WindowInferenceResult(
                bpm,
                confidence,
                bestPeak.Value,
                secondaryPeak.Value,
                startSample / (double) sampleRate,
                lengthSamples / (double) sampleRate);
        }

        private static double SnapBpmToStep(double bpm, float roundStep)
        {
            if (roundStep <= 0)
            {
                return bpm;
            }

            decimal step = decimal.Parse(roundStep.ToString("G9", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            decimal snapped = Math.Round((decimal) bpm / step, 0, MidpointRounding.AwayFromZero) * step;
            return (double) snapped;
        }

        private static double ComputeMedian(IEnumerable<double> values)
        {
            double[] ordered = values.OrderBy(v => v).ToArray();
            if (ordered.Length == 0)
            {
                return 0;
            }

            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2.0
                : ordered[middle];
        }

        private static double ComputeMedianAbsoluteDeviation(IEnumerable<double> values, double median)
        {
            double[] deviations = values.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
            if (deviations.Length == 0)
            {
                return 0;
            }

            int middle = deviations.Length / 2;
            return deviations.Length % 2 == 0
                ? (deviations[middle - 1] + deviations[middle]) / 2.0
                : deviations[middle];
        }

        private static double[] BuildPrefixSquares(float[] samples)
        {
            double[] prefixSquares = new double[samples.Length + 1];
            for (int i = 0; i < samples.Length; i++)
            {
                prefixSquares[i + 1] = prefixSquares[i] + (samples[i] * samples[i]);
            }

            return prefixSquares;
        }

        private static double ComputeWindowEnergy(double[] prefixSquares, int startSample, int lengthSamples)
        {
            if (lengthSamples <= 0)
            {
                return 0;
            }

            double sumSquares = prefixSquares[startSample + lengthSamples] - prefixSquares[startSample];
            return Math.Sqrt(sumSquares / lengthSamples);
        }




    }
}
