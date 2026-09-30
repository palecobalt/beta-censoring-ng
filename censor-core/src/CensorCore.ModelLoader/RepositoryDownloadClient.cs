using Octokit;

namespace CensorCore.ModelLoader
{
    public class RepositoryDownloadClient
    {
        private readonly string _owner;
        private readonly string _repo;

        public RepositoryDownloadClient(string repoId)
        {
            this._owner = repoId.Split("/").First();
            this._repo = repoId.Split("/").Last();
        }

        /// <summary>
        /// Downloads one of NudeNet's v3 models from its fixed release asset and checks its SHA-256. One API request
        /// instead of the three <see cref="DownloadModel"/> needs, which matters on shared IP addresses: GitHub allows
        /// 60 unauthenticated API requests an hour per address.
        /// </summary>
        public static async Task<(string FileName, byte[] ModelData)> DownloadKnownModel(KnownModel model) {
            var data = await DownloadAsset(KnownModels.DownloadUrl(model));
            KnownModels.Verify(model, data);
            return (model.FileName, data);
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