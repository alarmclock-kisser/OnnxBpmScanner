using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OnnxBpmScanner.Core;
using OnnxBpmScanner.Runtime;

namespace OnnxBpmScanner.Cli
{
    internal class Program
    {
        internal OnnxService? Onnx = null;

        static async Task Main(string[] args)
        {
            // Args:
            // --ressources "path/to/ressources/directory"
            // --model "path/to/model.onnx" or --model "/default" to load the first model found in directory
            // --directory "path/to/directory/with/audio/files" (optional, defaults to current directory)

            string? ressourcesDirectory = null;
            string? modelPath = "/default";
            string? audioDirectory = Directory.GetCurrentDirectory();
            List<string> audioFiles = new List<string>();
            int maxFiles = 0; // No limit
            int maxDurationMinutes = 0; // No limit
            int directMlDeviceId = 0;
            bool writeBpmTags = false;

            // Track which options were explicitly provided via args so we can fallback to appsettings.json
            bool hasRessourcesArg = false;
            bool hasModelArg = false;
            bool hasAudioDirArg = false;
            bool hasAudioFilesArg = false;
            bool hasDeviceArg = false;
            bool hasWriteTagsArg = false;

            // simple args parsing: support both "--key value" and "--key=value"
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--ressources", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    ressourcesDirectory = args[++i];
                    hasRessourcesArg = true;
                }
                else if (a.StartsWith("--ressources=", StringComparison.OrdinalIgnoreCase))
                {
                    ressourcesDirectory = a.Substring("--ressources=".Length);
                    hasRessourcesArg = true;
                }
                else if (string.Equals(a, "--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    modelPath = args[++i];
                    hasModelArg = true;
                }
                else if (a.StartsWith("--model=", StringComparison.OrdinalIgnoreCase))
                {
                    modelPath = a.Substring("--model=".Length);
                    hasModelArg = true;
                }
                else if (string.Equals(a, "--directory", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    audioDirectory = args[++i];
                    hasAudioDirArg = true;
                }
                else if (a.StartsWith("--directory=", StringComparison.OrdinalIgnoreCase))
                {
                    audioDirectory = a.Substring("--directory=".Length);
                    hasAudioDirArg = true;
                }
                else if (a.StartsWith("--audio-files=", StringComparison.OrdinalIgnoreCase))
                {
                    // Support multiple audio files
                    var files = a.Substring("--audio-files=".Length).Split(';', StringSplitOptions.RemoveEmptyEntries);
                    audioFiles.AddRange(files);
                    hasAudioFilesArg = true;
                }
                else if (a.StartsWith("--device=", StringComparison.OrdinalIgnoreCase))
                {
                    var idStr = a.Substring("--device=".Length);
                    if (int.TryParse(idStr, out int id))
                    {
                        directMlDeviceId = id;
                        hasDeviceArg = true;
                    }
                    else
                    {
                        Console.WriteLine($"Invalid DML device ID: {idStr}. Using default (0).");
                    }
                }
                else if (string.Equals(a, "--write-tags", StringComparison.OrdinalIgnoreCase))
                {
                    writeBpmTags = true;
                    hasWriteTagsArg = true;
                }
            }

            // If args did not provide values, try to read defaults from appsettings.json
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (!File.Exists(configPath))
                {
                    configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
                }

                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (!hasRessourcesArg && root.TryGetProperty("RessourcesDirectory", out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        var v = r.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            ressourcesDirectory = v;
                        }
                    }

                    if (!hasModelArg && root.TryGetProperty("ModelPath", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        var v = m.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            modelPath = v;
                        }
                    }

                    if (!hasAudioDirArg && root.TryGetProperty("AudiosDirectory", out var aDir) && aDir.ValueKind == JsonValueKind.String)
                    {
                        var v = aDir.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            audioDirectory = v;
                            if (audioDirectory.StartsWith("/mymusic", StringComparison.OrdinalIgnoreCase))
                            {
                                audioDirectory = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                            }
                        }
                    }

