namespace CensorCore.Censoring
{
    public class GlobalCensorOptions
    {
        public bool? AllowTransformers { get;set; } = true;
        public float? RelativeCensorScale { get; set; } = 1F;
        public float? PaddingScale { get; set; } = 1F;
        public bool? ForcePixelBackground { get; set; } = false;

        /// <summary>
        /// Merges overlapping matches of any class into one box when they use the same censor type.
        /// </summary>
        public bool? MergeOverlapping { get; set; } = false;

        /// <summary>
        /// When merging, also merges matches this close together, as a fraction of each box's size (0.1 = 10%).
        /// </summary>
        public float? MergeDistance { get; set; } = 0F;

        /// <summary>
        /// Censors every frame of animated GIFs instead of returning only the first frame.
        /// </summary>
        public bool? CensorAnimatedGifs { get; set; } = true;

        /// <summary>
        /// How often, in animation time, to run the model on a GIF frame; frames in between reuse nearby matches.
        /// </summary>
        public int? AnimationDetectionIntervalMs { get; set; } = 200;

        /// <summary>
        /// Animated GIFs with more frames than this are censored as a still image (first frame only).
        /// </summary>
        public int? AnimationMaxFrames { get; set; } = 500;

        public Dictionary<string, float> ClassStrength {get;set;} = new Dictionary<string, float>();
        /// <summary>
        /// This is a kludge solution to a real problem. As such, while it is present (and in use) in
        /// current versions of CensorCore, you should not rely on it remaining part of the long-term 
        /// and/or stable API in future. Censoring providers should use this to increase the layers 
        /// of effects applied to certain match classes;
        /// </summary>
        /// <typeparam name="string">The match class to apply to.</typeparam>
        /// <typeparam name="int">A positive or negative value that will get added to the provider's default layer value.</typeparam>
        /// <returns></returns>
        public Dictionary<string, int> LayerModifier {get;set;} = new Dictionary<string, int>();
    }
}