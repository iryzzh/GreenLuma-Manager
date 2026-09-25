using GreenLuma_Manager.Models;
using SteamKit2;

namespace GreenLuma_Manager.Services;

public sealed class SteamService : IDisposable
{
    private static readonly Lazy<SteamService> InstanceHolder = new(() => new SteamService());
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _apiThrottle = new(2, 2);

    private readonly Task _callbackLoop;
    private readonly CallbackManager _callbackManager;
    private readonly CancellationTokenSource _cts;
    private readonly object _readyLock = new();
    private readonly SteamApps _steamApps;
    private readonly SteamClient _steamClient;
    private readonly SteamUser _steamUser;

    private TaskCompletionSource _connectedTcs;

    private volatile bool _isConnected;
    private volatile bool _isLoggedOn;
    private volatile bool _isRunning;

    private DateTime _lastFailureTime = DateTime.MinValue;
    private TaskCompletionSource _loggedOnTcs;
    private int _reconnectAttempt;

    private SteamService()
    {
        using var timer = Logger.Measure("SteamService.ctor");
        _steamClient = new SteamClient(SteamConfiguration.Create(b =>
            b.WithProtocolTypes(ProtocolTypes.WebSocket)
                .WithConnectionTimeout(TimeSpan.FromSeconds(10))));

        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;

        _cts = new CancellationTokenSource();
        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        _isRunning = true;
        _callbackLoop = Task.Run(CallbackLoop);

        _steamClient.Connect();
    }

    public static SteamService Instance => InstanceHolder.Value;
    public static bool IsInitialized => InstanceHolder.IsValueCreated;

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _steamClient.Disconnect();
        try
        {
            _callbackLoop.Wait(1000);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SteamService.Dispose");
        }

