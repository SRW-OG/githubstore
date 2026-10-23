using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GitHubStore.Core.Abstractions;
using GitHubStore.Platform.Win32.Security;

namespace GitHubStore.App.Services.Preferences;

/// <summary>Protects a nonempty in-memory secret as an opaque, versioned storage string.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

/// <summary>Application adapter for current-user DPAPI. Never falls back to plaintext.</summary>
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public string Protect(string plaintext)
    {
        var bytes = Utf8.GetBytes(plaintext);
        try { return Prefix + Convert.ToBase64String(DpapiSecretProtection.Protect(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported protected credential format.");
        byte[] ciphertext;
        try { ciphertext = Convert.FromBase64String(protectedValue[Prefix.Length..]); }
        catch (FormatException) { throw new CryptographicException("Invalid protected credential encoding."); }
        var plaintext = DpapiSecretProtection.Unprotect(ciphertext);
        try { return Utf8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}

/// <summary>
/// Settings storage with protected credentials. Changed can be raised on a worker thread;
/// UI consumers must dispatch before touching visual state.
/// </summary>
public sealed class PreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ThemeMode>(allowIntegerValues: false) }
    };

    private readonly string _filePath;
    private readonly ISecretProtector _secretProtector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppPreferences _current = new();
    private bool _secretLoadFailed;
    private bool _hasInspectedStoredSecrets;

    public PreferencesService(string filePath, ISecretProtector? secretProtector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _secretProtector = secretProtector ?? new WindowsDpapiSecretProtector();
    }

    public string DataDirectory => Path.GetDirectoryName(_filePath)!;
    public AppPreferences Current => Volatile.Read(ref _current);
    public string? Error { get; private set; }
    public string? ErrorDetail { get; private set; }
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            AppPreferences loaded;
            try
            {
                var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
                loaded = ReadPreferences(json);
                _secretLoadFailed = false;
                _hasInspectedStoredSecrets = true;
                Error = null;
                ErrorDetail = null;
            }
            catch (FileNotFoundException)
            {
                loaded = new AppPreferences();
                _secretLoadFailed = false;
                _hasInspectedStoredSecrets = true;
                Error = null;
                ErrorDetail = null;
            }
            catch (DirectoryNotFoundException)
            {
                loaded = new AppPreferences();
                _secretLoadFailed = false;
                _hasInspectedStoredSecrets = true;
                Error = null;
                ErrorDetail = null;
            }
            catch (CryptographicException)
            {
                // Preserve both the last known in-memory values and the original file.
                loaded = Current;
                _secretLoadFailed = true;
                Error = "SettingsSecretDecryptionFailed";
                ErrorDetail = "Saved credentials could not be decrypted. The settings file has been preserved; saving is blocked until it can be loaded successfully.";
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                loaded = new AppPreferences();
                Error = "SettingsInvalidFile";
                ErrorDetail = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                loaded = new AppPreferences();
                Error = "SettingsLoadFailed";
                ErrorDetail = ex.Message;
            }

            ct.ThrowIfCancellationRequested();
            changed = Current != loaded;
            Volatile.Write(ref _current, loaded);
        }
        finally
        {
            _gate.Release();
        }
        if (changed) NotifyChanged();
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = Validate(preferences);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporaryFile = null;
        var changed = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_hasInspectedStoredSecrets && File.Exists(_filePath))
            {
                try
                {
                    var existing = JsonNode.Parse(await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false)) as JsonObject;
                    if (existing?.ContainsKey("protectedProxyPassword") == true
                        || existing?.ContainsKey("protectedAiApiKey") == true || existing?.ContainsKey("aiApiKey") == true)
                        _ = ReadPreferences(existing.ToJsonString());
                    _hasInspectedStoredSecrets = true;
                }
                catch (CryptographicException)
                {
                    _secretLoadFailed = true;
                    throw;
                }
            }
            if (_secretLoadFailed)
                throw new CryptographicException("The existing saved credentials must be successfully decrypted before settings can be saved.");

            // Complete protection before creating a directory or a temporary file.
            var persisted = JsonSerializer.SerializeToNode(normalized, JsonOptions)!.AsObject();
            if (!string.IsNullOrEmpty(normalized.ProxyPassword))
            {
                try
                {
                    var protectedPassword = _secretProtector.Protect(normalized.ProxyPassword);
                    if (string.IsNullOrEmpty(protectedPassword))
                        throw new CryptographicException("The protector returned an empty credential.");
                    persisted["protectedProxyPassword"] = protectedPassword;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A protector error may contain sensitive input; never surface its message.
                    throw new CryptographicException("Proxy credentials could not be protected.");
                }
            }
            if (!string.IsNullOrEmpty(normalized.AiApiKey))
            {
                try
                {
                    var protectedKey = _secretProtector.Protect(normalized.AiApiKey);
                    if (string.IsNullOrEmpty(protectedKey))
                        throw new CryptographicException("The protector returned an empty credential.");
                    persisted["protectedAiApiKey"] = protectedKey;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new CryptographicException("AI credentials could not be protected.");
                }
            }
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            temporaryFile = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            // Temp and destination share a directory/volume; publish only a complete file.
            File.Move(temporaryFile, _filePath, overwrite: true);
            temporaryFile = null;
            changed = Current != normalized;
            Volatile.Write(ref _current, normalized);
            _hasInspectedStoredSecrets = true;
            Error = null;
            ErrorDetail = null;
        }
        catch (CryptographicException)
        {
            Error = _secretLoadFailed ? "SettingsSecretDecryptionFailed" : "SettingsSecretProtectionFailed";
            ErrorDetail = _secretLoadFailed
                ? "The original settings file is preserved. Reload it successfully before saving."
                : "Saved credentials could not be protected. The previous settings file is preserved.";
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "SettingsSaveFailed";
            ErrorDetail = ex.Message;
            throw;
        }
        finally
        {
            if (temporaryFile is not null)
            {
                try { File.Delete(temporaryFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.WriteLine(ex);
                }
            }
            _gate.Release();
        }
        if (changed) NotifyChanged();
    }

    private AppPreferences ReadPreferences(string json)
    {
        var document = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Settings must be a JSON object.");
        var hasProtected = document.Remove("protectedProxyPassword", out var protectedNode);
        var hasLegacy = document.Remove("proxyPassword", out var legacyNode);
        string? password = null;
        if (hasProtected)
        {
            try
            {
                if (hasLegacy || protectedNode is not JsonValue value
                    || !value.TryGetValue<string>(out var protectedPassword)
                    || string.IsNullOrEmpty(protectedPassword))
                    throw new CryptographicException("Invalid protected credential.");
                password = _secretProtector.Unprotect(protectedPassword);
                if (password is null)
                    throw new CryptographicException("The protector returned an invalid credential.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CryptographicException("Proxy credentials could not be decrypted.");
            }
        }
        else if (legacyNode is not null)
        {
            if (legacyNode is not JsonValue legacyValue || !legacyValue.TryGetValue<string>(out password))
                throw new JsonException("Legacy proxy password must be a string or null.");
        }
        var hasProtectedAi = document.Remove("protectedAiApiKey", out var aiNode);
        var hasPlaintextAi = document.Remove("aiApiKey", out _);
        string? apiKey = null;
        if (hasProtectedAi || hasPlaintextAi)
        {
            try
            {
                if (hasPlaintextAi || aiNode is not JsonValue value
                    || !value.TryGetValue<string>(out var protectedKey) || string.IsNullOrEmpty(protectedKey))
                    throw new CryptographicException("Invalid protected AI credential.");
                apiKey = _secretProtector.Unprotect(protectedKey);
                if (apiKey is null) throw new CryptographicException("Invalid decrypted AI credential.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CryptographicException("AI credentials could not be decrypted.");
            }
        }
        var loaded = document.Deserialize<AppPreferences>(JsonOptions)
            ?? throw new JsonException("Settings must be a JSON object.");
        return Validate(loaded with { ProxyPassword = password, AiApiKey = apiKey });
    }

    private static AppPreferences Validate(AppPreferences preferences)
    {
        if (!Enum.IsDefined(preferences.Theme))
            throw new ArgumentException("Unsupported theme.", nameof(preferences));
        var language = preferences.Language?.Trim();
        language = language?.ToLowerInvariant() switch
        {
            "system" => "system",
            "zh-cn" => "zh-CN",
            "en-us" => "en-US",
            _ => throw new ArgumentException("Unsupported language.", nameof(preferences))
        };
        if (!Enum.IsDefined(preferences.DownloadEngine))
            throw new ArgumentException("Unsupported download engine.", nameof(preferences));
        if (!Enum.IsDefined(preferences.ProxyKind))
            throw new ArgumentException("Unsupported proxy kind.", nameof(preferences));

        // None means system proxy: discard custom proxy fields, including its secret.
        var proxyKind = preferences.ProxyKind;
        var proxyHost = preferences.ProxyHost?.Trim();
        var proxyPort = preferences.ProxyPort;
        if (proxyKind != ProxyKind.None)
        {
            if (string.IsNullOrWhiteSpace(proxyHost))
                throw new ArgumentException("Proxy host is required when a custom proxy is enabled.", nameof(preferences));
            RejectControlCharacters(preferences.ProxyHost!, nameof(preferences.ProxyHost));
            proxyHost = NormalizeProxyHost(proxyHost);
            if (proxyPort is <= 0 or > 65535)
                throw new ArgumentException("Proxy port must be between 1 and 65535.", nameof(preferences));
        }
        else
        {
            proxyHost = null;
            proxyPort = 0;
        }

        return preferences with
        {
            Language = language,
            CatalogLibraryUrl = NormalizeCatalogLibraryUrl(preferences.CatalogLibraryUrl),
            AiEndpoint = NormalizeAiEndpoint(preferences.AiEndpoint),
            AiModel = NormalizeAiModel(preferences.AiModel),
            AiApiKey = string.IsNullOrEmpty(preferences.AiApiKey) ? null : preferences.AiApiKey,
            DownloadCacheDirectory = NormalizeOptionalPath(preferences.DownloadCacheDirectory, nameof(preferences.DownloadCacheDirectory)),
            InstallRootDirectory = NormalizeOptionalPath(preferences.InstallRootDirectory, nameof(preferences.InstallRootDirectory)),
            Aria2ExecutablePath = NormalizeExecutablePath(preferences.Aria2ExecutablePath),
            XgetInstance = NormalizeXgetInstance(preferences.XgetInstance),
            ProxyHost = proxyHost,
            ProxyPort = proxyPort,
            ProxyUserName = proxyKind == ProxyKind.None ? null : preferences.ProxyUserName?.Trim(),
            ProxyPassword = proxyKind == ProxyKind.None || string.IsNullOrEmpty(preferences.ProxyPassword)
                ? null : preferences.ProxyPassword,
        };
    }

    public static string? NormalizeCatalogLibraryUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.Length > 2048 || candidate.Any(char.IsControl) || candidate.Contains('\\')
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/blob/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The catalog requires an HTTPS JSON direct URL without credentials or fragment.", nameof(AppPreferences.CatalogLibraryUrl));
        return uri.AbsoluteUri;
    }

    public static string NormalizeAiEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048
            || endpoint.Any(char.IsControl))
            throw new ArgumentException("An AI endpoint of at most 2048 characters is required.", nameof(AppPreferences.AiEndpoint));
        var candidate = endpoint.Trim();
        if (candidate.Contains('\\') || candidate.Contains('@') || candidate.Contains('?') || candidate.Contains('#')
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri)))
            throw new ArgumentException("AI endpoint must use HTTPS, or loopback HTTP, without credentials, query or fragment.", nameof(AppPreferences.AiEndpoint));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool IsLoopbackHost(Uri uri) =>
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    public static string NormalizeAiModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl))
            throw new ArgumentException("An AI model of at most 200 characters is required.", nameof(AppPreferences.AiModel));
        return model.Trim();
    }

    private static string NormalizeProxyHost(string host)
    {
        if (host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
            throw new ArgumentException("Proxy host must be a DNS name or IP address without credentials, scheme, port or path.", nameof(AppPreferences.ProxyHost));
        if (host.Contains('[') || host.Contains(']'))
        {
            if (!host.StartsWith('[') || !host.EndsWith(']')
                || host.Count(c => c == '[') != 1 || host.Count(c => c == ']') != 1)
                throw new ArgumentException("Proxy host must not include a port.", nameof(AppPreferences.ProxyHost));
            var unwrapped = host[1..^1];
            if (IPAddress.TryParse(unwrapped, out var ipv6) && ipv6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return unwrapped;
        }
        else if (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns)
            return host;
        throw new ArgumentException("Proxy host must be a DNS name or IP address.", nameof(AppPreferences.ProxyHost));
    }

    /// <summary>Normalize absolute paths without removing drive or UNC root separators.</summary>
    private static string? NormalizeOptionalPath(string? path, string parameterName)
    {
        if (path is null) return null;
        RejectControlCharacters(path, parameterName);
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            throw new ArgumentException("Path must be absolute.", parameterName);
        if (OperatingSystem.IsWindows() && (trimmed.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
            || trimmed.IndexOf(':', 2) >= 0 || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new ArgumentException("Path contains invalid characters or a device prefix.", parameterName);
        var fullPath = Path.GetFullPath(trimmed);
        if (OperatingSystem.IsWindows() && fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var components = fullPath[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length < 2)
                throw new ArgumentException("UNC paths must include a server and share.", parameterName);
        }
        var root = Path.GetPathRoot(fullPath)!;
        var withoutEndingSeparators = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(withoutEndingSeparators,
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return withoutEndingSeparators;
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        var normalized = NormalizeOptionalPath(path, nameof(AppPreferences.Aria2ExecutablePath));
        if (normalized is null) return null;
        if (!Path.GetExtension(normalized).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(normalized).Length == 0
            || Path.EndsInDirectorySeparator(path!.Trim()))
            throw new ArgumentException("aria2 must be an absolute .exe file path.", nameof(AppPreferences.Aria2ExecutablePath));
        return normalized;
    }

    private static string? NormalizeXgetInstance(string? instance)
    {
        if (instance is null) return null;
        RejectControlCharacters(instance, nameof(AppPreferences.XgetInstance));
        if (string.IsNullOrWhiteSpace(instance)) return null;
        var candidate = instance.Trim();
        if (candidate.Contains('\\') || candidate.Contains('@') || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString() || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || candidate.Contains('?') || candidate.Contains('#') || uri.AbsolutePath != "/")
            throw new ArgumentException("Xget instance must be an HTTPS root address without user information, query or fragment.", nameof(AppPreferences.XgetInstance));
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static void RejectControlCharacters(string value, string parameterName)
    {
        if (value.Any(char.IsControl))
            throw new ArgumentException("Control characters are not allowed.", parameterName);
    }

    private void NotifyChanged()
    {
        // A listener failure must not turn a committed settings file into a failed save.
        if (Changed is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
    }
}
