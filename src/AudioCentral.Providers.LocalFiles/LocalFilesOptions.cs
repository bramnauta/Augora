namespace AudioCentral.Providers.LocalFiles;

public sealed class LocalFilesOptions
{
    public const string SectionName = "Providers:LocalFiles";

    public bool Enabled { get; set; } = true;

    /// <summary>Music folders, e.g. a NAS share mounted at <c>/music</c>. Without folders the provider is hidden.</summary>
    public List<LocalFolderOptions> Folders { get; set; } = [];

    /// <summary>How often the folders are scanned for new or changed files.</summary>
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromHours(6);
}

public sealed class LocalFolderOptions
{
    /// <summary>Shown in the UI and part of every item id, so keep it stable.</summary>
    public string Name { get; set; } = "Music";

    public string Path { get; set; } = "";
}