                    if (!hasAudioFilesArg && root.TryGetProperty("AudioFiles", out var af) && af.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in af.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                var f = item.GetString();
                                if (!string.IsNullOrWhiteSpace(f))
                                {
                                    audioFiles.Add(f!);
                                }
                            }
                        }
                    }

                    if (!hasAudioDirArg && root.TryGetProperty("MaxFiles", out var maxF) && maxF.ValueKind == JsonValueKind.String)
                    {
                        maxFiles = maxF.GetInt32();
                    }

                    if (!hasAudioDirArg && root.TryGetProperty("MaxAudioDurationMinutes", out var maxAD) && maxAD.ValueKind == JsonValueKind.String)
                    {
                        maxDurationMinutes = maxAD.GetInt32();
                    }


                    if (!hasDeviceArg && root.TryGetProperty("DirectMlDeviceId", out var dml) && dml.ValueKind == JsonValueKind.Number)
                    {
                        if (dml.TryGetInt32(out var id))
                        {
                            directMlDeviceId = id;
                        }
                    }

                    if (!hasWriteTagsArg && root.TryGetProperty("WriteBpmTags", out var wt) && (wt.ValueKind == JsonValueKind.True || wt.ValueKind == JsonValueKind.False))
                    {
                        if (wt.ValueKind == JsonValueKind.True)
                        {
                            writeBpmTags = true;
                        }
                        else if (wt.ValueKind == JsonValueKind.False)
                        {
                            writeBpmTags = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to read appsettings.json: {ex.Message}");
            }

            // Instantiate OnnxService with optional custom ressources directory and model selection
            using var onnx = new OnnxService(ressourcesDirectory, modelPath, audioDirectory);
            onnx.AudioFiles.AddRange(audioFiles);

            Console.WriteLine($"Ressources directory: {(ressourcesDirectory ?? "(default)")}");
            Console.WriteLine($"Requested model: {modelPath}");
            Console.WriteLine($"Loaded model: {(onnx.IsModelLoaded ? onnx.LoadedModelPath : "(none)")}");
            Console.WriteLine($"Audio directory: {(audioDirectory ?? Directory.GetCurrentDirectory())}");


            // Now run inference get Dictionary<string, double?> results = onnx.EstimateBpmForAllAudioFilesAsync();
            // Use carriage return to overwrite the same console line for progress updates instead of writing new lines
            var progress = new Progress<double>(p =>
            {
                var text = $"Progress: {p:P1}";
                Console.Write('\r');
                Console.Write(text);
                // Pad remainder to clear previous longer content
                Console.Write(new string(' ', Math.Max(0, 5)));
                Console.Out.Flush();
            });

            var results = await onnx.EstimateBpmForAllAudioFilesAsync(progress, maxFiles, maxDurationMinutes);

            // Print results
            Console.WriteLine("-----");
            Console.WriteLine("BPM estimation results:");
            foreach (var kvp in results)
            {
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(kvp.Key)}: {(kvp.Value.HasValue ? $"{kvp.Value.Value:F3} BPM" : "Estimation failed")}");
            }

            // Ask if should write to file bpm tags
            var key = default(ConsoleKeyInfo);
            if (writeBpmTags == false)
            {
                Console.WriteLine("Write BPM tags to audio files? (y/n)");
                key = Console.ReadKey();
            }
            if (key.Key == ConsoleKey.Y || writeBpmTags)
            {
                foreach (var kvp in results)
                {
                    if (kvp.Value.HasValue)
                    {
                        try
                        {
                            bool writtenTag = AudioObj.WriteBpmTagToFile(kvp.Key, kvp.Value.Value);
                            if (writtenTag)
                            {
                                Console.WriteLine($"\nWrote BPM tag to {Path.GetFileName(kvp.Key)}");
                            }
                            else
                            {
                                Console.WriteLine($"\nFailed to write BPM tag to {Path.GetFileName(kvp.Key)}: Unsupported file format or tag writing failed.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"\nFailed to write BPM tag to {Path.GetFileName(kvp.Key)}: {ex.Message}");
                        }
                    }
                }
            }

            // Ask if copy StaticLogger logs to clipboard
            Console.WriteLine("Copy logs to clipboard? (y/n)");
            key = Console.ReadKey();
            if (key.Key == ConsoleKey.Y)
            {
                var logs = StaticLogger.LogEntriesBindingList;
                try
                {
                    await TextCopy.ClipboardService.SetTextAsync(string.Join(Environment.NewLine, logs));
                    Console.WriteLine("\nLogs copied to clipboard.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nFailed to copy logs to clipboard: {ex.Message}");
                }
            }


        }
    }
}
