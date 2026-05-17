using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using Microsoft.VisualBasic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Globalization;

namespace OnnxBpmScanner.Core
{
    public class AudioObj : IDisposable
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly DateTime CreatedAt = DateTime.UtcNow;

        public string FilePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;


        public float[] Data { get; set; } = Array.Empty<float>();
        public int Length => this.Data.Length;
        public int SampleRate { get; set; } = 0;
        public int Channels { get; set; } = 0;
        public int BitDepth { get; set; } = 0;
        public TimeSpan Duration => (this.SampleRate > 0 && this.Channels > 0) ? TimeSpan.FromSeconds((double) this.Length / this.Channels / this.SampleRate) : TimeSpan.Zero;


        public AudioObj()
        {

        }

        public AudioObj(string filePath)
        {
            if (File.Exists(filePath))
            {
                this.LoadFromFile(filePath);
            }
            else
            {
                this.Dispose();
            }
        }

        public AudioObj(float[] data, int sampleRate, int channels, int bitDepth, string name = "")
        {
            this.Data = data;
            this.SampleRate = sampleRate;
            this.Channels = channels;
            this.BitDepth = bitDepth;
            this.Name = name;
        }



        public void Dispose()
        {
            // Clear all data and reset fields
            this.Data = Array.Empty<float>();
            this.FilePath = string.Empty;
            this.Name = string.Empty;
            this.SampleRate = 0;
            this.Channels = 0;
            this.BitDepth = 0;

            GC.SuppressFinalize(this);
        }



        public bool LoadFromFile(string filePath)
        {
            // Load using NAudio AudioFileReader and set all Fields
            try
            {
                this.FilePath = filePath;
                using (var reader = new AudioFileReader(filePath))
                {
                    this.SampleRate = reader.WaveFormat.SampleRate;
                    this.Channels = reader.WaveFormat.Channels;
                    this.BitDepth = reader.WaveFormat.BitsPerSample;
                    var totalSamples = (int) (reader.Length / (reader.WaveFormat.BitsPerSample / 8));
                    // Ensure we don't allocate absurdly large arrays
                    if (totalSamples < 0 || totalSamples > 100_000_000)
                    {
                        totalSamples = 0;
                    }

                    var buffer = new float[totalSamples];
                    int samplesRead = reader.Read(buffer, 0, totalSamples);
                    if (samplesRead < 0)
                    {
                        samplesRead = 0;
                    }

                    this.Data = buffer[..samplesRead];
                }
                this.Name = Path.GetFileNameWithoutExtension(filePath);
            }
            catch (Exception ex)
            {
                this.FilePath = string.Empty;
                StaticLogger.Log($"Failed to load audio file: ");
                StaticLogger.Log(ex);
                return false;
            }

            return true;
        }

