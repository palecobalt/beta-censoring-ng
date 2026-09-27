namespace CensorCore {
    /// <summary>
    /// The options used to control what classification results are included in the final output.
    /// </summary>
    public class MatchOptions
    {
        /// <summary>
        /// The default score required for a classification to be considered a valid match.
        /// </summary>
        /// <value>The minimum required confidence/score.</value>
        public float MinimumScore {get;set;} = 0.6F;

        /// <summary>
        /// Class-specific scores for classification results. Class-specific scores are preferred when evaluating matches, 
        /// falling back to the minimum score if no class-specific one is provided.
        /// </summary>
        /// <typeparam name="string">The class name as returned from the model.</typeparam>
        /// <typeparam name="float">The minimum required confidence/score.</typeparam>
        /// <returns></returns>
        public Dictionary<string, float> ClassScores {get;set;} = new Dictionary<string, float>();

        /// <summary>
        /// Returns a set of sane defaults for matching results.
        /// </summary>
        /// <returns>Default match options.</returns>
        public static MatchOptions GetDefault() {
            return GetDefault(NudeNetModelVersion.V2);
        }

        /// <summary>
        /// Returns a set of sane defaults for matching results from the given model version.
        /// </summary>
        /// <returns>Default match options.</returns>
        public static MatchOptions GetDefault(NudeNetModelVersion modelVersion) {
            if (modelVersion == NudeNetModelVersion.V3) {
                // v3 (YOLOv8) scores run noticeably lower than v2's; the reference implementation accepts anything over 0.25
                var v3Classes = AIService.V3ClassList.Where(cn => cn.Split('_').Length > 2).ToDictionary(c => c, v => v.Contains("COVERED") ? 0.45F : 0.3F);
                return new MatchOptions() {
                    MinimumScore = 0.35F,
                    ClassScores = v3Classes
                };
            }
            var complexClasses = AIService.ClassList.Where(cn => cn.Split('_').Length > 2).ToDictionary(c => c, v => v.Contains("COVERED") ? 0.6F : 0.4F);
            return new MatchOptions() {
                MinimumScore = 0.55F,
                ClassScores = complexClasses
            };
        }
    }
}