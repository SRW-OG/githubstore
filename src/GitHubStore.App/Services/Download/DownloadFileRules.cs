using System.Security.Cryptography;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Download;

/// <summary>HTTP 和 aria2 共享的纯 BCL 输入、产物及发布规则。</summary>
internal static class DownloadFileRules
{
    internal static bool IsSupportedSource(Uri? source) => source is { IsAbsoluteUri: true }
        && source.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(source.UserInfo);

    internal static string? Validate(DownloadRequest request, out string targetPath)
    {
        targetPath = string.Empty;
        if (!IsSupportedSource(request.Source)) return DownloadErrorCodes.UnsupportedSource;
        if (string.IsNullOrWhiteSpace(request.TargetPath) || !Path.IsPathFullyQualified(request.TargetPath))
            return DownloadErrorCodes.NoTargetDirectory;
        try
        {
            targetPath = Path.GetFullPath(request.TargetPath);
            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || string.IsNullOrEmpty(Path.GetFileName(targetPath)))
                return DownloadErrorCodes.NoTargetDirectory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DownloadErrorCodes.NoTargetDirectory;
        }
        if (request.ExpectedSize is < 0 || !TryNormalizeSha256(request.ExpectedSha256, out _))
            return DownloadErrorCodes.InvalidVerification;
        return null;
    }

    internal static bool TryNormalizeSha256(string? value, out string? hash)
    {
        hash = null;
        if (value is null) return true;
        var candidate = value.Trim();
        if (candidate.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) candidate = candidate[7..];
        if (candidate.Length != 64 || candidate.Any(c => !Uri.IsHexDigit(c))) return false;
        hash = candidate;
        return true;
    }

    internal static async Task<string?> VerifyAsync(string path, DownloadRequest request, long? knownLength, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var length = new FileInfo(path).Length;
        if ((knownLength.HasValue && length != knownLength.Value)
            || (request.ExpectedSize.HasValue && length != request.ExpectedSize.Value))
            return DownloadErrorCodes.LengthMismatch;
        if (!TryNormalizeSha256(request.ExpectedSha256, out var hash)) return DownloadErrorCodes.InvalidVerification;
        if (hash is null) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var actual = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
        return Convert.ToHexString(actual).Equals(hash, StringComparison.OrdinalIgnoreCase)
            ? null : DownloadErrorCodes.ChecksumMismatch;
    }

    // Staging 必须与目标同卷；替换失败不先删除已有完整目标。
    internal static void Publish(string staging, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(staging, target);
            return;
        }

        try
        {
            // 优先原子替换：保留目标身份、失败可回滚。
            File.Replace(staging, target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // ReplaceFile 在部分卷、网络位置或受限令牌下不受支持；
            // 此时退回同卷覆盖移动，仍是一次元数据级替换，不会先删掉已有完整目标。
            File.Move(staging, target, overwrite: true);
        }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
