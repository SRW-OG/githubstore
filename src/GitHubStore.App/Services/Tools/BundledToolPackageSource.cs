using System.Reflection;
using System.Text.Json;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Tools;

/// <summary>Reads only resources embedded in the supplied application assembly.</summary>
public sealed class BundledToolPackageSource : IToolPackageSource
{
    private const int MaxManifestBytes = 1024 * 1024;
    private readonly Assembly _assembly;
    private readonly string? _manifestResourceName;

    public BundledToolPackageSource(Assembly assembly, string? manifestResourceName = null)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _manifestResourceName = manifestResourceName;
    }

    public async Task<BundledToolPackage?> GetPackageAsync(ManagedToolKind kind, string architecture,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (kind != ManagedToolKind.Aria2) return null;
        var name = _manifestResourceName;
        if (name is null)
        {
            var names = _assembly.GetManifestResourceNames()
                .Where(value => value.EndsWith(".tools.manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length == 0) return null;
            if (names.Length != 1) throw new ToolPackageException(ToolErrorCodes.InvalidManifest);
            name = names[0];
        }

        using var input = _assembly.GetManifestResourceStream(name);
        if (input is null) return null;
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (data.Length + read > MaxManifestBytes)
                throw new ToolPackageException(ToolErrorCodes.InvalidManifest);
            await data.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(data.ToArray(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 8,
            });
        }
        catch (JsonException)
        {
            throw new ToolPackageException(ToolErrorCodes.InvalidManifest);
        }
        if (manifest?.Packages is not { Length: > 0 })
            throw new ToolPackageException(ToolErrorCodes.InvalidManifest);
        // One package per architecture avoids silently choosing a version by resource enumeration order.
        var packages = manifest.Packages.Where(package => package is not null &&
            string.Equals(package.Architecture, architecture, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (packages.Length > 1) throw new ToolPackageException(ToolErrorCodes.InvalidManifest);
        return packages.SingleOrDefault();
    }

    public Task<Stream> OpenArchiveAsync(BundledToolPackage package, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var stream = _assembly.GetManifestResourceStream(package.ArchiveResourceName)
            ?? throw new ToolPackageException(ToolErrorCodes.PackageMissing);
        return Task.FromResult(stream);
    }

    private sealed record Manifest(BundledToolPackage[] Packages);
}
