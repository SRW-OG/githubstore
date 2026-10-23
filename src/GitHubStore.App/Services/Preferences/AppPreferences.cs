using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Preferences;

public enum ThemeMode { System, Light, Dark }

/// <summary>User choices; system values follow the desktop until explicitly changed.</summary>
public sealed record AppPreferences
{
    public ThemeMode Theme { get; init; } = ThemeMode.System;
    public string Language { get; init; } = "system";

    /// <summary>
    /// 随程序发布的软件目录库地址：首次启动即从该文件同步，软件库更新只需提交仓库里的 catalog.json。
    /// 用户在设置中清空地址并保存后，就不再进行远端同步（null 表示只用本地目录）。
    /// </summary>
    public const string DefaultCatalogLibraryUrl =
        "https://raw.githubusercontent.com/SRW-OG/githubstore/main/catalog/catalog.json";

    /// <summary>HTTPS direct URL for the shared software catalog JSON; null keeps only the local catalog.</summary>
    public string? CatalogLibraryUrl { get; init; } = DefaultCatalogLibraryUrl;

    /// <summary>
    /// 用户在设置中清空库地址并保存后为 true：表示不使用远端软件目录库。
    /// 旧版本没有这个字段，其保存的空地址只是当时的默认值，因此升级后仍使用随程序发布的默认地址，
    /// 不会被误读成“用户主动停用同步”。
    /// </summary>
    public bool CatalogLibraryOptedOut { get; init; }

    public bool SyncCatalogLibraryOnStartup { get; init; } = true;

    /// <summary>下载缓存目录；null 表示使用应用默认缓存目录。</summary>
    public string? DownloadCacheDirectory { get; init; }

    /// <summary>默认安装目录；null 表示使用默认值 C:\githubapps。</summary>
    public string? InstallRootDirectory { get; init; }

    /// <summary>首选下载引擎；aria2 由应用准备，组件失败如实报告，不暗中切换引擎。</summary>
    public DownloadEngine DownloadEngine { get; init; } = DownloadEngine.Http;

    /// <summary>兼容旧设置保留的字段；运行服务忽略此路径，使用已验证的内置组件。</summary>
    public string? Aria2ExecutablePath { get; init; }

    /// <summary>兼容旧设置保留的字段；运行服务使用内置 Xget provider，界面不再要求填写。</summary>
    public string? XgetInstance { get; init; }

    /// <summary>是否经 Xget 加速 GitHub 下载。</summary>
    public bool UseAccelerator { get; init; }

    /// <summary>是否允许 P2P 下载（需要 aria2）。</summary>
    public bool EnableP2P { get; init; }

    /// <summary>网络代理类型；None 使用系统代理，与默认 HTTP handler 一致。</summary>
    public ProxyKind ProxyKind { get; init; } = ProxyKind.None;
    public string? ProxyHost { get; init; }
    public int ProxyPort { get; init; }
    public string? ProxyUserName { get; init; }
    /// <summary>仅在内存中提供解密后的代理密码；PreferencesService 单独保护后落盘。</summary>
    [JsonIgnore]
    public string? ProxyPassword { get; init; }

    /// <summary>OpenAI-compatible base URL or full Chat Completions endpoint.</summary>
    public string AiEndpoint { get; init; } = "https://api.openai.com/v1";
    public string AiModel { get; init; } = "gpt-4o-mini";
    /// <summary>In-memory only; persisted separately under current-user DPAPI protection.</summary>
    [JsonIgnore]
    public string? AiApiKey { get; init; }

    /// <summary>把偏好里的代理字段组合成下载层需要的设置。</summary>
    public ProxySettings ToProxySettings() => new()
    {
        Kind = ProxyKind,
        Host = ProxyHost,
        Port = ProxyPort,
        UserName = ProxyUserName,
        Password = ProxyPassword,
    };
}
