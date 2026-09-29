using CensorCore.ModelLoader;
using Xunit;

namespace CensorCore.Tests;

public class ModelDownloadTests {
    [Fact]
    public void AWrongChecksumIsAnError() {
        Assert.Throws<ModelChecksumException>(() => RepositoryDownloadClient.VerifyDefaultModel(new byte[] { 1, 2, 3 }));
    }
}
