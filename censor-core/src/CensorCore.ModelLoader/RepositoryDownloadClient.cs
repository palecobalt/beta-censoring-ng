using System.Security.Cryptography;
using Octokit;

namespace CensorCore.ModelLoader
{
    public class RepositoryDownloadClient
    {
        // NudeNet's v3.4-weights release, as in scripts/fetch-models.sh
        public const string DefaultRepository = "notAI-tech/NudeNet";
        public const string DefaultModelName = "320n.onnx";
        public const long DefaultModelAssetId = 176831997;
        public const string DefaultModelSha256 = "c15d8273adad2d0a92f014cc69ab2d6c311a06777a55545f2c4eb46f51911f0f";
        public const string DefaultModelPage = "https://github.com/notAI-tech/NudeNet/releases/tag/v3.4-weights";

        private readonly string _owner;
        private readonly string _repo;

        public RepositoryDownloadClient(string repoId)
        {
            this._owner = repoId.Split("/").First();
            this._repo = repoId.Split("/").Last();
        }

        /// <summary>
        /// Downloads NudeNet's 320n model from a fixed release asset and checks its SHA-256. One API request instead
        /// of the three <see cref="DownloadModel"/> needs, which matters on shared IP addresses: GitHub allows 60
        /// unauthenticated API requests an hour per address.
        /// </summary>
        public static async Task<(string FileName, byte[] ModelData)> DownloadDefaultModel() {
            var data = await DownloadAsset($"https://api.github.com/repos/{DefaultRepository}/releases/assets/{DefaultModelAssetId}");
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (hash != DefaultModelSha256) {
                throw new InvalidDataException($"the downloaded {DefaultModelName} has SHA-256 {hash}, expected {DefaultModelSha256}");
            }
            return (DefaultModelName, data);
        }

        public async Task<(string FileName, byte[] ModelData)?> DownloadModel(bool getClassifier = false, bool preferBase = false) {
            var github = new GitHubClient(new ProductHeaderValue("CensorCore.ModelLoader"));
            var releases = await github.Repository.Release.GetAll("notAI-tech", "NudeNet");
            var checkpointRelease = releases.OrderByDescending(r => r.CreatedAt).FirstOrDefault(r => r.Assets.Any(a => a.Name.EndsWith(".onnx")));
            if (checkpointRelease != null) {
                if (getClassifier) {
                    var classifierRelease = checkpointRelease.Assets.FirstOrDefault(r => r.Name.Contains("classifier") && r.Name.EndsWith(".onnx"));
                    if (classifierRelease == null) {
                        throw new Exception("Could not locate detector asset in GitHub Releases!");
                    }
                    var baseAsset = await github.Repository.Release.GetAsset(this._owner, this._repo, classifierRelease.Id);
                    var download = await DownloadAsset(baseAsset.Url);
                    return (baseAsset.Name, download);
                } else {
                    // NudeNet v3 releases only ship YOLOv8 weights (320n/640m) with no "detector" assets
                    var baseRelease = checkpointRelease.Assets.FirstOrDefault(r => r.IsDetector(preferBase))
                        ?? checkpointRelease.Assets.FirstOrDefault(r => r.Name == "320n.onnx");
                    if (baseRelease != null) {
                        var baseAsset = await github.Repository.Release.GetAsset(this._owner, this._repo, baseRelease.Id);
                        var download = await DownloadAsset(baseAsset.Url);
                        return (baseAsset.Name, download);
                    }
                }
                return null;
            }
            return null;
        }

        // Octokit 14 returns no body for binary asset downloads (Get<byte[]> gives null, GetRaw throws), so the
        // asset is fetched directly; GitHub redirects the API URL to its release storage.
        private static async Task<byte[]> DownloadAsset(string assetApiUrl) {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CensorCore.ModelLoader");
            using var request = new HttpRequestMessage(HttpMethod.Get, assetApiUrl);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }
    }

    public static class AssetExtensions {
        public static bool IsDetector(this ReleaseAsset asset, bool preferBase) {
            var outcome = asset.Name.EndsWith(".onnx") &&
                asset.Name.Contains("detector") &&
                (preferBase 
                    ? asset.Name.Contains("_base_") 
                    : !asset.Name.Contains("_base"));
            return outcome;
        } 
    }
}