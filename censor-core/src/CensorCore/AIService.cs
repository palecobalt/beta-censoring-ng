using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CensorCore
{
    public interface IAIService<TMatch>{
        bool Verbose { get; set; }
        SessionOptions? Options { get; }

        Task<ImageResult<TMatch>?> RunModel(byte[] data, MatchOptions? options = null);
        Task<ImageResult<TMatch>?> RunModel(string url, MatchOptions? options = null);
    }

    /// <summary>
    /// Main service type for interacting with the AI model. Responsible for setting up, configurating and executing the image classifier.
    /// </summary>
    /// <remarks>
    /// This service does not perform any censoring on the image.
    /// </remarks>
    public class AIService {
        public static readonly string[] ClassList = new[] { "EXPOSED_ANUS", "EXPOSED_ARMPITS", "COVERED_BELLY", "EXPOSED_BELLY", "COVERED_BUTTOCKS", "EXPOSED_BUTTOCKS", "FACE_F", "FACE_M", "COVERED_FEET", "EXPOSED_FEET", "COVERED_BREAST_F", "EXPOSED_BREAST_F", "COVERED_GENITALIA_F", "EXPOSED_GENITALIA_F", "EXPOSED_BREAST_M", "EXPOSED_GENITALIA_M" };

        /// <summary>
        /// NudeNet v3 classes in model output order, translated to the v2 names used by CensorCore and its clients.
        /// </summary>
        public static readonly string[] V3ClassList = new[] {
            "COVERED_GENITALIA_F", // FEMALE_GENITALIA_COVERED
            "FACE_F",              // FACE_FEMALE
            "EXPOSED_BUTTOCKS",    // BUTTOCKS_EXPOSED
            "EXPOSED_BREAST_F",    // FEMALE_BREAST_EXPOSED
            "EXPOSED_GENITALIA_F", // FEMALE_GENITALIA_EXPOSED
            "EXPOSED_BREAST_M",    // MALE_BREAST_EXPOSED
            "EXPOSED_ANUS",        // ANUS_EXPOSED
            "EXPOSED_FEET",        // FEET_EXPOSED
            "COVERED_BELLY",       // BELLY_COVERED
            "COVERED_FEET",        // FEET_COVERED
            "COVERED_ARMPITS",     // ARMPITS_COVERED (not in v2)
            "EXPOSED_ARMPITS",     // ARMPITS_EXPOSED
            "FACE_M",              // FACE_MALE
            "EXPOSED_BELLY",       // BELLY_EXPOSED
            "EXPOSED_GENITALIA_M", // MALE_GENITALIA_EXPOSED
            "COVERED_ANUS",        // ANUS_COVERED (not in v2)
            "COVERED_BREAST_F",    // FEMALE_BREAST_COVERED
            "COVERED_BUTTOCKS",    // BUTTOCKS_COVERED
        };

        // Candidate and NMS thresholds used by the NudeNet v3 reference implementation
        private const float V3CandidateScore = 0.25F;
        private const float V3NmsThreshold = 0.45F;

        private readonly InferenceSession _session;
        private readonly IImageHandler _imageHandler;
        // GPU runs go through a few dedicated threads, since CUDA keeps GPU memory for every thread that runs the model
        private DedicatedThreadRunner? _gpuRunner;
        public bool Verbose { get; set; } = false;
        public bool SkipGifs { get; set; } = false;
        public NudeNetModelVersion ModelVersion { get; private set; } = NudeNetModelVersion.V2;
        public int InputSize { get; private set; } = 320;
        /// <summary>
        /// The classes of a v3 or other YOLO model in output order, under the v2 names; null for a class that isn't censored.
        /// </summary>
        public IReadOnlyList<string?> Labels { get; private set; } = V3ClassList;
        // NudeNet's own models get the preprocessing of its reference implementation, other YOLO models Ultralytics'
        private bool _ultralyticsInput;

        private void Log(string message) {
            if (Verbose) {
                Console.WriteLine(message);
            }
        }

        public SessionOptions Options => new SessionOptions() {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        public static async Task<AIService> CreateFromFileAsync(string modelPath, IImageHandler imageHandler) {
            var opts = new SessionOptions() {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            return await Task.Run(() =>
            {
                var session = new InferenceSession(modelPath, opts);
                return new AIService(session, imageHandler);
            });
        }

        public static AIService Create(byte[] model, IImageHandler imageHandler, bool enableAcceleration = true, int gpuDeviceId = 0, long? gpuMemoryLimit = null, int maxConcurrentGpuRuns = 0) {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (isWindows && enableAcceleration) {
                InferenceSession? hwSession = null;
                var deviceId = 0;
                while (hwSession == null && deviceId < 2) {
                    try {
                        var hwOpts = new SessionOptions() {

                        };
                        hwOpts.AppendExecutionProvider_DML(deviceId);
                        hwSession = new InferenceSession(model, hwOpts);
                        return new AIService(hwSession, imageHandler);
                    }
                    catch {
                        deviceId++;
                    }
                }
                Console.WriteLine("WARN: Failed to initialize hardware acceleration!");
            }
            else if (enableAcceleration) {
                // CUDA needs the GPU build of ONNX Runtime; the CPU build throws here and we fall back to CPU
                try {
                    var cudaOptions = new OrtCUDAProviderOptions();
                    var providerOptions = new Dictionary<string, string> {
                        ["device_id"] = gpuDeviceId.ToString(),
                        // grow the memory arena only as needed instead of in powers of two
                        ["arena_extend_strategy"] = "kSameAsRequested",
                    };
                    if (gpuMemoryLimit is > 0) {
                        providerOptions["gpu_mem_limit"] = gpuMemoryLimit.Value.ToString();
                    }
                    cudaOptions.UpdateOptions(providerOptions);
                    var cudaSessionOptions = SessionOptions.MakeSessionOptionWithCudaProvider(cudaOptions);
                    cudaSessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    var cudaSession = new InferenceSession(model, cudaSessionOptions);
                    Console.WriteLine($"Using CUDA acceleration on GPU {gpuDeviceId}" + (maxConcurrentGpuRuns > 0 ? $" ({maxConcurrentGpuRuns} concurrent runs)" : string.Empty));
                    return new AIService(cudaSession, imageHandler) {
                        _gpuRunner = maxConcurrentGpuRuns > 0 ? new DedicatedThreadRunner(maxConcurrentGpuRuns, "gpu-inference") : null
                    };
                }
                catch (Exception e) {
                    Console.WriteLine($"WARN: Failed to initialize CUDA acceleration, using CPU: {e.Message}");
                }
            }
            var opts = new SessionOptions() {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            var session = new InferenceSession(model, opts);
            return new AIService(session, imageHandler);
        }
        public static AIService CreateFromFile(string modelPath, IImageHandler imageHandler) {
            return AIService.Create(File.ReadAllBytes(modelPath), imageHandler);
        }

        public AIService(byte[] modelContents, IImageHandler imageHandler) {
            this._session = new InferenceSession(modelContents, Options);
            this._imageHandler = imageHandler;
            DetectModelVersion();
        }

        public AIService(InferenceSession session, IImageHandler imageHandler) {
            this._session = session;
            this._imageHandler = imageHandler;
            DetectModelVersion();
        }

        private void DetectModelVersion() {
            // v3 (YOLOv8) models have a single [batch, 4 + classes, anchors] output, v2 has separate boxes/scores/labels
            var outputs = this._session.OutputMetadata.Values.ToList();
            if (outputs.Count == 1 && outputs[0].Dimensions.Length == 3) {
                var classes = outputs[0].Dimensions[1] - 4;
                this._session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var metadata);
                var names = ModelLabels.ParseNames(metadata);
                var labels = names?.Select(ModelLabels.Translate).ToArray();
                if (labels != null && labels.Length == classes && !labels.SequenceEqual(V3ClassList)) {
                    // another detector exported by Ultralytics (the hotscreen models): its classes are in its metadata
                    if (labels.All(l => l == null)) {
                        throw new NotSupportedException($"this model detects none of the classes the server censors (its classes: {string.Join(", ", names!)})");
                    }
                    ModelVersion = NudeNetModelVersion.V3;
                    InputSize = GetV3InputSize();
                    Labels = labels;
                    _ultralyticsInput = true;
                    var unused = names!.Where((_, i) => labels[i] == null).ToList();
                    Console.WriteLine($"Loaded YOLO model with {classes} classes ({InputSize}x{InputSize} input)"
                        + (unused.Count > 0 ? $"; not used: {string.Join(", ", unused)}" : string.Empty));
                    return;
                }
                if (classes == V3ClassList.Length) {
                    ModelVersion = NudeNetModelVersion.V3;
                    InputSize = GetV3InputSize();
                }
            }
            Console.WriteLine($"Loaded NudeNet {ModelVersion} model" + (ModelVersion == NudeNetModelVersion.V3 ? $" ({InputSize}x{InputSize} input)" : string.Empty));
        }

        private int GetV3InputSize() {
            var dims = this._session.InputMetadata.Values.First().Dimensions;
            if (dims.Length == 4 && dims[3] > 0) {
                return dims[3];
            }
            if (this._session.ModelMetadata.CustomMetadataMap.TryGetValue("imgsz", out var imgsz)
                && System.Text.RegularExpressions.Regex.Match(imgsz, @"\d+") is var match && match.Success) {
                return int.Parse(match.Value);
            }
            return 320;
        }

        private TensorLoadOptions<float> GetDefaultLoadOptions() {
            if (ModelVersion != NudeNetModelVersion.V3) {
                return new NudeNetLoadOptions();
            }
            return _ultralyticsInput ? new YoloLoadOptions(InputSize) : new NudeNetV3LoadOptions(InputSize);
        }

        public async Task<ImageResult?> RunModel<TTensor>(byte[] data, TensorLoadOptions<TTensor> loadOptions, MatchOptions? options = null) {
            var timer = new System.Diagnostics.Stopwatch();
            timer.Start();
            var imageData = await this._imageHandler.LoadImageData(data);
            if (SkipGifs && imageData.Format is SixLabors.ImageSharp.Formats.Gif.GifFormat) {
                return null;
            }
            timer.Stop();
            Log($"Loaded image data in {timer.Elapsed.TotalSeconds}s");
            var result = await RunModelForImage<TTensor>(imageData, options, loadOptions);
            if (result != null && result.Session != null) {
                result.Session.ImageLoadTime = timer.Elapsed;
            }
            return result;
        }

        public async Task<ImageResult?> RunModel(byte[] data, MatchOptions? options = null) {
            return await RunModel<float>(data, GetDefaultLoadOptions(), options);
        }

        public async Task<ImageResult?> RunModel(string url, MatchOptions? options = null) {
            return await RunModel<float>(url, GetDefaultLoadOptions(), options);
        }

        public async Task<ImageResult?> RunModel<TTensor>(string url, TensorLoadOptions<TTensor> loadOptions, MatchOptions? options = null) {
            var timer = new System.Diagnostics.Stopwatch();
            timer.Start();
            var imageData = await this._imageHandler.LoadImage(url);
            if (SkipGifs && imageData.Format is SixLabors.ImageSharp.Formats.Gif.GifFormat) {
                return null;
            }
            timer.Stop();
            Log($"Loaded image data in {timer.Elapsed.TotalSeconds}s");
            var result = await RunModelForImage<TTensor>(imageData, options, loadOptions);
            if (result != null && result.Session != null) {
                result.Session.ImageLoadTime = timer.Elapsed;
            }
            return result;
        }

        private async Task<ImageResult?> RunModelForImage<TTensor>(ImageData imageData, MatchOptions? options, TensorLoadOptions<TTensor> loadOptions) {
            options ??= MatchOptions.GetDefault(ModelVersion);
            var timer = new System.Diagnostics.Stopwatch();
            timer.Restart();
            // var modelInput = await this._imageHandler.LoadToTensor(imageData);
            var modelInput = await this._imageHandler.LoadToTensor<TTensor>(imageData, loadOptions);
            timer.Stop();
            var tensorLoadTime = timer.Elapsed;
            Log($"Loaded tensor data in {timer.Elapsed.TotalSeconds}s");
            timer.Restart();
            var feeds = GetFeeds(modelInput).ToList();
            var output = _gpuRunner != null
                ? await _gpuRunner.Run(() => this._session.Run(feeds))
                : this._session.Run(feeds);
            var runTime = timer.Elapsed;
            Log($"Finished model in {timer.Elapsed.TotalSeconds}s");
            timer.Restart();
            var classifications = this.GetResults(imageData, output.ToList(), options).ToList();
            var modelName = string.IsNullOrWhiteSpace(this._session.ModelMetadata.Description)
                ? this._session.ModelMetadata.GraphName
                : this._session.ModelMetadata.Description;
            var sessionMeta = new SessionMetadata(modelName, runTime) {
                TensorLoadTime = tensorLoadTime
            };
            var result = new ImageResult(imageData, classifications) {
                Session = sessionMeta
            };
            Log($"Built results in {timer.Elapsed.TotalSeconds}s");
            timer.Reset();
            return result;
        }

        private IEnumerable<NamedOnnxValue> GetFeeds<TTensor>(InputImage<TTensor> input) {
            return this._session.InputMetadata.Select(im => NamedOnnxValue.CreateFromTensor<TTensor>(im.Key, input.Tensor));
        }

        private IEnumerable<Classification> GetResults(ImageData imgData, List<DisposableNamedOnnxValue> tensorOutput, MatchOptions matchOptions) {
            if (ModelVersion == NudeNetModelVersion.V3) {
                return GetV3Results(imgData, tensorOutput, matchOptions);
            }
            // var boxOutput = tensorOutput.First(to => to.ElementType == TensorElementType.Float && to.);
            // var scoreOutput = tensorOutput.First(to => to.Name == "output2");
            var labelOutput = tensorOutput.First(to => to.ElementType == TensorElementType.Int32); //output3

            var floatTensors = tensorOutput.Where(to => to.ElementType == TensorElementType.Float).Select(to => to.AsTensor<float>()).ToList();
            var rawScores = floatTensors.First(ft => ft.Length < 1000);
            var rawBoxes = floatTensors.First(ft => ft.Length > 1000);
            var rawLabels = labelOutput.AsTensor<int>();

            var length = rawLabels.Length;
            if (rawLabels.Last() == -1) {
                var validLabels = rawLabels.TakeWhile(l => l != -1).ToList();
                length = validLabels.Count;
            }

            var results = new List<Classification>();
            var boxes = rawBoxes.Chunk(4).ToList();

            for (int i = 0; i < length; i++) {
                var confidence = rawScores.ElementAtOrDefault(i);
                string? className = null;
                try {
                    var classNameIndex = rawLabels.ElementAt(i);
                    className = ClassList[classNameIndex];
                } catch {
                    //ignored since it's clearly fucked
                }
                if (confidence > 0 && !string.IsNullOrWhiteSpace(className) && boxes.Count > i) {
                    if (confidence >= matchOptions.GetScoreForClass(className)) {
                        var box = boxes.ElementAt(i).ToBox(imgData.ScaleFactor, imgData.SampleOffset);
                        results.Add(new Classification(box, confidence, className));
                        // yield return new Classification(box, confidence, className);
                    }
                }
            }
            return results;
        }

        private IEnumerable<Classification> GetV3Results(ImageData imgData, List<DisposableNamedOnnxValue> tensorOutput, MatchOptions matchOptions) {
            var output = tensorOutput.First().AsTensor<float>();
            var channels = output.Dimensions[1];
            var anchors = output.Dimensions[2];
            var values = output is DenseTensor<float> dense ? dense.Buffer.ToArray() : output.ToArray();
            var img = imgData.SampledImage ?? imgData.SourceImage;
            // the model input is the sampled image scaled so its longest side fits the input size
            var input = LetterboxGeometry.For(img.Width, img.Height, InputSize, _ultralyticsInput);
            // image pixels per model pixel; as a division, since 1 / input.Scale rounds differently and moved boxes
            var scale = (float)Math.Max(img.Width, img.Height) / InputSize;

            var candidates = new List<(float[] Box, float Score, int ClassIndex)>();
            for (int i = 0; i < anchors; i++) {
                var classIndex = 0;
                var score = float.MinValue;
                for (int c = 4; c < channels; c++) {
                    var classScore = values[c * anchors + i];
                    if (classScore > score) {
                        score = classScore;
                        classIndex = c - 4;
                    }
                }
                if (score < V3CandidateScore || Labels[classIndex] == null) {
                    continue;
                }
                var width = values[2 * anchors + i] * scale;
                var height = values[3 * anchors + i] * scale;
                var x1 = Math.Clamp((values[i] - input.Left) * scale - width / 2, 0, img.Width);
                var y1 = Math.Clamp((values[anchors + i] - input.Top) * scale - height / 2, 0, img.Height);
                var x2 = x1 + Math.Min(width, img.Width - x1);
                var y2 = y1 + Math.Min(height, img.Height - y1);
                candidates.Add((new[] { x1, y1, x2, y2 }, score, classIndex));
            }

            // class-agnostic NMS, as in the reference implementation
            var kept = new List<float[]>();
            var results = new List<Classification>();
            foreach (var candidate in candidates.OrderByDescending(c => c.Score)) {
                if (kept.Any(k => IntersectionOverUnion(k, candidate.Box) > V3NmsThreshold)) {
                    continue;
                }
                kept.Add(candidate.Box);
                var className = Labels[candidate.ClassIndex]!;
                if (candidate.Score >= matchOptions.GetScoreForClass(className)) {
                    var box = candidate.Box.ToBox(imgData.ScaleFactor, imgData.SampleOffset);
                    results.Add(new Classification(box, candidate.Score, className));
                }
            }
            return results;
        }

        private static float IntersectionOverUnion(float[] a, float[] b) {
            var intersectWidth = Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]);
            var intersectHeight = Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]);
            if (intersectWidth <= 0 || intersectHeight <= 0) {
                return 0;
            }
            var intersection = intersectWidth * intersectHeight;
            var union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - intersection;
            return union <= 0 ? 0 : intersection / union;
        }
    }

    public enum NudeNetModelVersion {
        V2,
        V3
    }
}
