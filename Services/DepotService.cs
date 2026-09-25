namespace GreenLuma_Manager.Services;

public class DepotInfo
{
    public required string DepotId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Os { get; set; } = "all"; // "windows", "mac", "linux", "all"
    public string Type { get; set; } = "game"; // "game", "dlc", "shared", "related_dlc"
    public bool IsDlc { get; set; }
    public string? DlcAppId { get; set; }
    public bool IsSharedInstall { get; set; }
    public string? SharedFromAppId { get; set; }
    public ulong Size { get; set; }
    public string? ManifestId { get; set; }

    public bool IsCompatibleWithWindows =>
        string.IsNullOrEmpty(Os) ||
        Os.Equals("all", StringComparison.OrdinalIgnoreCase) ||
        Os.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        Os.Contains("win", StringComparison.OrdinalIgnoreCase);
}

public class AppPackageInfo
{
    public string AppId { get; set; } = string.Empty;
    public List<string> Depots { get; set; } = [];
    public List<string> DlcAppIds { get; set; } = [];
    public Dictionary<string, List<string>> DlcDepots { get; set; } = [];
    public List<DepotInfo> DepotDetails { get; set; } = [];
}

public static class DepotService
{
    public static async Task<AppPackageInfo?> FetchAppPackageInfoAsync(string appId)
    {
        if (!uint.TryParse(appId, out var id))
            return null;

        return await SteamService.Instance.GetAppPackageInfoAsync(id).ConfigureAwait(false);
    }
}