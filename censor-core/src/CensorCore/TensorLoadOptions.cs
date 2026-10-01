using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CensorCore {
    public delegate void ActionRef<T>(Tensor<T> data, Point point, ref Rgba32 pixel);
    // public delegate void ActionRef<Tensor<T>, Rgba32>(T data, ref Rgba32 item);
    public abstract class TensorLoadOptions<T> {
        public Func<Image, int[]> Dimensions { get; }

        public TensorLoadOptions(Func<Image, int[]> dimensions) {
            Dimensions = dimensions;
        }

        public virtual ActionRef<T>? LoadPixel {get;protected set;}

        /// <summary>
        /// Transforms the image before it is loaded into the tensor (e.g. resizing to a fixed model input size).
        /// Returning a different instance than <paramref name="image"/> hands ownership of it to the caller.
        /// </summary>
        public virtual Image<Rgba32> PrepareImage(Image<Rgba32> image) => image;

        public virtual IEnumerable<NamedOnnxValue> GetFeeds(InferenceSession session, InputImage<float> input) {
            return session.InputMetadata.Select(im => NamedOnnxValue.CreateFromTensor<float>(im.Key, input.Tensor));
        }
    }

    public class NudeNetLoadOptions : TensorLoadOptions<float> {
        public NudeNetLoadOptions() : base(img => new[] {1, img.Height, img.Width, 3}) {
            LoadPixel = LoadNudeNetPixels;
        }

        private void LoadNudeNetPixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, y, x, 0] = pixel.B - 103.939F;
            data[0, y, x, 1] = pixel.G - 116.779F;
            data[0, y, x, 2] = pixel.R - 123.68F;
        }
    }

    /// <summary>
    /// Load options for the YOLOv8-based NudeNet v3 models (320n/640m).
    /// </summary>
    /// <remarks>
    /// Mirrors the reference implementation: the image is scaled so its longest side matches the model input,
    /// anchored top-left with the remainder left black (zero), and loaded as NCHW in the 0-1 range.
    /// </remarks>
    public class NudeNetV3LoadOptions : TensorLoadOptions<float> {
        public int InputSize { get; }

        public NudeNetV3LoadOptions(int inputSize = 320) : base(img => new[] {1, 3, inputSize, inputSize}) {
            InputSize = inputSize;
            LoadPixel = LoadV3Pixels;
        }

        public override Image<Rgba32> PrepareImage(Image<Rgba32> image) {
            var box = LetterboxGeometry.For(image.Width, image.Height, InputSize, centred: false);
            return image.Clone(ctx => ctx.Resize(box.Width, box.Height, KnownResamplers.Triangle));
        }

        private void LoadV3Pixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, 0, y, x] = pixel.B / 255F;
            data[0, 1, y, x] = pixel.G / 255F;
            data[0, 2, y, x] = pixel.R / 255F;
        }
    }

    /// <summary>
    /// Load options for other YOLO detectors exported by Ultralytics (the hotscreen models).
    /// </summary>
    /// <remarks>
    /// Ultralytics' own preprocessing: the image is scaled so its longest side matches the model input, centred
    /// on a grey square, and loaded as NCHW RGB in the 0-1 range.
    /// </remarks>
    public class YoloLoadOptions : TensorLoadOptions<float> {
        private static readonly Rgba32 Padding = new Rgba32(114, 114, 114);

        public int InputSize { get; }

        public YoloLoadOptions(int inputSize = 640) : base(img => new[] {1, 3, inputSize, inputSize}) {
            InputSize = inputSize;
            LoadPixel = LoadYoloPixels;
        }

        public override Image<Rgba32> PrepareImage(Image<Rgba32> image) {
            var box = LetterboxGeometry.For(image.Width, image.Height, InputSize, centred: true);
            using var resized = image.Clone(ctx => ctx.Resize(box.Width, box.Height, KnownResamplers.Triangle));
            var canvas = new Image<Rgba32>(InputSize, InputSize, Padding);
            resized.ProcessPixelRows(canvas, (source, target) => {
                for (int y = 0; y < source.Height; y++) {
                    source.GetRowSpan(y).CopyTo(target.GetRowSpan(y + box.Top).Slice(box.Left));
                }
            });
            return canvas;
        }

        private void LoadYoloPixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, 0, y, x] = pixel.R / 255F;
            data[0, 1, y, x] = pixel.G / 255F;
            data[0, 2, y, x] = pixel.B / 255F;
        }
    }

    public class BodyPixLoadOptions : TensorLoadOptions<float> {
        public BodyPixLoadOptions() : base(img => new[] { 1,640, 360, 3}) {
            LoadPixel = LoadBodyPixPixels;
        }

        private void LoadBodyPixPixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, y, x, 0] = pixel.B - 123.15F;
            data[0, y, x, 1] = pixel.G - 115.90F;
            data[0, y, x, 2] = pixel.R - 103.06F;
        }
    }

    public class RobustVideoOptions : TensorLoadOptions<float> {
        public RobustVideoOptions() : base(img => new[] { 1, 3, img.Height, img.Width})
        {
            LoadPixel = LoadRobustVideoPixels;
        }

        private void LoadRobustVideoPixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, 0, y, x] = pixel.B / 255F;
            data[0, 1, y, x] = pixel.G / 255F;
            data[0, 2, y, x] = pixel.R / 255F;
        }

        public override IEnumerable<NamedOnnxValue> GetFeeds(InferenceSession session, InputImage<float> input) {
            var ratio = new DenseTensor<float>(1);
            var img = input.Image.SampledImage ?? input.Image.SourceImage;
            ratio[0] = Math.Max(0.25F, 480F/(Math.Max(img.Width, img.Height)));
            var dict = new Dictionary<string, Tensor<float>>() {
                ["src"] = input.Tensor,
                ["r1i"] = new DenseTensor<float>(new[] {1,1,1,1}),
                ["r2i"] = new DenseTensor<float>(new[] {1,1,1,1}),
                ["r3i"] = new DenseTensor<float>(new[] {1,1,1,1}),
                ["r4i"] = new DenseTensor<float>(new[] {1,1,1,1}),
                ["downsample_ratio"] = ratio
            };
            return session.InputMetadata.Select(im => NamedOnnxValue.CreateFromTensor(im.Key, dict[im.Key]));
        }
    }

    public class FaceDetectionLoadOptions : TensorLoadOptions<float> {
        public FaceDetectionLoadOptions() : base(img => new[] {1, 3, img.Height, img.Width}) {
            LoadPixel = LoadLandmarkPixels;
        }

        private void LoadLandmarkPixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, 0, y, x] = (pixel.B - 127F)/128;
            data[0, 1, y, x] = (pixel.G - 127F)/128;
            data[0, 2, y, x] = (pixel.R - 127F)/128;
        }
    }

    public class LandmarksLoadOptions : TensorLoadOptions<float> {
        public LandmarksLoadOptions() : base(img => new[] {1, 3, 112, 112}) {
            LoadPixel = LoadLandmarkPixels;
        }

        private void LoadLandmarkPixels(Tensor<float> data, Point point, ref Rgba32 pixel) {
            var y = point.Y;
            var x = point.X;
            data[0, 0, y, x] = pixel.B / 255F;
            data[0, 1, y, x] = pixel.G / 255F;
            data[0, 2, y, x] = pixel.R / 255F;
        }
    }
}
