using SixLabors.ImageSharp;

namespace CensorCore.Censoring;

/// <summary>
/// Keeps one label per face. The model sometimes reports the same face as both FACE_F and FACE_M; each label then
/// got its own censoring, such as a blur for the male face and eye bars for the female one. The more confident
/// label wins.
/// </summary>
public class FaceLabelResolver : IResultsTransformer {
    private static readonly string[] FaceLabels = { "FACE_F", "FACE_M" };
    private const float MinimumOverlap = 0.5F;

    public IEnumerable<Classification> TransformResults(IEnumerable<Classification> results, IResultParser? parser) {
        var all = results.ToList();
        var faces = all.Where(r => FaceLabels.Contains(r.Label)).OrderByDescending(r => r.Confidence).ToList();
        var dropped = new HashSet<Classification>();
        foreach (var face in faces) {
            if (dropped.Contains(face)) {
                continue;
            }
            foreach (var other in faces.Where(o => o != face && o.Label != face.Label && !dropped.Contains(o))) {
                if (Overlap(face.Box, other.Box) >= MinimumOverlap) {
                    dropped.Add(other);
                }
            }
        }
        return all.Where(r => !dropped.Contains(r));
    }

    // intersection over union
    public static float Overlap(BoundingBox a, BoundingBox b) {
        var intersection = Rectangle.Intersect(a.ToRectangle(), b.ToRectangle());
        if (intersection.IsEmpty) {
            return 0;
        }
        float shared = intersection.Width * intersection.Height;
        return shared / (a.Width * a.Height + b.Width * b.Height - shared);
    }
}
