using BetaCensor.Core;
using BetaCensor.Web.Providers;
using Microsoft.Extensions.FileProviders;

namespace BetaCensor.Web.Components;
public class ArchiveProviderComponent : IStoreComponent {
    public string AssetType => "stickers";

    public IFileProvider? GetFileProvider(string contentRoot) {
        var localPackages = Directory.GetFiles(contentRoot, "*.betapkg").Where(f => File.Exists(f));
        var localZips = Directory.GetFiles(contentRoot, "*-stickers.zip").Where(f => File.Exists(f));
        localPackages = localPackages.Concat(localZips);
        var defaultStorePath = Path.Join(contentRoot, "stickers");
        if (Directory.Exists(defaultStorePath)) {
            if (!Path.IsPathFullyQualified(defaultStorePath)) {
                defaultStorePath = Path.GetFullPath(defaultStorePath);
            }
            var storePackages = Directory.GetFiles(defaultStorePath, "*.betapkg").Where(File.Exists);
            localPackages = localPackages.Concat(storePackages);
        }
        if (localPackages.Any()) {
            var providers = localPackages.Select(lp => new ZipArchiveFileProvider(lp)).ToArray();
            return new NestedFilesProvider(new ConcatFileProvider(providers), (s, dir) => string.Join('/', new[] {s, dir.Name}));
        }
        return null;
    }

    public bool TryProvidePath(string path, bool allowNesting, out IFileProvider? provider) {
        if (PathMatches(path) && File.Exists(path)) {
            var zipProvider = new ZipArchiveFileProvider(path);
            provider = allowNesting ? new NestedFilesProvider(zipProvider) : zipProvider;
            return true;
        }
        provider = null;
        return false;
    }

    private bool PathMatches(string path) {
        var types = new List<string> { ".zip", ".betapkg" };
        var a = Path.GetExtension(path);
        return types.Any(t => t.Equals(a, StringComparison.CurrentCultureIgnoreCase));
    }
}
