namespace CensorCore.ModelLoader;

public class ModelLoaderOptions {
    public bool PreferBaseModel {get;set;} = false;
    public bool GetClassifier {get;set;} = false;
    public string RepositorySlug {get;set;} = KnownModels.Repository;
    /// <summary>
    /// The model to use (640m, 320n or a hotscreen model): that file is looked for, and downloaded when it isn't found.
    /// Unset, any model file found is used, 640m before 320n before others, and 640m is downloaded when there is none.
    /// </summary>
    public KnownModel? Model {get;set;}
    /// <summary>Where a downloaded model is saved; the temp folder is the fallback.</summary>
    public string? DownloadDirectory {get;set;}
}
