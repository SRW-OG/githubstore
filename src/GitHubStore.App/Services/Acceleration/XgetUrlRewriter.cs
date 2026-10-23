namespace GitHubStore.App.Services.Acceleration;

/// <summary>仅开放已明确对应 github.com 的 HTTPS /gh/ 路由。</summary>
public sealed class XgetUrlRewriter
{
    public const string DefaultInstance = "https://xget.xi-xu.me";
    private readonly Uri _instance;

    public XgetUrlRewriter(string? instanceBase = null)
    {
        var candidate = string.IsNullOrWhiteSpace(instanceBase) ? DefaultInstance : instanceBase.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
            uri = new Uri(DefaultInstance, UriKind.Absolute);
        _instance = new Uri($"{uri.Scheme}://{uri.Authority}", UriKind.Absolute);
    }

    public Uri Instance => _instance;

    public static bool IsSupportedHost(Uri? uri) => uri is { IsAbsoluteUri: true }
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort;

    public bool TryRewrite(Uri? source, out Uri rewritten)
    {
        rewritten = source ?? throw new ArgumentNullException(nameof(source));
        if (!IsSupportedHost(source) || source.AbsolutePath == "/") return false;
        // 保留转义后的路径与查询；其余 GitHub 内容域没有已验证的 /gh/ 映射。
        rewritten = new Uri(_instance.AbsoluteUri.TrimEnd('/') + "/gh" + source.AbsolutePath + source.Query);
        return true;
    }

    public Uri RewriteOrOriginal(Uri source) => TryRewrite(source, out var rewritten) ? rewritten : source;
}
