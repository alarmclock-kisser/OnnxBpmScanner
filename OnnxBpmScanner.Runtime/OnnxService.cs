using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using OnnxBpmScanner.Core;

namespace OnnxBpmScanner.Runtime
{
    public partial class OnnxService : IDisposable
    {
        public List<string> ModelPaths { get; set; } = [];
        public string? LoadedModelPath { get; private set; } = null;
        public bool IsModelLoaded => this.LoadedModelPath != null && this._session != null;

        private InferenceSession? _session = null;



        public OnnxService(string? customDirectory = null, string? loadModel = "/default", string? audioDirectory = null)
        {
            MathNet.Numerics.Control.UseNativeMKL();

            this.GetModelPaths(customDirectory);
            this.GetAudioFiles(audioDirectory);

            if (!string.IsNullOrEmpty(loadModel))
            {
                this.LoadModel(loadModel);
            }
        }


        public string[] GetModelPaths(string? customDirectory = null)
        {
            // Set model path to first .onnx file found in repository\Ressources\ 
            string resourcesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ressources");

            if (customDirectory != null)
            {
                resourcesPath = customDirectory;
            }

            var modelFiles = Directory.GetFiles(resourcesPath, "*.onnx", SearchOption.AllDirectories);
            this.ModelPaths.AddRange(modelFiles);
            return this.ModelPaths.ToArray();
        }

        public bool LoadModel(string modelPath, int dmlDeviceId = 0)
        {
            if (modelPath.StartsWith("/default"))
            {
                modelPath = this.ModelPaths.Count > 0 ? this.ModelPaths[0] : string.Empty;
            }

            if (!File.Exists(modelPath))
            {
                StaticLogger.Log($"Model file not found: {modelPath}");
                return false;
            }

            try
            {
                var options = new SessionOptions();
                options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

                try
                {
                    // First if available use DirectML on dmlDeviceId
                    options.AppendExecutionProvider_DML(dmlDeviceId);
                    options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    this._session = new InferenceSession(modelPath, options);

                    StaticLogger.Log($"Using DirectML execution provider: [{dmlDeviceId}]");
                }
                catch (Exception ex)
                {
                    try
                    {
                        // First if available use DirectML on Desktop GPU
                        options.AppendExecutionProvider_DML((dmlDeviceId > 0 ? 0 : 1));
                        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                        this._session = new InferenceSession(modelPath, options);

                        StaticLogger.Log($"Using DirectML execution provider: [{(dmlDeviceId > 0 ? 0 : 1)}] GPU");
                    }
                    catch (Exception ex2)
                    {
                        StaticLogger.Log($"DirectML not available: {ex.Message}, {ex2.Message}");
                        StaticLogger.Log("Falling back to CPU execution provider.");
                        options.AppendExecutionProvider_CPU();
                        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                        this._session = new InferenceSession(modelPath, options);
                    }
                }

            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Error loading model: {ex.Message}");
                return false;
            }

            this.LoadedModelPath = Path.GetFullPath(modelPath);
            StaticLogger.Log($"Model loaded successfully: {modelPath}");
            return true;
        }



        public void DisposeSession()
        {
            if (this._session != null)
            {
                this._session.Dispose();
                this._session = null;
                this.LoadedModelPath = null;
                StaticLogger.Log("ONNX session disposed.");
            }
        }


        public void Dispose()
        {
            this.DisposeSession();

            GC.SuppressFinalize(this);
        }
    }
}
