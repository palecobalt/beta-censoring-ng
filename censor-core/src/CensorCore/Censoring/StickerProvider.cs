using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;



namespace CensorCore.Censoring
{
    public static class KnownAssetTypes {
        public static string Stickers => "stickers";
    }
    public class StickerProvider : ICensorTypeProvider
    {
        private readonly IAssetStore _store;
        private readonly GlobalCensorOptions _globalOpts = new GlobalCensorOptions();

        public StickerProvider(IAssetStore store)
        {
            this._store = store;
        }
        public StickerProvider(IAssetStore store, GlobalCensorOptions options) : this(store)
        {
            this._globalOpts = options;
        }
        public async Task<Action<IImageProcessingContext>?> CensorImage(Image<Rgba32> inputImage, Classification result, string method, int level)
        {
            var mutations = new List<Action<IImageProcessingContext>>();
            var padding = inputImage.GetPadding(_globalOpts);
            float boxRatio = (float)result.Box.Width / result.Box.Height;
            var options = method.GetOptions("sticker");
            var (useBlur, usePixels) = UseBlur(options);
            var sticker = await AnimationChoices.Choose($"sticker|{AnimationChoices.GetArea(result)}|{string.Join(';', options.Categories ?? new List<string>())}",
                () => GetImageAsync(boxRatio, options.Categories));
            if (useBlur) {
                var blurMutation = CensorEffects.GetMaskedBlurEffect(inputImage, result, padding, level, minimumLevel: 10);
                mutations.Add(blurMutation);
            } else if (usePixels) {
                var pixelMutation = CensorEffects.GetMaskedPixelEffect(inputImage, result, padding, level);
                mutations.Add(pixelMutation);
            }
            var effectCenter = result.Box.GetCenter();
            if (sticker != null) {
                // the store picked a sticker with about the box's aspect ratio; Max fits it inside the box
                var stickerImage = Image.Load(sticker);
                var resizeOpts = new ResizeOptions {
                    Size = new Size(result.Box.Width, result.Box.Height),
                    Mode = ResizeMode.Max
                };
                stickerImage.Mutate(s => {
                    s.Resize(resizeOpts);
                });
                if (result.SourceAngle.HasValue) {
                    var ctr = result.Box.GetCenter();
                    var affineBuilder = new AffineTransformBuilder();
                    affineBuilder.PrependTranslation(new Vector2(ctr.X, ctr.Y));
                    affineBuilder.PrependRotationDegrees(result.SourceAngle.Value);
                    affineBuilder.AppendTranslation(new Vector2(-ctr.X, -ctr.Y));
                    stickerImage.Mutate(m => m.Transform(affineBuilder));
                }
                var targetLoc = new Point(effectCenter.X - (stickerImage.Width/2), effectCenter.Y - (stickerImage.Height/2));
                mutations.Add(x => {
                    x.DrawImage(stickerImage, targetLoc, Math.Min(level/10F,1));
                });
            }
            return x => {
                foreach (var mutation in mutations)
                {
                    mutation(x);
                }
            };
        }

        private async Task<byte[]?> GetImageAsync(float? boxRatio, List<string>? categories) {
            try {
                var sticker = await this._store.GetRandomImage(KnownAssetTypes.Stickers, boxRatio, categories);
                return sticker?.RawData;
            } catch (NotImplementedException) {
                //ignored
                //not all providers will implement this, and that's okay
            }
            try {
                var allFiles = await this._store.GetImages(KnownAssetTypes.Stickers, categories);
                var candidates = allFiles.Where(f => boxRatio == null || (GetRatio(f.RawData) is { } ratio && CloseEnough(ratio, boxRatio.Value))).ToList();
                return candidates.Any() ? candidates.Random().RawData : null;
            } catch {
                return null;
            }

        }

        /// <summary>
        /// What goes under the sticker: the configured background (blur unless set otherwise), or what the request's
        /// useBlur / usePixels parameters say.
        /// </summary>
        public (bool UseBlur, bool UsePixels) UseBlur((List<string>? Categories, Flurl.QueryParamCollection? Parameters) options) {
            var background = (_globalOpts.StickerBackground ?? (_globalOpts.ForcePixelBackground == true ? "pixels" : "blur")).Trim().ToLowerInvariant();
            bool? requested(string name) =>
                options.Parameters != null && options.Parameters.TryGetFirst(name, out var value) && bool.TryParse((string)value, out var flag) ? flag : null;
            var useBlur = requested("useBlur") ?? (background == "blur");
            var usePixels = requested("usePixels") ?? (background == "pixels");
            return (useBlur, usePixels);
        }

        private static float? GetRatio(byte[] image) {
            try {
                var info = Image.Identify(image);
                return (float)info.Width / info.Height;
            } catch {
                return null;
            }
        }

        private bool CloseEnough(float stickerRatio, float targetRatio)
        {
            var diff = stickerRatio / targetRatio;
            return 0.75 <= diff && diff <= 1.25;
        }

        public bool Supports(string censorType) => censorType.StartsWith("sticker");
        public int Layer => 6;
    }
}
