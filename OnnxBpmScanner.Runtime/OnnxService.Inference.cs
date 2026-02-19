using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxBpmScanner.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace OnnxBpmScanner.Runtime
{
    public partial class OnnxService
    {
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



        public async Task<Dictionary<string, double?>> EstimateBpmForAllAudioFilesAsync(IProgress<double>? progress = null, int maxFiles = 0, int maxAudioDurationMinutes = 0)
        {
            var results = new ConcurrentDictionary<string, double?>();
            int totalFiles = this.AudioFiles.Count;
            int processedFiles = 0;
            foreach (var file in this.AudioFiles)
            {
                if (maxFiles > 0 && processedFiles >= maxFiles)
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

                Stopwatch sw = Stopwatch.StartNew();

                double? bpm = await this.RunInferenceBpmEstimateAsync(file, progress);
                results[file] = bpm;
                processedFiles++;

                sw.Stop();
                Console.WriteLine($"Audio file {processedFiles}/{totalFiles} processed within {sw.Elapsed.TotalSeconds:F3} sec.");
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
        public async Task<double?> RunInferenceBpmEstimateAsync(string audioFilePath, IProgress<double>? progress = null)
        {
            if (!this.IsModelLoaded || this._session == null)
            {
                StaticLogger.Log("No model loaded.");
                return null;
            }

            AudioObj? audioObj = null;

            try
            {
                // 1. Audio-Import (schon asynchron in AudioHandling.cs)
                audioObj = await this.AudioHandler.ImportAudioAsync(audioFilePath).ConfigureAwait(false);
                if (audioObj == null) return null;

                await StaticLogger.LogAsync($"Processing {audioObj.Name}...").ConfigureAwait(false);
                progress?.Report(0.01);

                // Vorbereitung der Parameter
                const int nFft = 1024;
                const int hop = 512;
                const int nMels = 128;
                const int targetSR = 22050;

                // 2. Pre-Processing (Mono & Resampling) - Non-Blocking
                if (audioObj.Channels != 1) await audioObj.RechannelAsync(1).ConfigureAwait(false);
                if (audioObj.SampleRate != targetSR) await audioObj.ResampleAsync(targetSR).ConfigureAwait(false);

                float[] mono = audioObj.Data;
                int sampleRate = audioObj.SampleRate;
                progress?.Report(0.10);

                // 3. Feature Extraction (FFT & Mel) - In Hintergrund-Task auslagern
                float[] melFlat = await Task.Run(() =>
                {
                    float[] padded = new float[mono.Length + nFft];
                    Array.Copy(mono, 0, padded, nFft / 2, mono.Length);

                    int fftBins = nFft / 2 + 1;
                    int totalFrames = 1 + (padded.Length - nFft) / hop;
                    float[,] melFilter = BuildMelFilterBank(nMels, fftBins, sampleRate, nFft);
                    float[] output = new float[totalFrames * nMels];

                    float[] window = new float[nFft];
                    for (int i = 0; i < nFft; i++) window[i] = 0.5f - 0.5f * (float) Math.Cos(2 * Math.PI * i / nFft);

                    System.Numerics.Complex[] fft = new System.Numerics.Complex[nFft];

                    for (int frame = 0; frame < totalFrames; frame++)
                    {
                        int offset = frame * hop;
                        for (int i = 0; i < nFft; i++) fft[i] = new System.Numerics.Complex(padded[offset + i] * window[i], 0);

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

                        // Öfter reporten für flüssige Anzeige
                        if (frame % 200 == 0) progress?.Report(0.10 + (0.30 * frame / totalFrames));
                    }
                    return output;
                }).ConfigureAwait(false);

                int totalFramesInMel = melFlat.Length / nMels;
                progress?.Report(0.40);

                // 4. ONNX Inference (Chunked) - GPU/DirectML ist von Natur aus async-freundlich
                const int chunkSize = 1024;
                const int chunkHop = 512;
                List<float> activationCurve = new();

                await Task.Run(() => {
                    for (int start = 0; start < totalFramesInMel; start += chunkHop)
                    {
                        int currentSize = Math.Min(chunkSize, totalFramesInMel - start);
                        float[] chunk = new float[currentSize * nMels];
                        Array.Copy(melFlat, start * nMels, chunk, 0, currentSize * nMels);

                        var tensor = new DenseTensor<float>(chunk, [1, currentSize, nMels]);
                        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(this._session.InputMetadata.Keys.First(), tensor) };

                        // session.Run blockiert den Thread kurzzeitig, daher im Task.Run
                        using var results = this._session.Run(inputs);
                        lock (activationCurve) activationCurve.AddRange(results.First().AsEnumerable<float>());

                        progress?.Report(0.40 + (0.30 * start / totalFramesInMel));
                        if (totalFramesInMel - start <= chunkSize) break;
                    }
                }).ConfigureAwait(false);

                // 5. Robuste Autokorrelation - Schwere Mathematik im Hintergrund
                double finalBpm = await Task.Run(() =>
                {
                    double secondsPerFrame = (double) hop / sampleRate;
                    int minLag = (int) (60.0 / (220.0 * secondsPerFrame));
                    int maxLag = (int) (60.0 / (50.0 * secondsPerFrame));

                    double[] acf = new double[maxLag + 2];
                    double maxVal = -1;
                    int bestLag = 0;

                    for (int lag = minLag; lag <= maxLag; lag++)
                    {
                        double sum = 0;
                        int limit = Math.Min(activationCurve.Count - lag, activationCurve.Count / 2);
                        for (int i = 0; i < limit; i++)
                        {
                            sum += activationCurve[i] * activationCurve[i + lag];
                        }
                        acf[lag] = sum / limit;

                        if (acf[lag] > maxVal)
                        {
                            maxVal = acf[lag];
                            bestLag = lag;
                        }
                        if (lag % 5 == 0) progress?.Report(0.70 + (0.30 * (lag - minLag) / (maxLag - minLag)));
                    }

                    // Sub-Frame Interpolation
                    double refinedLag = bestLag;
                    if (bestLag > minLag && bestLag < maxLag)
                    {
                        double alpha = acf[bestLag - 1];
                        double beta = acf[bestLag];
                        double gamma = acf[bestLag + 1];
                        double p = 0.5 * (alpha - gamma) / (alpha - 2 * beta + gamma);
                        refinedLag = bestLag + p;
                    }

                    double bpm = 60.0 / (refinedLag * secondsPerFrame);
                    while (bpm < 85) bpm *= 2;
                    while (bpm > 175) bpm /= 2;
                    return bpm;
                }).ConfigureAwait(false);

                await StaticLogger.LogAsync($"{audioObj.Name}: {finalBpm:F3} BPM detected").ConfigureAwait(false);
                progress?.Report(1.0);
                return Math.Round(finalBpm, 3);
            }
            catch (Exception ex)
            {
                await StaticLogger.LogAsync(ex).ConfigureAwait(false);
                return null;
            }
            finally
            {
                if (audioObj != null)
                {
                    this.AudioHandler.RemoveAudio(audioObj.Id);
                    audioObj.Dispose();
                }
            }
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




    }
}
