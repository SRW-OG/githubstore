using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GitHubStore.App.Services.Acceleration;
using GitHubStore.App.Services.Download;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>所有 HTTP 响应均由内存 handler 产生；aria2 用不存在的绝对路径，绝不启动真实进程。</summary>
[Trait("Category", "Unit")]
public sealed class DownloadRegressionTests
{
    private static readonly Uri Source = new("https://github.com/owner/repo/releases/download/v1/app.zip");
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Range416_DisposesResponseAndRetriesWithoutRange_WithoutPublishingErrorBody()
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await SeedPartAsync(request);
        var calls = 0;
        var disposed = false;
        using var client = Client(message =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(3, message.Headers.Range!.Ranges.Single().From);
                Assert.Equal("\"v1\"", message.Headers.IfRange!.EntityTag!.ToString());
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                { Content = new DisposeObservedContent("416 error page", () => disposed = true) };
            }
            Assert.True(disposed);
            Assert.Null(message.Headers.Range);
            Assert.Null(message.Headers.IfRange);
            return Body("abcdef");
        });

        var result = await new HttpDownloadService(client).DownloadAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(2, calls);
        Assert.Equal("abcdef", await File.ReadAllTextAsync(request.TargetPath));
        Assert.False(File.Exists(request.TargetPath + ".part"));
        Assert.False(File.Exists(request.TargetPath + ".part.json"));
    }

    [Theory]
    [InlineData(416)]
    [InlineData(206)]
    public async Task RepeatedInvalidRangeResponse_IsBoundedAndPreservesCompleteTarget(int status)
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await SeedPartAsync(request);
        await File.WriteAllTextAsync(request.TargetPath, "previous complete file");
        var calls = 0;
        using var client = Client(message =>
        {
            calls++;
            if (calls == 2) Assert.Null(message.Headers.Range);
            return Body("error page", status: (HttpStatusCode)status);
        });

        var result = await new HttpDownloadService(client).DownloadAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadErrorCodes.InvalidResponse, result.ErrorCode);
        Assert.Equal(2, calls);
        Assert.Equal("previous complete file", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("total")]
    [InlineData("end")]
    [InlineData("length")]
    [InlineData("unknown_total")]
    [InlineData("etag")]
    public async Task Incorrect206_RetriesFullGet_AndSendsIfRange(string defect)
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await SeedPartAsync(request);
        var calls = 0;
        using var client = Client(message =>
        {
            calls++;
            if (calls == 2)
            {
                Assert.Null(message.Headers.Range);
                Assert.Null(message.Headers.IfRange);
                return Body("ABCDEF");
            }
            Assert.Equal(3, message.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"v1\"", message.Headers.IfRange!.EntityTag!.ToString());
            var response = Body("def", status: HttpStatusCode.PartialContent, etag: defect == "etag" ? "\"v2\"" : "\"v1\"");
            response.Content.Headers.ContentRange = defect switch
            {
                "start" => new ContentRangeHeaderValue(2, 5, 6),
                "total" => new ContentRangeHeaderValue(3, 6, 7),
                "end" => new ContentRangeHeaderValue(3, 4, 6),
                "unknown_total" => new ContentRangeHeaderValue(3, 5),
                _ => new ContentRangeHeaderValue(3, 5, 6),
            };
            if (defect == "length") response.Content.Headers.ContentLength = 4;
            return response;
        });

        var result = await new HttpDownloadService(client).DownloadAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(2, calls);
        Assert.Equal("ABCDEF", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Fact]
    public async Task Valid206_AppendsOnlyVerifiedEntityAndPublishesVerifiedHash()
    {
        using var directory = new TempDirectory();
        var request = Request(directory) with { ExpectedSize = 6, ExpectedSha256 = "sha256:" + Hash("abcdef") };
        await SeedPartAsync(request);
        using var client = Client(message =>
        {
            Assert.Equal(3, message.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"v1\"", message.Headers.IfRange!.EntityTag!.ToString());
            var response = Body("def", status: HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });

        var result = await new HttpDownloadService(client).DownloadAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(6, result.BytesWritten);
        Assert.Equal("abcdef", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Fact]
    public async Task LastModifiedWithoutStrongEtag_UsesDateIfRange()
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await SeedPartAsync(request, etag: "W/\"weak\"", modified: Modified);
        using var client = Client(message =>
        {
            Assert.Equal(Modified, message.Headers.IfRange!.Date);
            Assert.Null(message.Headers.IfRange.EntityTag);
            var response = Body("def", status: HttpStatusCode.PartialContent, etag: null);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            response.Content.Headers.LastModified = Modified;
            return response;
        });

        Assert.True((await new HttpDownloadService(client).DownloadAsync(request)).Succeeded);
        Assert.Equal("abcdef", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Fact]
    public async Task ChangedEntityReturning200_ReplacesPartInsteadOfAppending()
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await SeedPartAsync(request);
        using var client = Client(message =>
        {
            Assert.NotNull(message.Headers.IfRange);
            return Body("NEW", etag: "\"v2\"");
        });

        Assert.True((await new HttpDownloadService(client).DownloadAsync(request)).Succeeded);
        Assert.Equal("NEW", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("weak_etag")]
    [InlineData("unknown_length")]
    public async Task UnreliableMetadata_ForcesFullDownload(string state)
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        if (state is "weak_etag" or "unknown_length")
        {
            if (state == "weak_etag") await SeedPartAsync(request, etag: "W/\"weak\"");
            else
            {
                // 明确构造无总长 sidecar：有验证器仍不足以安全续传。
                await File.WriteAllTextAsync(request.TargetPath + ".part", "abc");
                await File.WriteAllTextAsync(request.TargetPath + ".part.json",
                    System.Text.Json.JsonSerializer.Serialize(new { Source = Source.AbsoluteUri, ETag = "\"v1\"", TotalLength = (long?)null }));
            }
        }
        else
        {
            await File.WriteAllTextAsync(request.TargetPath + ".part", "old partial");
            if (state == "corrupt") await File.WriteAllTextAsync(request.TargetPath + ".part.json", "{broken");
        }
        using var client = Client(message =>
        {
            Assert.Null(message.Headers.Range);
            Assert.Null(message.Headers.IfRange);
            return Body("new whole file");
        });

        Assert.True((await new HttpDownloadService(client).DownloadAsync(request)).Succeeded);
        Assert.Equal("new whole file", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Fact]
    public async Task SameNameChangedSource_CannotAppendOtherSourcesPartialFile()
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await SeedPartAsync(request);
        request = request with { Source = new Uri("https://other.example/asset.zip") };
        using var client = Client(message =>
        {
            Assert.Equal(request.Source, message.RequestUri);
            Assert.Null(message.Headers.Range);
            return Body("UVWXYZ");
        });

        Assert.True((await new HttpDownloadService(client).DownloadAsync(request)).Succeeded);
        Assert.Equal("UVWXYZ", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("expected_size")]
    [InlineData("sha256")]
    public async Task InvalidCompletePayload_PreservesExistingTarget(string defect)
    {
        using var directory = new TempDirectory();
        var request = Request(directory);
        await File.WriteAllTextAsync(request.TargetPath, "known complete target");
        request = defect switch
        {
            "expected_size" => request with { ExpectedSize = 9 },
            "sha256" => request with { ExpectedSha256 = Hash("different") },
            _ => request,
        };
        using var client = Client(_ => Body("abcdef", declaredLength: defect switch { "truncated" => 9, "oversized" => 3, _ => 6 }));

        var result = await new HttpDownloadService(client).DownloadAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(defect == "sha256" ? DownloadErrorCodes.ChecksumMismatch : DownloadErrorCodes.LengthMismatch, result.ErrorCode);
        Assert.Equal("known complete target", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Fact]
    public async Task UnknownResponseLength_IsCheckedAgainstAssetSize()
    {
        using var directory = new TempDirectory();
        var request = Request(directory) with { ExpectedSize = 9 };
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableReadStream("abc")) });

        var result = await new HttpDownloadService(client).DownloadAsync(request);

        Assert.Equal(DownloadErrorCodes.LengthMismatch, result.ErrorCode);
        Assert.False(File.Exists(request.TargetPath));
    }

    [Fact]
    public async Task Cancellation_PreservesResumablePartAndNeverReportsSuccess()
    {
        using var directory = new TempDirectory();
        var request = Request(directory) with { ExpectedSize = 6 };
        using var cancellation = new CancellationTokenSource();
        await File.WriteAllTextAsync(request.TargetPath, "old complete target");
        using var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new CancelAfterPrefixStream("abc", cancellation)) };
            response.Content.Headers.ContentLength = 6;
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });

        var result = await new HttpDownloadService(client).DownloadAsync(request, ct: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadErrorCodes.Cancelled, result.ErrorCode);
        Assert.Equal("abc", await File.ReadAllTextAsync(request.TargetPath + ".part"));
        Assert.True(File.Exists(request.TargetPath + ".part.json"));
        Assert.Equal("old complete target", await File.ReadAllTextAsync(request.TargetPath));
        using var retry = Client(message =>
        {
            Assert.Equal(3, message.Headers.Range!.Ranges.Single().From);
            Assert.NotNull(message.Headers.IfRange);
            var response = Body("def", status: HttpStatusCode.PartialContent);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        var resumedResult = await new HttpDownloadService(retry).DownloadAsync(request);
        Assert.True(resumedResult.Succeeded, resumedResult.ErrorCode);
        Assert.Equal("abcdef", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Theory]
    [InlineData("relative.zip", "https://github.com/owner/file", DownloadErrorCodes.NoTargetDirectory)]
    [InlineData("absolute", "credential-source", DownloadErrorCodes.UnsupportedSource)]
    [InlineData("absolute", "http://github.com/owner/file", DownloadErrorCodes.UnsupportedSource)]
    public async Task InvalidInputs_AreRejectedBeforeHttp(string target, string uri, string error)
    {
        using var directory = new TempDirectory();
        using var client = Client(_ => throw new InvalidOperationException("HTTP must not run"));
        var source = uri == "credential-source" ? CredentialSource() : new Uri(uri);
        var request = Request(directory) with { Source = source, TargetPath = target == "absolute" ? directory.Combine("file.zip") : target };
        Assert.Equal(error, (await new HttpDownloadService(client).DownloadAsync(request)).ErrorCode);
    }

    [Theory]
    [InlineData("md5:abc")]
    [InlineData("")]
    [InlineData("sha256:not-a-hash")]
    public async Task InvalidExpectedDigest_IsRejectedBeforeHttp(string digest)
    {
        using var directory = new TempDirectory();
        using var client = Client(_ => throw new InvalidOperationException("HTTP must not run"));
        var result = await new HttpDownloadService(client).DownloadAsync(Request(directory) with { ExpectedSha256 = digest });
        Assert.Equal(DownloadErrorCodes.InvalidVerification, result.ErrorCode);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/releases/download/v1/a.zip?x=1", true)]
    [InlineData("https://raw.githubusercontent.com/owner/repo/main/a", false)]
    [InlineData("https://objects.githubusercontent.com/path", false)]
    [InlineData("https://codeload.github.com/owner/repo/zip/main", false)]
    [InlineData("http://github.com/owner/repo", false)]
    [InlineData("credential-source", false)]
    [InlineData("https://github.com.evil.example/owner/repo", false)]
    [InlineData("https://github.com:8443/owner/repo", false)]
    public void Xget_OnlyRewritesExplicitGitHubHttpsRoute(string address, bool expected)
    {
        var rewriter = new XgetUrlRewriter("https://accelerator.example");
        var source = address == "credential-source" ? CredentialSource() : new Uri(address);
        Assert.Equal(expected, rewriter.TryRewrite(source, out var rewritten));
        Assert.Equal(expected ? "https://accelerator.example/gh/owner/repo/releases/download/v1/a.zip?x=1" : source.AbsoluteUri, rewritten.AbsoluteUri);
        Assert.Equal("https://xget.xi-xu.me", XgetUrlRewriter.DefaultInstance);
    }

    [Fact]
    public async Task AcceleratorFailure_RetriesOriginOnce_WithoutMixingPartialSources()
    {
        using var directory = new TempDirectory();
        var request = Request(directory) with { UseAccelerator = true };
        var calls = 0;
        using var client = Client(message =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("accelerator.example", message.RequestUri!.Host);
                return Body("abc", declaredLength: 6);
            }
            Assert.Equal(Source, message.RequestUri);
            Assert.Null(message.Headers.Range);
            Assert.Null(message.Headers.IfRange);
            return Body("UVWXYZ");
        });
        var service = new CompositeDownloadService(client, directory.Combine("missing-aria2.exe"), "https://accelerator.example");

        var result = await service.DownloadAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(Source, result.EffectiveUri);
        Assert.Equal(2, calls);
        Assert.Equal("UVWXYZ", await File.ReadAllTextAsync(request.TargetPath));
    }

    [Fact]
    public async Task AcceleratorAndOriginFailures_OnlyMakeTwoRequests()
    {
        using var directory = new TempDirectory();
        var calls = 0;
        using var client = Client(_ => { calls++; return Body("error", status: HttpStatusCode.BadGateway); });
        var result = await new CompositeDownloadService(client, acceleratorBase: "https://accelerator.example")
            .DownloadAsync(Request(directory) with { UseAccelerator = true });
        Assert.False(result.Succeeded);
        Assert.Equal(2, calls);
        Assert.False(File.Exists(directory.Combine("app.zip")));
    }

    [Fact]
    public async Task AcceleratorCancellation_DoesNotRetryOrigin()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var result = await new CompositeDownloadService(client, acceleratorBase: "https://accelerator.example")
            .DownloadAsync(Request(directory) with { UseAccelerator = true }, ct: cancellation.Token);
        Assert.Equal(DownloadErrorCodes.Cancelled, result.ErrorCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MissingAria2_DoesNotLaunchProcessOrTreatOldTargetAsSuccess()
    {
        using var directory = new TempDirectory();
        var request = Request(directory) with { Engine = DownloadEngine.Aria2 };
        await File.WriteAllTextAsync(request.TargetPath, "old complete target");
        await File.WriteAllTextAsync(request.TargetPath + ".aria2", "old control file");
        var runner = new Aria2ProcessRunner(directory.Combine("definitely-missing-aria2.exe"));
        Assert.False(await runner.ProbeAsync());
        var result = await runner.DownloadAsync(request, null, false, null, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(DownloadErrorCodes.Aria2NotFound, result.ErrorCode);
        Assert.Equal("old complete target", await File.ReadAllTextAsync(request.TargetPath));
        using var client = Client(_ => throw new InvalidOperationException("No HTTP fallback for explicit missing engine"));
        var composite = new CompositeDownloadService(client, runner.ExecutablePath);
        Assert.Equal(DownloadErrorCodes.Aria2NotFound, (await composite.DownloadAsync(request)).ErrorCode);
    }

    [Fact]
    public async Task ProbeCancellation_PropagatesEvenWhenExecutableIsMissing()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new Aria2ProcessRunner(directory.Combine("missing-aria2.exe"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ProbeAsync(cancellation.Token));
    }

    [Fact]
    public async Task PeerSource_ReturnsIndependentNotSupportedWithoutLaunchingAnything()
    {
        using var directory = new TempDirectory();
        using var client = Client(_ => throw new InvalidOperationException("P2P must not use HTTP"));
        var result = await new CompositeDownloadService(client).DownloadAsync(Request(directory) with { PeerSource = "magnet:?xt=urn:btih:abc" });
        Assert.False(result.Succeeded);
        Assert.Equal(DownloadErrorCodes.PeerNotSupported, result.ErrorCode);
        Assert.False(File.Exists(directory.Combine("app.zip")));
    }

    [Fact]
    public void Aria2Diagnostics_RedactCredentialsAndSourceTokens()
    {
        var proxy = new UriBuilder("http", "proxy.example", 8080) { UserName = "fixture-user", Password = "fixture-password" }.Uri;
        var source = new UriBuilder(CredentialSource()) { Query = "token=secret-token" }.Uri;
        var command = Aria2ProcessRunner.SanitizeCommandLine("aria2c", [
            "--all-proxy=" + proxy.AbsoluteUri,
            "--all-proxy-user=other-user", "--all-proxy-passwd=other-password",
            source.AbsoluteUri,
        ]);
        foreach (var secret in new[] { "fixture-user", "fixture-password", "other-user", "other-password", "secret-token" })
            Assert.DoesNotContain(secret, command, StringComparison.Ordinal);
        Assert.Contains("http://proxy.example:8080", command, StringComparison.Ordinal);
    }

    private static Uri CredentialSource() => new UriBuilder(Source) { UserName = "fixture-user", Password = "fixture-password" }.Uri;
    private static DownloadRequest Request(TempDirectory directory) => new() { Source = Source, TargetPath = directory.Combine("app.zip") };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new StubHttpHandler(respond));

    private static HttpResponseMessage Body(string text, long? declaredLength = null, HttpStatusCode status = HttpStatusCode.OK, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
        response.Content.Headers.ContentLength = declaredLength ?? Encoding.UTF8.GetByteCount(text);
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static async Task SeedPartAsync(DownloadRequest request, string? etag = "\"v1\"", DateTimeOffset? modified = null)
    {
        using var client = Client(_ =>
        {
            var response = Body("abc", declaredLength: 6, etag: etag);
            response.Content.Headers.LastModified = modified;
            return response;
        });
        var result = await new HttpDownloadService(client).DownloadAsync(request);
        Assert.Equal(DownloadErrorCodes.LengthMismatch, result.ErrorCode);
        Assert.Equal("abc", await File.ReadAllTextAsync(request.TargetPath + ".part"));
    }

    private sealed class DisposeObservedContent : ByteArrayContent
    {
        private readonly Action _onDispose;
        public DisposeObservedContent(string text, Action onDispose) : base(Encoding.UTF8.GetBytes(text)) => _onDispose = onDispose;
        protected override void Dispose(bool disposing) { if (disposing) _onDispose(); base.Dispose(disposing); }
    }

    private class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableReadStream(string value) => _inner = new MemoryStream(Encoding.UTF8.GetBytes(value));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CancelAfterPrefixStream : NonSeekableReadStream
    {
        private readonly CancellationTokenSource _cancellation;
        private bool _read;
        public CancelAfterPrefixStream(string prefix, CancellationTokenSource cancellation) : base(prefix) => _cancellation = cancellation;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) { _cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            _read = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
