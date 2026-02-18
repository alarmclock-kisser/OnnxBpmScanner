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



        public string[] GetAudioFiles(string? customDirectory = null)
        {
            string audioPath = this.InputDirectory;
            if (customDirectory != null)
            {
                audioPath = customDirectory;
            }

            // Get wav, mp3, flac files
            var audioFiles = Directory.GetFiles(audioPath, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            this.AudioFiles.AddRange(audioFiles);

            foreach (var file in audioFiles)
            {
                StaticLogger.Log($"Found audio file: {file}");
            }

            return this.AudioFiles.ToArray();
        }



        public async Task<Dictionary<string, double?>> EstimateBpmForAllAudioFilesAsync(IProgress<double>? progress = null)
        {
            var results = new ConcurrentDictionary<string, double?>();
            int totalFiles = this.AudioFiles.Count;
            int processedFiles = 0;
            foreach (var file in this.AudioFiles)
            {
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
                audioObj = await this.AudioHandler.ImportAudioAsync(audioFilePath);
                if (audioObj == null) return null;

                StaticLogger.Log($"Processing {audioObj.Name}...");

                // 1. Mono & High-Quality Resampling (0% - 10%)
                if (audioObj.Channels != 1) await audioObj.RechannelAsync(1);
                const int targetSR = 22050;
                if (audioObj.SampleRate != targetSR) await audioObj.ResampleAsync(targetSR);

                float[] mono = audioObj.Data;
                int sampleRate = audioObj.SampleRate;
                progress?.Report(0.10);

                // 2. Feature Extraction (10% - 40%)
                const int nFft = 1024;
                const int hop = 512;
                const int nMels = 128;

                float[] paddedMono = new float[mono.Length + nFft];
                Array.Copy(mono, 0, paddedMono, nFft / 2, mono.Length);

                int fftBins = nFft / 2 + 1;
                int totalFrames = 1 + (paddedMono.Length - nFft) / hop;

                float[,] melFilter = BuildMelFilterBank(nMels, fftBins, sampleRate, nFft);
                float[] melFlat = new float[totalFrames * nMels];
                float[] window = new float[nFft];
                for (int i = 0; i < nFft; i++) window[i] = 0.5f - 0.5f * (float) Math.Cos(2 * Math.PI * i / nFft);

                System.Numerics.Complex[] fft = new System.Numerics.Complex[nFft];

                for (int frame = 0; frame < totalFrames; frame++)
                {
                    int offset = frame * hop;
                    for (int i = 0; i < nFft; i++) fft[i] = new System.Numerics.Complex(paddedMono[offset + i] * window[i], 0);

                    MathNet.Numerics.IntegralTransforms.Fourier.Forward(fft, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);

                    for (int m = 0; m < nMels; m++)
                    {
                        double sum = 0;
                        for (int b = 0; b < fftBins; b++)
                        {
                            double magSq = fft[b].Real * fft[b].Real + fft[b].Imaginary * fft[b].Imaginary;
                            sum += magSq * melFilter[m, b];
                        }
                        melFlat[frame * nMels + m] = (float) Math.Log10(sum + 1e-10);
                    }
                    if (frame % 500 == 0) progress?.Report(0.10 + (0.30 * frame / totalFrames));
                }

                // 3. ONNX Inference (40% - 70%)
                const int chunkSize = 1024;
                const int chunkHop = 512;
                List<float> activationCurve = new();

                for (int start = 0; start < totalFrames; start += chunkHop)
                {
                    int currentSize = Math.Min(chunkSize, totalFrames - start);
                    float[] chunk = new float[currentSize * nMels];
                    Array.Copy(melFlat, start * nMels, chunk, 0, currentSize * nMels);

                    var tensor = new DenseTensor<float>(chunk, [1, currentSize, nMels]);
                    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(this._session.InputMetadata.Keys.First(), tensor) };

                    using var results = this._session.Run(inputs);
                    activationCurve.AddRange(results.First().AsEnumerable<float>());

                    progress?.Report(0.40 + (0.30 * start / totalFrames));
                    if (totalFrames - start <= chunkSize) break;
                }

                // 4. Robuste Autokorrelation (70% - 100%)
                double secondsPerFrame = (double) hop / sampleRate;
                // Bereich 60 bis 200 BPM
                int minLag = (int) (60.0 / (220.0 * secondsPerFrame));
                int maxLag = (int) (60.0 / (50.0 * secondsPerFrame));

                double[] acf = new double[maxLag + 2];
                double maxVal = -1;
                int bestLag = 0;

                for (int lag = minLag; lag <= maxLag; lag++)
                {
                    double sum = 0;
                    int count = 0;
                    // Wir nutzen nur die erste Hälfte der Kurve für stabilere Korrelation
                    int limit = Math.Min(activationCurve.Count - lag, activationCurve.Count / 2);
                    for (int i = 0; i < limit; i++)
                    {
                        sum += activationCurve[i] * activationCurve[i + lag];
                        count++;
                    }
                    acf[lag] = sum / count;

                    if (acf[lag] > maxVal)
                    {
                        maxVal = acf[lag];
                        bestLag = lag;
                    }
                    if (lag % 10 == 0) progress?.Report(0.70 + (0.30 * (lag - minLag) / (maxLag - minLag)));
                }

                // 5. Mathematische Peak-Verfeinerung (Sub-Frame Interpolation)
                double refinedLag = bestLag;
                if (bestLag > minLag && bestLag < maxLag)
                {
                    double alpha = acf[bestLag - 1];
                    double beta = acf[bestLag];
                    double gamma = acf[bestLag + 1];

                    // Quadratische Interpolation für den exakten Peak zwischen den Frames
                    double p = 0.5 * (alpha - gamma) / (alpha - 2 * beta + gamma);
                    refinedLag = bestLag + p;
                }

                double bpm = 60.0 / (refinedLag * secondsPerFrame);

                // 6. Intelligente Oktaven-Korrektur (Vermeidung des 80 vs 160 Fehlers)
                // Wir suchen, ob ein Peak bei der doppelten oder halben Frequenz stärker ist
                while (bpm < 85) bpm *= 2;
                while (bpm > 175) bpm /= 2;

                StaticLogger.Log($"{audioObj.Name}: {bpm:F3} BPM detected via ACF-Refinement");
                progress?.Report(1.0);
                return Math.Round(bpm, 3);
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Inference Error: {ex.Message}");
                return null;
            }
            finally
            {
                if (audioObj != null) { this.AudioHandler.RemoveAudio(audioObj); audioObj.Dispose(); }
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
