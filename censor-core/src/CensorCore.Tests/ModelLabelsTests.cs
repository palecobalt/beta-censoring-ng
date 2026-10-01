using Xunit;

namespace CensorCore.Tests;

public class ModelLabelsTests {
    private const string NudeNetNames = "{0: 'FEMALE_GENITALIA_COVERED', 1: 'FACE_FEMALE', 2: 'BUTTOCKS_EXPOSED', 3: 'FEMALE_BREAST_EXPOSED', 4: 'FEMALE_GENITALIA_EXPOSED', 5: 'MALE_BREAST_EXPOSED', 6: 'ANUS_EXPOSED', 7: 'FEET_EXPOSED', 8: 'BELLY_COVERED', 9: 'FEET_COVERED', 10: 'ARMPITS_COVERED', 11: 'ARMPITS_EXPOSED', 12: 'FACE_MALE', 13: 'BELLY_EXPOSED', 14: 'MALE_GENITALIA_EXPOSED', 15: 'ANUS_COVERED', 16: 'FEMALE_BREAST_COVERED', 17: 'BUTTOCKS_COVERED'}";
    private const string HotscreenNames = "{0: 'FEMALE_FACE', 1: 'MALE_FACE', 2: 'FEMALE_GENITALIA_COVERED', 3: 'FEMALE_GENITALIA_EXPOSED', 4: 'BUTTOCKS_COVERED', 5: 'BUTTOCKS_EXPOSED', 6: 'FEMALE_BREAST_COVERED', 7: 'FEMALE_BREAST_EXPOSED', 8: 'MALE_BREAST_EXPOSED', 9: 'ARMPITS_EXPOSED', 10: 'BELLY_EXPOSED', 11: 'MALE_GENITALIA_EXPOSED', 12: 'ANUS_EXPOSED', 13: 'FEET_COVERED', 14: 'FEET_EXPOSED', 15: 'EYE'}";

    [Fact]
    public void NudeNetsMetadataGivesTheBuiltInClassList() {
        var labels = ModelLabels.ParseNames(NudeNetNames)!.Select(ModelLabels.Translate);

        Assert.Equal(AIService.V3ClassList, labels);
    }

    [Fact]
    public void HotscreenClassesGetTheSameNamesAndEyeIsNotUsed() {
        var labels = ModelLabels.ParseNames(HotscreenNames)!.Select(ModelLabels.Translate).ToArray();

        Assert.Equal(16, labels.Length);
        Assert.Equal("FACE_F", labels[0]);
        Assert.Equal("FACE_M", labels[1]);
        Assert.Equal("COVERED_GENITALIA_F", labels[2]);
        Assert.Equal("EXPOSED_BREAST_M", labels[8]);
        Assert.Equal("EXPOSED_FEET", labels[14]);
        Assert.Null(labels[15]);
        Assert.All(labels.Take(15), label => Assert.Contains(label, AIService.V3ClassList));
    }

    [Theory]
    [InlineData("EXPOSED_BREAST_F", "EXPOSED_BREAST_F")]
    [InlineData("face_female", "FACE_F")]
    [InlineData("person", null)]
    [InlineData("EXPOSED", null)]
    [InlineData("_COVERED", null)]
    public void TranslatesOtherSpellings(string name, string? expected) {
        Assert.Equal(expected, ModelLabels.Translate(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no names here")]
    [InlineData("{1: 'A', 2: 'B'}")]
    [InlineData("{0: 'A', 2: 'B'}")]
    public void NamesThatCannotBeReadGiveNothing(string? metadata) {
        Assert.Null(ModelLabels.ParseNames(metadata));
    }

    [Fact]
    public void ReadsNamesInClassOrderWithEitherQuote() {
        Assert.Equal(new[] { "A", "B B", "C" }, ModelLabels.ParseNames("{1: \"B B\", 0: 'A', 2: 'C'}"));
    }

    [Fact]
    public void NudeNetInputIsAnchoredTopLeft() {
        var box = LetterboxGeometry.For(1280, 720, 640, centred: false);

        Assert.Equal((640, 360, 0, 0), (box.Width, box.Height, box.Left, box.Top));
        Assert.Equal(0.5F, box.Scale);
    }

    [Theory]
    // the borders Ultralytics' letterbox gives: round(d - 0.1) before, the rest after
    [InlineData(1280, 720, 640, 360, 0, 140)]
    [InlineData(720, 1280, 360, 640, 140, 0)]
    [InlineData(1000, 667, 640, 427, 0, 106)]
    [InlineData(500, 500, 640, 640, 0, 0)]
    [InlineData(4000, 3, 640, 1, 0, 319)]
    public void UltralyticsInputIsCentred(int width, int height, int scaledWidth, int scaledHeight, int left, int top) {
        var box = LetterboxGeometry.For(width, height, 640, centred: true);

        Assert.Equal((scaledWidth, scaledHeight, left, top), (box.Width, box.Height, box.Left, box.Top));
    }

    [Fact]
    public void TheCentredImageIsSurroundedByGrey() {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(200, 100, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 0, 0));
        using var prepared = new YoloLoadOptions(640).PrepareImage(image);

        Assert.Equal((640, 640), (prepared.Width, prepared.Height));
        Assert.Equal(new SixLabors.ImageSharp.PixelFormats.Rgba32(114, 114, 114), prepared[320, 159]);
        Assert.Equal(new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 0, 0), prepared[0, 160]);
        Assert.Equal(new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 0, 0), prepared[639, 479]);
        Assert.Equal(new SixLabors.ImageSharp.PixelFormats.Rgba32(114, 114, 114), prepared[320, 480]);
    }
}
