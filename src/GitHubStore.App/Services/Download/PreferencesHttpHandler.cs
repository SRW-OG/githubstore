using System.Collections.Concurrent;
using GitHubStore.App.Services.Preferences;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>元数据请求按代理快照选择连接池；旧请求在设置变化后仍可完成。</summary>
public sealed class PreferencesHttpHandler(PreferencesService preferences) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<ProxySettings, HttpMessageInvoker> _senders = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = preferences.Current;
        var sender = _senders.GetOrAdd(snapshot.ToProxySettings(),
            _ => new HttpMessageInvoker(ConfiguredDownloadService.CreateHandler(snapshot)));
        return sender.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var sender in _senders.Values) sender.Dispose();
            _senders.Clear();
        }
        base.Dispose(disposing);
    }
}