        public async Task<bool> ResampleAsync(int targetSampleRate, int? targetBitDepth = null)
        {
            if (targetSampleRate == this.SampleRate)
            {
                // If only bit depth should change, update it and return
                if (targetBitDepth.HasValue)
                {
                    this.BitDepth = targetBitDepth.Value;
                }
                return true; // Already at target sample rate
            }

            try
            {
                return await Task.Run(() =>
                {
                    // Create a wave format for the current data
                    var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                    var byteData = new byte[this.Data.Length * sizeof(float)];
                    Buffer.BlockCopy(this.Data, 0, byteData, 0, byteData.Length);

                    using var ms = new MemoryStream(byteData);
                    var sampleProvider = new RawSourceWaveStream(ms, sourceFormat).ToSampleProvider();
                    var resampler = new WdlResamplingSampleProvider(sampleProvider, targetSampleRate);

                    // Read resampled data
                    var resampledList = new List<float>();
                    float[] buffer = new float[8192];
                    int samplesRead;
                    while ((samplesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        // AddRange for performance and to avoid multiple resizes
                        if (samplesRead == buffer.Length)
                        {
                            resampledList.AddRange(buffer);
                        }
                        else
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                resampledList.Add(buffer[i]);
                            }
                        }
                    }

                    // Update the AudioObj with resampled data
                    this.Data = resampledList.ToArray();
                    this.SampleRate = targetSampleRate;
                    // Update bit depth if requested, otherwise keep existing
                    if (targetBitDepth.HasValue)
                    {
                        this.BitDepth = targetBitDepth.Value;
                    }

                    return true;
                });
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to resample audio:");
                StaticLogger.Log(ex);
                return false;
            }
        }

        public async Task<bool> RechannelAsync(int targetChannels)
        {
            if (targetChannels == this.Channels)
            {
                return true;
            }

            try
            {
                return await Task.Run(async () =>
                {
                    var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                    byte[] byteData = new byte[this.Data.Length * sizeof(float)];
                    var provider = new BufferedWaveProvider(sourceFormat)
                    {
                        BufferLength = byteData.Length,
                        ReadFully = false
                    };
                    Buffer.BlockCopy(this.Data, 0, byteData, 0, byteData.Length);
                    provider.AddSamples(byteData, 0, byteData.Length);

                    var sampleProvider = provider.ToSampleProvider();


                    if (targetChannels != 1 && targetChannels != 2)
                    {
                        // Use the exact other than set channels, if it's not mono or stereo
                        targetChannels = this.Channels == 1 ? 2 : 1;
                        await StaticLogger.LogAsync($"Invalid bitdepth detected ({targetChannels}). Using {targetChannels} since audio has {this.Channels} channels.");
                    }

                    ISampleProvider rechanneledProvider;
                    if (targetChannels == 1)
                    {
                        rechanneledProvider = new StereoToMonoSampleProvider(sampleProvider);
                    }
                    else if (targetChannels == 2)
                    {
                        rechanneledProvider = new MonoToStereoSampleProvider(sampleProvider);
                    }
                    else
                    {
                        // This should never happen due to the check above, but just in case
                        StaticLogger.Log($"Unexpected target channel count: {targetChannels}. No rechanneling applied.");
                        return false;
                    }

                    var rechanneledList = new List<float>();
                    float[] buffer = new float[8192];
                    int samplesRead;
                    while ((samplesRead = rechanneledProvider.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (samplesRead == buffer.Length)
                        {
                            rechanneledList.AddRange(buffer);
                        }
                        else
                        {
                            for (int i = 0; i < samplesRead; i++)
                            {
                                rechanneledList.Add(buffer[i]);
                            }
                        }
                    }

                    this.Data = rechanneledList.ToArray();
                    this.Channels = targetChannels;

                    return true;
                });
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to rechannel audio:");
                StaticLogger.Log(ex);
                return false;
            }
        }



        public string? ExportWav(string? outputDirectory = null, string? fileName = null, int bits = 16)
        {
            outputDirectory ??= AudioHandling.ExportDirectory;
            if (string.IsNullOrEmpty(outputDirectory))
            {
                StaticLogger.Log("Export directory is not set.");
                return null;
            }

            if (!Directory.Exists(outputDirectory))
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    StaticLogger.Log($"Audio output directory '{outputDirectory}' created.");
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Failed to create export directory: {outputDirectory}");
                    StaticLogger.Log(ex);
                    return null;
                }
            }

            // Dateinamen bestimmen (Name, Id oder Fallback)
            string baseName = fileName ?? (!string.IsNullOrEmpty(this.Name) ? this.Name : this.Id.ToString());
            string outputPath = Path.Combine(outputDirectory, $"{baseName}.wav");

            // Falls Datei existiert, Index anhängen (z.B. "Aufnahme (1).wav")
            int copyIndex = 1;
            while (File.Exists(outputPath))
            {
                outputPath = Path.Combine(outputDirectory, $"{baseName} ({copyIndex++}).wav");
            }

            string? outFile;
            try
            {
                // Bestimme die Ausgabebit-Tiefe: falls der Caller den Standard (16) übergeben hat,
                // aber dieses AudioObj eine eigene BitDepth gesetzt hat, benutze diese.
                int outputBits = bits;
                if (this.BitDepth > 0 && bits == 16)
                {
                    outputBits = this.BitDepth;
                }

                // NAudio WaveFormat definieren. Für 32 Bit nutzen wir das IEEE-Float-Format.
                WaveFormat format;
                if (outputBits == 32)
                {
                    format = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                }
                else
                {
                    format = new WaveFormat(this.SampleRate, outputBits, this.Channels);
                }

                using (var writer = new WaveFileWriter(outputPath, format))
                {
                    // Die float-Daten in den Writer schreiben
                    // WriteSamples bei NAudio konvertiert automatisch basierend auf dem 'format'
                    writer.WriteSamples(this.Data, 0, this.Data.Length);
                }

                StaticLogger.Log($"Audio exported successfully: {outputPath}");
                outFile = outputPath;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to export audio to WAV: {outputPath}");
                StaticLogger.Log(ex);
                outFile = null;
            }

            return outFile;
        }

        public async Task<string?> ExportWavAsync(string? outputDirectory = null, string? fileName = null, int bits = 16)
        {
            return await Task.Run(() => this.ExportWav(outputDirectory, fileName, bits));
        }

        public async Task<string?> SerializeAsBase64Async(int? sampleRate = null, int? channels = null, int? bitDepth = null)
        {
            if (sampleRate.HasValue)
            {
                bool success = await this.ResampleAsync(sampleRate.Value, bitDepth);
                if (!success)
                {
                    await StaticLogger.LogAsync($"Failed to resample audio for Base64 serialization. Aborting.");
                    return null;
                }
            }

            if (channels.HasValue)
            {
                bool success = await this.RechannelAsync(channels.Value);
                if (!success)
                {
                    await StaticLogger.LogAsync("Failed to rechannel audio for Base64 serialization. Aborting.");
                    return null;
                }
            }

            return await Task.Run(() =>
            {
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        // NAudio WaveFormat definieren
                        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
                        using (var writer = new WaveFileWriter(ms, format))
                        {
                            // Die float-Daten in den Writer schreiben
                            writer.WriteSamples(this.Data, 0, this.Data.Length);
                            writer.Flush();
                        }
                        // Konvertiere den MemoryStream in ein Base64-String
                        string base64String = Convert.ToBase64String(ms.ToArray());
                        return base64String;
                    }
                }
                catch (Exception ex)
                {
                    StaticLogger.Log($"Failed to serialize audio as Base64:");
                    StaticLogger.Log(ex);
                    return null;
                }
            });
        }

        public async Task TrimLeadingAndTrailingSilenceAsync(
            float silenceThreshold = 0.0015f,
            int minSilenceDurationMs = 350,
            int analysisWindowMs = 20,
            int analysisHopMs = 10,
            float adaptiveNoiseMultiplier = 2.5f)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (this.Data.Length == 0 || this.SampleRate <= 0 || this.Channels <= 0)
                    {
                        StaticLogger.Log($"Silence trim skipped for {this.Name}: audio buffer is empty or format is invalid.");
                        return;
                    }

                    int samplesPerChannel = this.Data.Length / this.Channels;
                    if (samplesPerChannel == 0)
                    {
                        StaticLogger.Log($"Silence trim skipped for {this.Name}: no samples available.");
                        return;
                    }

                    int windowSize = Math.Max(1, (int) Math.Round(this.SampleRate * (analysisWindowMs / 1000.0)));
                    int hopSize = Math.Max(1, (int) Math.Round(this.SampleRate * (analysisHopMs / 1000.0)));
                    int minSilenceSamples = Math.Max(1, (int) Math.Round(this.SampleRate * (minSilenceDurationMs / 1000.0)));
                    int frameCount = Math.Max(1, 1 + (int) Math.Ceiling(Math.Max(0, samplesPerChannel - windowSize) / (double) hopSize));

                    float[] frameRms = new float[frameCount];
                    float peakRms = 0f;

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        int startSample = frame * hopSize;
                        int endSample = Math.Min(samplesPerChannel, startSample + windowSize);
                        if (startSample >= endSample)
                        {
                            continue;
                        }

                        double sumSquares = 0;
                        int sampleCount = 0;
                        for (int i = startSample; i < endSample; i++)
                        {
                            double monoSample = 0;
                            int baseIndex = i * this.Channels;
                            for (int ch = 0; ch < this.Channels; ch++)
                            {
                                monoSample += this.Data[baseIndex + ch];
                            }

                            monoSample /= this.Channels;
                            sumSquares += monoSample * monoSample;
                            sampleCount++;
                        }

                        if (sampleCount == 0)
                        {
                            continue;
                        }

                        float rms = (float) Math.Sqrt(sumSquares / sampleCount);
                        frameRms[frame] = rms;
                        if (rms > peakRms)
                        {
                            peakRms = rms;
                        }
                    }

                    if (peakRms <= 0f)
                    {
                        StaticLogger.Log($"{this.Name}: trim start=0.00s end=0.00s (audio appears silent)");
                        return;
                    }

                    float[] sortedRms = new float[frameRms.Length];
                    Array.Copy(frameRms, sortedRms, frameRms.Length);
                    Array.Sort(sortedRms);

                    int percentileIndex = Math.Clamp((int) Math.Floor((sortedRms.Length - 1) * 0.2), 0, sortedRms.Length - 1);
                    float noiseFloor = sortedRms[percentileIndex];
                    float effectiveThreshold = Math.Max(silenceThreshold, Math.Max(noiseFloor * adaptiveNoiseMultiplier, peakRms * 0.01f));

                    int minSilenceFrames = Math.Max(1, (int) Math.Ceiling(minSilenceSamples / (double) hopSize));
                    int minActiveFrames = Math.Max(2, Math.Min(8, minSilenceFrames / 2));

                    int firstActiveFrame = -1;
                    for (int frame = 0; frame <= frameRms.Length - minActiveFrames; frame++)
                    {
                        bool isActiveRun = true;
                        for (int offset = 0; offset < minActiveFrames; offset++)
                        {
                            if (frameRms[frame + offset] < effectiveThreshold)
                            {
                                isActiveRun = false;
                                break;
                            }
                        }

                        if (isActiveRun)
                        {
                            firstActiveFrame = frame;
                            break;
                        }
                    }

                    int lastActiveFrame = -1;
                    for (int frame = frameRms.Length - minActiveFrames; frame >= 0; frame--)
                    {
                        bool isActiveRun = true;
                        for (int offset = 0; offset < minActiveFrames; offset++)
                        {
                            if (frameRms[frame + offset] < effectiveThreshold)
                            {
                                isActiveRun = false;
                                break;
                            }
                        }

                        if (isActiveRun)
                        {
                            lastActiveFrame = frame + minActiveFrames - 1;
                            break;
                        }
                    }

                    if (firstActiveFrame < 0 || lastActiveFrame < 0 || firstActiveFrame > lastActiveFrame)
                    {
                        StaticLogger.Log($"{this.Name}: trim start=0.00s end=0.00s (no stable active section)");
                        return;
                    }

                    int leadingSamples = firstActiveFrame * hopSize;
                    int trailingStartSample = Math.Min(samplesPerChannel, (lastActiveFrame * hopSize) + windowSize);
                    int trailingSamples = Math.Max(0, samplesPerChannel - trailingStartSample);

                    if (leadingSamples < minSilenceSamples)
                    {
                        leadingSamples = 0;
                    }

                    if (trailingSamples < minSilenceSamples)
                    {
                        trailingSamples = 0;
                        trailingStartSample = samplesPerChannel;
                    }

                    int trimmedSampleCount = samplesPerChannel - leadingSamples - trailingSamples;
                    if (trimmedSampleCount <= 0)
                    {
                        StaticLogger.Log($"{this.Name}: trim start=0.00s end=0.00s (trim would remove full audio)");
                        return;
                    }

                    if (leadingSamples == 0 && trailingSamples == 0)
                    {
                        StaticLogger.Log($"{this.Name}: trim start=0.00s end=0.00s");
                        return;
                    }

                    int newLength = trimmedSampleCount * this.Channels;
                    float[] trimmedData = new float[newLength];
                    Array.Copy(this.Data, leadingSamples * this.Channels, trimmedData, 0, newLength);
                    this.Data = trimmedData;

                    double leadingSeconds = leadingSamples / (double) this.SampleRate;
                    double trailingSeconds = trailingSamples / (double) this.SampleRate;
                    StaticLogger.Log($"{this.Name}: trim start={leadingSeconds:F2}s end={trailingSeconds:F2}s");
                }
                catch (Exception ex)
                {
                    StaticLogger.Log("Failed to trim leading/trailing silence:");
                    StaticLogger.Log(ex);
                }
            });
        }

        public async Task CutTrailingSilenceAsync(float silenceThreshold = 0.001f, int minSilenceDurationMs = 500)
        {
            await this.TrimLeadingAndTrailingSilenceAsync(silenceThreshold, minSilenceDurationMs);
        }


        public bool WriteBpmTag(double bpm)
        {
            if (string.IsNullOrEmpty(this.FilePath) || !File.Exists(this.FilePath))
            {
                StaticLogger.Log($"Cannot write BPM tag: File path is invalid or file does not exist: {this.FilePath}");
                return false;
            }

            if (bpm < 300)
            {
                bpm *= 100;
            }

            try
            {
                using (var reader = new AudioFileReader(this.FilePath))
                {
                    var tagLibFile = TagLib.File.Create(this.FilePath);
                    // Standard integer BPM field
                    tagLibFile.Tag.BeatsPerMinute = (uint) Math.Round(bpm);

                    // Also write a precision BPM text frame (TXXX) with two decimals for DJ software
                    var id3v2Tag = tagLibFile.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                    if (id3v2Tag != null)
                    {
                        // Standard text frame TBPM (may be used by some tag readers)
                        var tbpmFrame = TagLib.Id3v2.TextInformationFrame.Get(id3v2Tag, "TBPM", true);
                        tbpmFrame.Text = new[] { bpm.ToString("F2", CultureInfo.InvariantCulture) };

                        // Also set a user text (TXXX) frame "BPM" for software that reads custom fields
                        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2Tag, "BPM", true);
                        frame.Text = new[] { bpm.ToString("F2", CultureInfo.InvariantCulture) };
                    }

                    // Also place a human-readable BPM with decimals into the common Comment field
                    tagLibFile.Tag.Comment = $"BPM: {bpm.ToString("F2", CultureInfo.InvariantCulture)}";

                    tagLibFile.Save();
                }
                return true;
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Failed to write BPM tag to audio file: {this.FilePath}");
                StaticLogger.Log(ex);
                return false;
            }
        }

        public static bool WriteBpmTagToFile(string filePath, double bpm)
        {
            try
            {
                using (var tagLibFile = TagLib.File.Create(filePath))
                {
                    // 1. Standard-Feld (Ganzzahl für Windows Explorer)
                    tagLibFile.Tag.BeatsPerMinute = (uint) Math.Round(bpm);

                    // 2. Präzisions-Feld (TXXX Frame für DJ-Software)
                    var id3v2Tag = tagLibFile.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                    if (id3v2Tag != null)
                    {
                        // Write TBPM text frame with decimal precision
                        var tbpmFrame = TagLib.Id3v2.TextInformationFrame.Get(id3v2Tag, "TBPM", true);
                        tbpmFrame.Text = new[] { bpm.ToString("F2", CultureInfo.InvariantCulture) };

                        // Wir suchen ein existierendes BPM-Textfeld oder erstellen ein neues (TXXX)
                        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2Tag, "BPM", true);
                        frame.Text = new[] { bpm.ToString("F2", CultureInfo.InvariantCulture) };
                    }

                    // Also write a common comment with the decimal BPM so Windows Explorer can show it in the Comments column
                    tagLibFile.Tag.Comment = $"BPM: {bpm.ToString("F2", CultureInfo.InvariantCulture)}";

                    tagLibFile.Save();
                }
                return true;
            }
            catch (Exception ex)
            {
                // Nutze deinen StaticLogger zur Fehlerverfolgung
                StaticLogger.Log($"TagLib Error: {ex.Message}");
                return false;
            }
        }


    }
}
