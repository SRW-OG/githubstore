using System.Net;
using GitHubStore.App.Services.Preferences;
using GitHubStore.App.Services.Tools;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>Each operation uses a settings snapshot and application-managed tools, never a user-supplied exe.</summary>
public sealed class ConfiguredDownloadService : IDownloadService
{
    private readonly PreferencesService _preferences;
    private readonly Func<AppPreferences, HttpClient> _clientFactory;
    private readonly IToolManager _tools;
    private bool _aria2Available;

    public ConfiguredDownloadService(PreferencesService preferences, Func<AppPreferences, HttpClient>? clientFactory = null,
        IToolManager? tools = null)
    {
        _preferences = preferences;
        _clientFactory = clientFactory ?? CreateClient;
        _tools = tools ?? new ManagedToolManager(preferences.DataDirectory,
            new BundledToolPackageSource(typeof(ConfiguredDownloadService).Assembly),
            _ => Task.FromResult<string?>(null));
    }

    public IReadOnlyList<DownloadEngine> AvailableEngines => _aria2Available
        ? [DownloadEngine.Http, DownloadEngine.Aria2] : [DownloadEngine.Http];

    public async Task<bool> ProbeAria2Async(CancellationToken ct = default)
    {
        var resolved = await _tools.EnsureAsync(ManagedToolKind.Aria2, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!resolved.IsAvailable || string.IsNullOrEmpty(resolved.ExecutablePath))
        {
            _aria2Available = false;
            return false;
        }
        _aria2Available = await new Aria2ProcessRunner(resolved.ExecutablePath).ProbeAsync(ct).ConfigureAwait(false);
        return _aria2Available;
    }

    public Task<DownloadResult> DownloadAsync(DownloadRequest request,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
        DownloadWithSnapshotAsync(request, _preferences.Current, progress, ct);

    public async Task<DownloadResult> DownloadWithSnapshotAsync(DownloadRequest request, AppPreferences snapshot,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuredRequest = request with { Engine = snapshot.DownloadEngine, UseAccelerator = snapshot.UseAccelerator };
        DownloadResult Failure(string code) => DownloadResult.Failure(code, request.Source, configuredRequest.Engine);
        if (ct.IsCancellationRequested) return Failure(DownloadErrorCodes.Cancelled);
        if (!string.IsNullOrWhiteSpace(request.PeerSource)) return Failure(DownloadErrorCodes.PeerNotSupported);
        var validation = DownloadFileRules.Validate(configuredRequest, out _);
        if (validation is not null) return Failure(validation);

        string? aria2Path = null;
        if (configuredRequest.Engine == DownloadEngine.Aria2)
        {
            var resolved = await _tools.EnsureAsync(ManagedToolKind.Aria2, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || resolved.ErrorCode == ToolErrorCodes.Cancelled)
                return Failure(DownloadErrorCodes.Cancelled);
            if (!resolved.IsAvailable || string.IsNullOrEmpty(resolved.ExecutablePath))
                return Failure(DownloadErrorCodes.Aria2NotFound);
            aria2Path = resolved.ExecutablePath;
        }

        // Xget is an integrated network provider. No executable or user-entered address is required.
        string? xgetBase = null;
        if (configuredRequest.UseAccelerator)
        {
            var resolved = await _tools.EnsureAsync(ManagedToolKind.Xget, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || resolved.ErrorCode == ToolErrorCodes.Cancelled)
                return Failure(DownloadErrorCodes.Cancelled);
            if (!resolved.IsAvailable || resolved.Endpoint is null)
                return Failure(DownloadErrorCodes.NetworkError);
            xgetBase = resolved.Endpoint.AbsoluteUri;
        }
        using var http = _clientFactory(snapshot);
        var service = new CompositeDownloadService(http, aria2Path, xgetBase) { Proxy = snapshot.ToProxySettings() };
        return await service.DownloadAsync(configuredRequest, progress, ct).ConfigureAwait(false);
    }

    public static SocketsHttpHandler CreateHandler(AppPreferences settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UseProxy = true,
        };
        if (settings.ProxyKind != ProxyKind.None)
        {
            var scheme = settings.ProxyKind == ProxyKind.Socks5 ? "socks5" : "http";
            var address = new UriBuilder(scheme, settings.ProxyHost!, settings.ProxyPort).Uri;
            var proxy = new WebProxy(address);
            if (!string.IsNullOrEmpty(settings.ProxyUserName))
                proxy.Credentials = new NetworkCredential(settings.ProxyUserName, settings.ProxyPassword);
            handler.Proxy = proxy;
        }
        return handler;
    }

    private static HttpClient CreateClient(AppPreferences settings)
    {
        var http = new HttpClient(CreateHandler(settings)) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubStore/0.1");
        return http;
    }
}