        _apiThrottle.Dispose();
        _cts.Dispose();
    }

    public async Task<GameDetails?> GetGameDetailsAsync(uint appId)
    {
        var result = await GetAppInfoBatchAsync([appId]).ConfigureAwait(false);
        return result.GetValueOrDefault(appId);
    }

    public async Task<Dictionary<uint, GameDetails>> GetAppInfoBatchAsync(List<uint> appIds)
    {
        var results = new Dictionary<uint, GameDetails>();
        var maxRetries = 2;

        await _apiThrottle.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
                try
                {
                    if (!await EnsureReadyAsync().ConfigureAwait(false)) return results;

                    var tokens = new Dictionary<uint, ulong>();
                    try
                    {
                        var tokenResult =
                            await _steamApps.PICSGetAccessTokens(appIds, []).ToTask().ConfigureAwait(false);
                        foreach (var (appId, token) in tokenResult.AppTokens)
                            tokens[appId] = token;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "SteamService.GetTokens");
                    }

                    var requests = appIds.Select(id => new SteamApps.PICSRequest
                    {
                        ID = id,
                        AccessToken = tokens.TryGetValue(id, out var t) ? t : 0
                    }).ToList();

                    var job = _steamApps.PICSGetProductInfo(requests, []);
                    var task = job.ToTask();

                    if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
                    {
                        ObserveTask(task);
                        if (attempt < maxRetries) continue;
                        break;
                    }

                    var result = await task.ConfigureAwait(false);

                    if (result.Failed || result.Results == null)
                    {
                        if (attempt < maxRetries)
                        {
                            await Task.Delay(500).ConfigureAwait(false);
                            continue;
                        }

                        return results;
                    }

                    foreach (var callback in result.Results)
                    foreach (var (appId, appData) in callback.Apps)
                    {
                        var kv = appData.KeyValues;
                        var common = kv["common"];

                        var name = common["name"].Value ?? $"App {appId}";
                        var type = MapSteamTypeToDisplayType(common["type"].Value ?? "Game");

                        var clientIconHash = common["clienticon"].Value;
                        var parentId = common["parent"].Value;

                        var libAssets = common["library_assets"];
                        var heroHash = libAssets["hero_capsule"]["image"].Value;

                        var assets = common["assets"];
                        var mainHash = assets["main_capsule"]["image"].Value;

                        var headerNode = common["header_image"];
                        var headerImage = headerNode.Value;
                        if (string.IsNullOrEmpty(headerImage))
                            headerImage = headerNode["english"].Value;

                        List<string>? dlcList = null;
                        var dlcListValue = kv["extended"]["listofdlc"].Value;
                        if (!string.IsNullOrEmpty(dlcListValue))
                            dlcList = dlcListValue.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

                        var depotsNode = kv["depots"];
                        if (depotsNode != KeyValue.Invalid)
                            foreach (var depot in depotsNode.Children)
                            {
                                if (!uint.TryParse(depot.Name, out _)) continue;
                                var dlcAppId = depot["dlcappid"].Value;
                                if (!string.IsNullOrEmpty(dlcAppId))
                                {
                                    dlcList ??= [];
                                    if (!dlcList.Contains(dlcAppId))
                                        dlcList.Add(dlcAppId);
                                }
                            }

                        results[appId] = new GameDetails(
                            appId.ToString(),
                            type,
                            name,
                            clientIconHash,
                            heroHash,
                            mainHash,
                            parentId,
                            headerImage,
                            dlcList
                        );
                    }

                    if (results.Count > 0) return results;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "SteamService.GetAppInfoBatch");
                    if (attempt == maxRetries) break;
                    await Task.Delay(500).ConfigureAwait(false);
                }
        }
        finally
        {
            _apiThrottle.Release();
        }

        return results;
    }

    public async Task<AppPackageInfo?> GetAppPackageInfoAsync(uint appId)
    {
        await _apiThrottle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await EnsureReadyAsync().ConfigureAwait(false)) return null;

            var request = new SteamApps.PICSRequest { ID = appId, AccessToken = 0 };
            var job = _steamApps.PICSGetProductInfo([request], []);
            var task = job.ToTask();

            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
            {
                ObserveTask(task);
                return null;
            }

            var result = await task.ConfigureAwait(false);

            if (result.Failed || result.Results == null) return null;

            foreach (var callback in result.Results)
                if (callback.Apps.TryGetValue(appId, out var appData))
                    return ParseAppPackageInfo(appId, appData);

            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SteamService.GetAppPackageInfo");
            return null;
        }
        finally
        {
            _apiThrottle.Release();
        }
    }

    private static AppPackageInfo? ParseAppPackageInfo(uint appId,
        SteamApps.PICSProductInfoCallback.PICSProductInfo appData)
    {
        var kv = appData.KeyValues;

        var type = kv["common"]["type"].Value;
        if (string.Equals(type, "depot", StringComparison.OrdinalIgnoreCase))
            return null;

        var info = new AppPackageInfo
        {
            AppId = appId.ToString()
        };

        var dlcList = kv["extended"]["listofdlc"].Value;
        if (!string.IsNullOrEmpty(dlcList))
            info.DlcAppIds = dlcList.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

        foreach (var dlcId in info.DlcAppIds)
            info.DlcDepots[dlcId] = [];

        var depotsNode = kv["depots"];
        foreach (var child in depotsNode.Children)
        {
            if (!uint.TryParse(child.Name, out var depotId))
                continue;

            if (depotId == appId)
                continue;

            var name = child["name"].Value ?? string.Empty;
            if (name.Contains("Unused", StringComparison.OrdinalIgnoreCase))
                continue;

            if (child["manifests"] == KeyValue.Invalid && child["depotfromapp"] == KeyValue.Invalid)
                continue;

            var osList = child["config"]["oslist"].Value;
            if (string.IsNullOrEmpty(osList))
                osList = child["oslist"].Value;
            if (string.IsNullOrEmpty(osList))
                osList = "all";

            var sharedApp = child["depotfromapp"].Value;
            var isShared = !string.IsNullOrEmpty(sharedApp) || child["sharedinstall"].Value == "1";

            var dlcAppId = child["dlcappid"].Value;
            var isDlc = !string.IsNullOrEmpty(dlcAppId);

            var manifestId = child["manifests"]["public"]["gid"].Value;
            if (string.IsNullOrEmpty(manifestId))
                manifestId = child["manifests"]["public"].Value;

            ulong size = 0;
            if (ulong.TryParse(child["maxsize"].Value, out var sz) || ulong.TryParse(child["size"].Value, out sz))
                size = sz;

            var depotDetail = new DepotInfo
            {
                DepotId = depotId.ToString(),
                Name = name,
                Os = osList,
                Type = isDlc ? "dlc" : (isShared ? "shared" : "game"),
                IsDlc = isDlc,
                DlcAppId = isDlc ? dlcAppId : null,
                IsSharedInstall = isShared,
                SharedFromAppId = sharedApp,
                Size = size,
                ManifestId = manifestId
            };

            info.DepotDetails.Add(depotDetail);

            if (!depotDetail.IsCompatibleWithWindows)
                continue;

            if (isDlc && info.DlcDepots.TryGetValue(dlcAppId!, out var dlcDepotList))
            {
                dlcDepotList.Add(depotId.ToString());
            }
            else if (isDlc)
            {
                info.DlcDepots[dlcAppId!] = [depotId.ToString()];
                if (!info.DlcAppIds.Contains(dlcAppId!))
                    info.DlcAppIds.Add(dlcAppId!);
            }
            else
            {
                info.Depots.Add(depotId.ToString());
            }
        }

        return info;
    }

    public async Task<List<uint>> GetPackageAppIdsAsync(uint packageId)
    {
        var appIds = new List<uint>();
        try
        {
            if (!await EnsureReadyAsync().ConfigureAwait(false)) return appIds;

            var request = new SteamApps.PICSRequest { ID = packageId, AccessToken = 0 };
            var job = _steamApps.PICSGetProductInfo([], [request]);

            var task = job.ToTask();
            if (await Task.WhenAny(task, Task.Delay(5000)) != task)
                return appIds;

            var result = await task.ConfigureAwait(false);
            if (result.Failed || result.Results == null)
                return appIds;

            foreach (var callback in result.Results)
            {
                if (!callback.Packages.TryGetValue(packageId, out var pkgData))
                    continue;

                var kv = pkgData.KeyValues;
                var appIdsNode = kv["appids"];
                foreach (var child in appIdsNode.Children)
                    if (uint.TryParse(child.Value, out var appId))
                        appIds.Add(appId);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SteamService.GetPackageAppIds");
        }

        return appIds;
    }

    public async Task<List<uint>> GetAppPackageIdsAsync(uint appId)
    {
        var packageIds = new List<uint>();
        try
        {
            if (!await EnsureReadyAsync().ConfigureAwait(false)) return packageIds;

            var request = new SteamApps.PICSRequest { ID = appId, AccessToken = 0 };
            var job = _steamApps.PICSGetProductInfo([request], []);

            var task = job.ToTask();
            if (await Task.WhenAny(task, Task.Delay(5000)) != task)
                return packageIds;

            var result = await task.ConfigureAwait(false);
            if (result.Failed || result.Results == null)
                return packageIds;

            foreach (var callback in result.Results)
            {
                if (!callback.Apps.TryGetValue(appId, out var appData))
                    continue;

                var kv = appData.KeyValues;
                var depotsNode = kv["depots"];
                var baseLicensesNode = depotsNode["baselicenses"];
                foreach (var child in baseLicensesNode.Children)
                    if (uint.TryParse(child.Value, out var pkgId))
                        packageIds.Add(pkgId);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SteamService.GetAppPackageIds");
        }

        return packageIds;
    }

    public async Task<List<uint>> ScanRangeForDlcsAsync(uint baseAppId, List<uint> knownDlcIds)
    {
        var foundDlcIds = new List<uint>();

        try
        {
            if (!await EnsureReadyAsync().ConfigureAwait(false)) return foundDlcIds;

            var clusters = new List<(uint Min, uint Max)>();
            if (knownDlcIds.Count > 0)
            {
                var sorted = knownDlcIds.OrderBy(x => x).ToList();
                var clusterStart = sorted[0];
                var clusterEnd = sorted[0];

                for (var i = 1; i < sorted.Count; i++)
                    if (sorted[i] - clusterEnd <= 1000)
                    {
                        clusterEnd = sorted[i];
                    }
                    else
                    {
                        clusters.Add((clusterStart, clusterEnd));
                        clusterStart = sorted[i];
                        clusterEnd = sorted[i];
                    }

                clusters.Add((clusterStart, clusterEnd));
            }
            else
            {
                clusters.Add((baseAppId, baseAppId));
            }

            var idsToScan = new HashSet<uint>();
            var knownSet = new HashSet<uint>(knownDlcIds) { baseAppId };

            foreach (var (min, max) in clusters)
            {
                var rangeStart = min >= 100 ? min - 100 : 0;
                var rangeEnd = max + 100;
                for (var id = rangeStart; id <= rangeEnd; id++)
                    if (!knownSet.Contains(id))
                        idsToScan.Add(id);
            }

            if (idsToScan.Count == 0) return foundDlcIds;

            var baseAppIdStr = baseAppId.ToString();
            foreach (var batch in idsToScan.Chunk(500))
                try
                {
                    var requests = batch.Select(id => new SteamApps.PICSRequest
                    {
                        ID = id, AccessToken = 0
                    }).ToList();

                    var job = _steamApps.PICSGetProductInfo(requests, []);
                    var task = job.ToTask();
                    if (await Task.WhenAny(task, Task.Delay(10000)) != task)
                        continue;

                    var result = await task.ConfigureAwait(false);
                    if (result.Failed || result.Results == null) continue;

                    foreach (var callback in result.Results)
                    foreach (var (appId, appData) in callback.Apps)
                    {
                        var kv = appData.KeyValues;
                        var common = kv["common"];
                        var type = common["type"].Value;
                        var parent = common["parent"].Value;
                        var dlcForAppId = kv["extended"]["dlcforappid"].Value;

                        var isParentMatch = string.Equals(parent, baseAppIdStr, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(dlcForAppId, baseAppIdStr, StringComparison.OrdinalIgnoreCase);

                        if (isParentMatch)
                            foundDlcIds.Add(appId);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "SteamService.ScanRange.Batch");
                }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SteamService.ScanRangeForDlcs");
        }

        return foundDlcIds;
    }

    public async Task<(Game? BaseGame, List<Game> Dlcs)> GetGameAndAllDlcsAsync(uint appId)
    {
        var baseDetails = await GetGameDetailsAsync(appId).ConfigureAwait(false);
        if (baseDetails == null || baseDetails.Name == $"App {appId}")
        {
            var pkgAppIds = await GetPackageAppIdsAsync(appId).ConfigureAwait(false);
            if (pkgAppIds.Count > 0)
            {
                var appDetails = await GetAppInfoBatchAsync(pkgAppIds).ConfigureAwait(false);
                var pkgResults = new List<Game>();
                foreach (var pId in pkgAppIds)
                {
                    appDetails.TryGetValue(pId, out var d);
                    pkgResults.Add(new Game
                    {
                        AppId = pId.ToString(),
                        Name = d?.Name ?? $"App {pId}",
                        Type = d?.Type ?? "DLC",
                        IconUrl = string.Empty
                    });
                }

                return (
                    new Game
                    {
                        AppId = appId.ToString(),
                        Name = $"Package {appId}",
                        Type = "Package",
                        IconUrl = string.Empty
                    },
                    pkgResults);
            }

            return (null, []);
        }

        var baseGame = new Game
        {
            AppId = appId.ToString(),
            Name = baseDetails.Name,
            Type = baseDetails.Type,
            IconUrl = string.Empty
        };

        var knownDlcIds = new HashSet<uint>();
        if (baseDetails.ListOfDlc != null)
        {
            foreach (var idStr in baseDetails.ListOfDlc)
                if (uint.TryParse(idStr, out var id))
                    knownDlcIds.Add(id);
        }

        var packageInfo = await GetAppPackageInfoAsync(appId).ConfigureAwait(false);
        if (packageInfo != null)
        {
            foreach (var dlcIdStr in packageInfo.DlcAppIds)
                if (uint.TryParse(dlcIdStr, out var dlcId))
                    knownDlcIds.Add(dlcId);

            if (packageInfo.Depots.Count > 0)
                baseGame.Depots = [.. packageInfo.Depots];
        }

        var relatedDlcIds = new HashSet<uint>();
        try
        {
            var packageIds = await GetAppPackageIdsAsync(appId).ConfigureAwait(false);
            foreach (var pkgId in packageIds.Take(10))
            {
                var pkgApps = await GetPackageAppIdsAsync(pkgId).ConfigureAwait(false);
                foreach (var pAppId in pkgApps)
                {
                    if (pAppId != appId && !knownDlcIds.Contains(pAppId))
                        relatedDlcIds.Add(pAppId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "SteamService.GetGameAndAllDlcs.RelatedPackages");
        }

        var allKnown = knownDlcIds.Concat(relatedDlcIds).ToList();
        var scannedDlcs = await ScanRangeForDlcsAsync(appId, allKnown).ConfigureAwait(false);

        var allDlcIdsToQuery = knownDlcIds
            .Concat(relatedDlcIds)
            .Concat(scannedDlcs)
            .Distinct()
            .ToList();

        var dlcDetails = allDlcIdsToQuery.Count > 0
            ? await GetAppInfoBatchAsync(allDlcIdsToQuery).ConfigureAwait(false)
            : new Dictionary<uint, GameDetails>();

        var dlcGames = new List<Game>();

        foreach (var dlcId in allDlcIdsToQuery)
        {
            dlcDetails.TryGetValue(dlcId, out var details);
            var name = details?.Name;
            var isUnknown = string.IsNullOrWhiteSpace(name) || name == $"App {dlcId}";
            var finalName = isUnknown ? $"Unknown DLC {dlcId}" : name!;

            string type;
            if (isUnknown)
                type = "Unknown DLC";
            else if (relatedDlcIds.Contains(dlcId))
                type = details?.Type == "Soundtrack" ? "Soundtrack" : "Related DLC";
            else
                type = details?.Type ?? "DLC";

            var dlcGame = new Game
            {
                AppId = dlcId.ToString(),
                Name = finalName,
                Type = type,
                IconUrl = string.Empty
            };

            if (packageInfo?.DlcDepots != null && packageInfo.DlcDepots.TryGetValue(dlcId.ToString(), out var dlcDepots))
            {
                dlcGame.Depots = [.. dlcDepots];
            }

            dlcGames.Add(dlcGame);
        }

        return (baseGame, dlcGames);
    }

    private async Task<bool> EnsureReadyAsync()
    {
        if (_isConnected && _isLoggedOn) return true;

        if (DateTime.UtcNow - _lastFailureTime < FailureCooldown) return false;

        TaskCompletionSource connectedTcs;
        TaskCompletionSource loggedOnTcs;

        lock (_readyLock)
        {
            connectedTcs = _connectedTcs;
            loggedOnTcs = _loggedOnTcs;
        }

        if (await Task.WhenAny(connectedTcs.Task, Task.Delay(ConnectionTimeout)) != connectedTcs.Task)
        {
            _lastFailureTime = DateTime.UtcNow;
            return false;
        }

        if (await Task.WhenAny(loggedOnTcs.Task, Task.Delay(ConnectionTimeout)) != loggedOnTcs.Task)
        {
            _lastFailureTime = DateTime.UtcNow;
            return false;
        }

        return true;
    }

    private async Task CallbackLoop()
    {
        while (_isRunning && !_cts.Token.IsCancellationRequested)
        {
            _callbackManager.RunCallbacks();
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _reconnectAttempt = 0;

        lock (_readyLock)
        {
            _isConnected = true;
            _connectedTcs.TrySetResult();
        }

        _steamUser.LogOnAnonymous();
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        lock (_readyLock)
        {
            _isConnected = false;
            _isLoggedOn = false;
            _connectedTcs = new TaskCompletionSource();
            _loggedOnTcs = new TaskCompletionSource();
        }

        if (!_isRunning || callback.UserInitiated) return;

        var delay = Math.Min(5 * (1 << Math.Min(_reconnectAttempt, 4)), 60);
        _reconnectAttempt++;

        Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ =>
        {
            if (_isRunning) _steamClient.Connect();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK) return;

        lock (_readyLock)
        {
            _isLoggedOn = true;
            _loggedOnTcs.TrySetResult();
        }

        _lastFailureTime = DateTime.MinValue;
    }

    private static void ObserveTask(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.NotOnRanToCompletion);
    }

    public static string MapSteamTypeToDisplayType(string steamType)
    {
        return steamType.ToLowerInvariant() switch
        {
            "game" => "Game",
            "dlc" => "DLC",
            "unknown dlc" => "Unknown DLC",
            "related dlc" or "related_dlc" => "Related DLC",
            "demo" => "Demo",
            "mod" => "Mod",
            "video" => "Video",
            "music" => "Soundtrack",
            "bundle" => "Bundle",
            "episode" => "Episode",
            "tool" or "advertising" => "Software",
            _ => "Game"
        };
    }
}

public record GameDetails(
    string AppId,
    string Type,
    string Name,
    string? ClientIconHash = null,
    string? HeroHash = null,
    string? MainHash = null,
    string? ParentAppId = null,
    string? HeaderImage = null,
    List<string>? ListOfDlc = null
);