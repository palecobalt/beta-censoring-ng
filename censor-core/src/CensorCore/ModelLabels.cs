using System.Text.RegularExpressions;

namespace CensorCore
{
    /// <summary>
    /// Class names of YOLO detection models: read from the "names" entry Ultralytics writes into the ONNX metadata
    /// and translated to the names CensorCore and its clients use.
    /// </summary>
    public static class ModelLabels {
        /// <summary>
        /// Reads the names from metadata like <c>{0: 'FEMALE_FACE', 1: 'MALE_FACE'}</c>, in class order.
        /// Null when there are none or the numbering has gaps.
        /// </summary>
        public static string[]? ParseNames(string? metadata) {
            if (string.IsNullOrWhiteSpace(metadata)) {
                return null;
            }
            var names = new SortedDictionary<int, string>();
            foreach (Match match in Regex.Matches(metadata, @"(\d+)\s*:\s*(['""])(.*?)\2")) {
                names[int.Parse(match.Groups[1].Value)] = match.Groups[3].Value;
            }
            if (names.Count == 0 || names.Keys.First() != 0 || names.Keys.Last() != names.Count - 1) {
                return null;
            }
            return names.Values.ToArray();
        }

        /// <summary>
        /// Translates a model's class name to the v2 name (<c>FEMALE_BREAST_EXPOSED</c> and
        /// <c>EXPOSED_BREAST_F</c> both give <c>EXPOSED_BREAST_F</c>). Null for classes that aren't censored,
        /// such as hotscreen's <c>EYE</c>.
        /// </summary>
        public static string? Translate(string name) {
            name = name.Trim().ToUpperInvariant();
            switch (name) {
                case "FACE_FEMALE" or "FEMALE_FACE" or "FACE_F":
                    return "FACE_F";
                case "FACE_MALE" or "MALE_FACE" or "FACE_M":
                    return "FACE_M";
            }
            foreach (var state in new[] { "EXPOSED", "COVERED" }) {
                if (name.StartsWith(state + "_") && name.Length > state.Length + 1) {
                    return name;
                }
                if (name.EndsWith("_" + state) && name.Length > state.Length + 1) {
                    var part = name[..^(state.Length + 1)];
                    if (part.StartsWith("FEMALE_")) {
                        part = part["FEMALE_".Length..] + "_F";
                    } else if (part.StartsWith("MALE_")) {
                        part = part["MALE_".Length..] + "_M";
                    }
                    return $"{state}_{part}";
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Where an image lands in a square model input: scaled so its longest side fills the input, and placed in the
    /// top-left corner (NudeNet's reference implementation) or centred (Ultralytics).
    /// </summary>
    public readonly record struct LetterboxGeometry(int Width, int Height, int Left, int Top, float Scale) {
        public static LetterboxGeometry For(int imageWidth, int imageHeight, int inputSize, bool centred) {
            var scale = (float)inputSize / Math.Max(imageWidth, imageHeight);
            var width = Math.Clamp((int)Math.Round(imageWidth * scale), 1, inputSize);
            var height = Math.Clamp((int)Math.Round(imageHeight * scale), 1, inputSize);
            // the same rounding of the border as Ultralytics' letterbox
            var left = centred ? (int)Math.Round((inputSize - width) / 2F - 0.1F) : 0;
            var top = centred ? (int)Math.Round((inputSize - height) / 2F - 0.1F) : 0;
            return new LetterboxGeometry(width, height, left, top, scale);
        }
    }
}
