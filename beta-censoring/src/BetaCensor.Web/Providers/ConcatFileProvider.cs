using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace BetaCensor.Web.Providers;

/// <summary>
/// Lists the files of several providers together. Unlike CompositeFileProvider, which keeps only the first file of
/// each name, it keeps them all (sticker packs often reuse names such as 1.png); folders are listed once.
/// </summary>
public class ConcatFileProvider : IFileProvider {
    private readonly IFileProvider[] _providers;

    public ConcatFileProvider(IEnumerable<IFileProvider> providers) {
        _providers = providers.ToArray();
    }

    public IDirectoryContents GetDirectoryContents(string subpath) {
        var contents = _providers.Select(p => p.GetDirectoryContents(subpath)).Where(c => c.Exists).SelectMany(c => c).ToList();
        if (!contents.Any()) {
            return NotFoundDirectoryContents.Singleton;
        }
        var folders = contents.Where(f => f.IsDirectory).DistinctBy(f => f.Name);
        return new VirtualDirectory(contents.Where(f => !f.IsDirectory).Concat(folders).ToList());
    }

    public IFileInfo GetFileInfo(string subpath) =>
        _providers.Select(p => p.GetFileInfo(subpath)).FirstOrDefault(f => f.Exists) ?? new NotFoundFileInfo(subpath);

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}
