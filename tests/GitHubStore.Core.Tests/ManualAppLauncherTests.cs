using System.Text.Json.Nodes;
using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ManualAppLauncherTests
{
    [Fact]
    public async Task SaveAndReloadPreservesIdentityCategoryAndTimestampWithoutManagedRegistration()
    {
        using var fixture = new Fixture();
        var path = fixture.Target("tool.exe");
        var category = new CategoryId("development");
        var added = await fixture.Launcher().AddAsync(path, "My tool", category);
        var reloaded = Assert.Single(await fixture.Launcher().ListAsync());
        Assert.Equal(added, reloaded);
        Assert.Equal(path, reloaded.Path);
        Assert.Equal(category, reloaded.Category);
        Assert.True(reloaded.IsAvailable);
        Assert.NotEqual(default, reloaded.AddedAt);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.Id));
        var managed = new ManagedAppLauncher(fixture.DataDirectory, fixture.ManagedRoot);
        Assert.Empty(await managed.ListAsync());
        var inventory = new GitHubStore.App.Services.Inventory.ManagedInstalledProgramService(managed);
        Assert.Empty(await inventory.ScanAsync());
        Assert.False(File.Exists(Path.Combine(fixture.DataDirectory, "managed-apps.json")));
        Assert.Empty(Directory.GetFiles(fixture.DataDirectory, "*.tmp"));
    }

    [Fact]
    public async Task UncategorizedEntryRoundTrips()
    {
        using var fixture = new Fixture();
        await fixture.Launcher().AddAsync(fixture.Target("tool.lnk"), "Shortcut");
        Assert.Null(Assert.Single(await fixture.Launcher().ListAsync()).Category);
    }

    [Fact]
    public async Task PathsAreDeduplicatedIgnoringCaseAcrossServiceInstances()
    {
        using var fixture = new Fixture();
        var path = fixture.Target("tool.exe");
        var original = await fixture.Launcher().AddAsync(path, "Original", new CategoryId("tools"));
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        var duplicate = await fixture.Launcher().AddAsync(path.ToUpperInvariant(), "Another", new CategoryId("other"));
        Assert.Equal(original, duplicate);
        Assert.Single(await fixture.Launcher().ListAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    [Fact]
    public async Task ConcurrentAddsFromDifferentInstancesDoNotLoseEntries()
    {
        using var fixture = new Fixture();
        var first = fixture.Target("first.exe");
        var second = fixture.Target("second.exe");
        await Task.WhenAll(fixture.Launcher().AddAsync(first, "First"), fixture.Launcher().AddAsync(second, "Second"));
        Assert.Equal(2, (await fixture.Launcher().ListAsync()).Count);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData(@"C:tool.exe")]
    [InlineData(@"\tool.exe")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData("//server/share/tool.lnk")]
    [InlineData(@"\\?\C:\tool.exe")]
    [InlineData(@"C:\tools\tool.exe:stream")]
    [InlineData(@"C:\tools\..\tool.exe")]
    [InlineData(@"C:\tools\NUL.exe")]
    [InlineData("")]
    public async Task InvalidAndNonlocalPathsAreRejected(string path)
    {
        using var fixture = new Fixture();
        Assert.Equal("manual_launcher_invalid_path", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().AddAsync(path, "Tool"))).Code);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Theory]
    [InlineData("tool.cmd")]
    [InlineData("tool.bat")]
    [InlineData("tool.ps1")]
    [InlineData("tool.url")]
    [InlineData("tool.txt")]
    [InlineData("tool.msi")]
    public async Task ExistingUnsupportedTypesAreRejected(string name)
    {
        using var fixture = new Fixture();
        Assert.Equal("manual_launcher_invalid_path", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().AddAsync(fixture.Target(name), "Tool"))).Code);
    }

    [Fact]
    public async Task MissingFilesAndDirectoriesCannotBeAdded()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "directory.exe");
        Directory.CreateDirectory(directory);
        foreach (var path in new[] { directory, Path.Combine(fixture.Root, "absent.exe") })
            Assert.Equal("manual_launcher_entry_missing", (await Assert.ThrowsAsync<LauncherException>(
                () => fixture.Launcher().AddAsync(path, "Tool"))).Code);
        Assert.False(File.Exists(fixture.Manifest));
    }

    [Theory]
    [InlineData("", null, "manual_launcher_invalid_display_name")]
    [InlineData("Tool\nInjected", null, "manual_launcher_invalid_display_name")]
    [InlineData("Tool", "", "manual_launcher_invalid_category")]
    public async Task InvalidMetadataCannotBePersisted(string name, string? category, string code)
    {
        using var fixture = new Fixture();
        Assert.Equal(code, (await Assert.ThrowsAsync<LauncherException>(() => fixture.Launcher().AddAsync(
            fixture.Target("tool.exe"), name, category is null ? null : new CategoryId(category)))).Code);
    }

    [Fact]
    public async Task MissingEntryRemainsUnavailableAndCanLaunchAgainAfterRestoration()
    {
        using var fixture = new Fixture();
        var path = fixture.Target("tool.exe");
        var starts = 0;
        var launcher = fixture.Launcher((_, _) => { starts++; return true; });
        var added = await launcher.AddAsync(path, "Tool");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        File.Delete(path);
        var missing = Assert.Single(await fixture.Launcher().ListAsync());
        Assert.Equal(added.Id, missing.Id);
        Assert.False(missing.IsAvailable);
        Assert.Equal("manual_launcher_entry_missing", (await launcher.LaunchAsync(added.Id)).ErrorCode);
        Assert.Equal(0, starts);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        await File.WriteAllTextAsync(path, "restored");
        Assert.True(Assert.Single(await launcher.ListAsync()).IsAvailable);
        Assert.True((await launcher.LaunchAsync(added.Id)).Started);
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[],\"unexpected\":true}")]
    public async Task CorruptIndexIsNeverOverwrittenByAddRemoveOrLaunch(string contents)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.DataDirectory);
        await File.WriteAllTextAsync(fixture.Manifest, contents);
        var launcher = fixture.Launcher((_, _) => throw new Exception("must not launch"));
        Assert.Equal("manual_launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => launcher.ListAsync())).Code);
        Assert.Equal("manual_launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(
            () => launcher.AddAsync(fixture.Target("tool.exe"), "Tool"))).Code);
        Assert.Equal("manual_launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() => launcher.RemoveAsync("id"))).Code);
        Assert.Equal("manual_launcher_invalid_manifest", (await launcher.LaunchAsync("id")).ErrorCode);
        Assert.Equal(contents, await File.ReadAllTextAsync(fixture.Manifest));
    }

    [Fact]
    public async Task DuplicatePersistedPathsInvalidateTheIndexRatherThanDroppingRecords()
    {
        using var fixture = new Fixture();
        await fixture.Launcher().AddAsync(fixture.Target("tool.exe"), "Tool");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Manifest))!;
        var entries = json["entries"]!.AsArray();
        var duplicate = entries[0]!.DeepClone();
        duplicate["id"] = "another-id";
        duplicate["path"] = duplicate["path"]!.GetValue<string>().ToUpperInvariant();
        entries.Add(duplicate);
        var contents = json.ToJsonString();
        await File.WriteAllTextAsync(fixture.Manifest, contents);
        Assert.Equal("manual_launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(
            () => fixture.Launcher().RemoveAsync("another-id"))).Code);
        Assert.Equal(contents, await File.ReadAllTextAsync(fixture.Manifest));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("lnk")]
    public async Task LaunchUsesSavedIdAndLeavesShortcutArgumentsAndDirectoryToTheShell(string extension)
    {
        using var fixture = new Fixture();
        var path = fixture.Target("tool." + extension);
        string? receivedPath = null;
        string? receivedDirectory = null;
        var launcher = fixture.Launcher((target, directory) => { receivedPath = target; receivedDirectory = directory; return true; });
        var added = await launcher.AddAsync(path, "Tool");
        Assert.Equal("manual_launcher_not_found", (await launcher.LaunchAsync(path)).ErrorCode);
        Assert.Null(receivedPath);
        Assert.True((await launcher.LaunchAsync(added.Id)).Started);
        Assert.Equal(path, receivedPath);
        Assert.Equal(extension == "lnk" ? string.Empty : Path.GetDirectoryName(path), receivedDirectory);
        await fixture.Launcher().RemoveAsync(added.Id);
        receivedPath = null;
        Assert.Equal("manual_launcher_not_found", (await launcher.LaunchAsync(added.Id)).ErrorCode);
        Assert.Null(receivedPath);
    }

    [Fact]
    public async Task LaunchRereadsThePersistedPathInsteadOfUsingCachedEntries()
    {
        using var fixture = new Fixture();
        var first = fixture.Target("first.exe");
        var second = fixture.Target("second.exe");
        string? received = null;
        var launcher = fixture.Launcher((path, _) => { received = path; return true; });
        var entry = await launcher.AddAsync(first, "First");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Manifest))!;
        json["entries"]![0]!["path"] = second;
        await File.WriteAllTextAsync(fixture.Manifest, json.ToJsonString());
        Assert.True((await launcher.LaunchAsync(entry.Id)).Started);
        Assert.Equal(second, received);
    }

    [Fact]
    public async Task RemoveDeletesOnlyTheEntryAndPreservesTargetAndNeighborFiles()
    {
        using var fixture = new Fixture();
        var first = fixture.Target("first.lnk");
        var second = fixture.Target("second.exe");
        var userData = fixture.Target("settings.json");
        var launcher = fixture.Launcher();
        var entry = await launcher.AddAsync(first, "First");
        var other = await launcher.AddAsync(second, "Second");
        await launcher.RemoveAsync(entry.Id);
        await launcher.RemoveAsync("unknown");
        Assert.Equal(other.Id, Assert.Single(await fixture.Launcher().ListAsync()).Id);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal("test target", await File.ReadAllTextAsync(userData));
    }

    [Fact]
    public async Task EmptyIndexDoesNotCreateFilesOrDiscoverTargets()
    {
        using var fixture = new Fixture();
        fixture.Target("existing.exe");
        var launcher = fixture.Launcher();
        Assert.Empty(await launcher.ListAsync());
        await launcher.RemoveAsync("unknown");
        Assert.Equal("manual_launcher_not_found", (await launcher.LaunchAsync("unknown")).ErrorCode);
        Assert.False(Directory.Exists(fixture.DataDirectory));
    }

    [Fact]
    public async Task StarterFailuresExposeOnlyStableErrorCodes()
    {
        using var fixture = new Fixture();
        var added = await fixture.Launcher().AddAsync(fixture.Target("tool.exe"), "Tool");
        foreach (var starter in new Func<string, string, bool>[] { (_, _) => false, (_, _) => throw new Exception("private process detail") })
        {
            var result = await fixture.Launcher(starter).LaunchAsync(added.Id);
            Assert.False(result.Started);
            Assert.Equal("manual_launcher_start_failed", result.ErrorCode);
        }
        Assert.Equal("manual_launcher_invalid_id", (await fixture.Launcher().LaunchAsync(" ")).ErrorCode);
        Assert.Equal("manual_launcher_invalid_id", (await Assert.ThrowsAsync<LauncherException>(() => fixture.Launcher().RemoveAsync(" "))).Code);
    }

    [Fact]
    public async Task CancellationDoesNotReplaceTheIndex()
    {
        using var fixture = new Fixture();
        await fixture.Launcher().AddAsync(fixture.Target("first.exe"), "First");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Launcher().AddAsync(
            fixture.Target("second.exe"), "Second", ct: cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    [Fact]
    public async Task UpdatePreservesIdentityAndAddedTimeWhileReplacingPathAndProperties()
    {
        using var fixture = new Fixture();
        var originalPath = fixture.Target("old.exe");
        var replacementPath = fixture.Target("new.lnk");
        var original = await fixture.Launcher().AddAsync(originalPath, "Old", new CategoryId("system"));
        var updated = await fixture.Launcher().UpdateAsync(original.Id, replacementPath, "New", new CategoryId("dev-tools"));
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.AddedAt, updated.AddedAt);
        Assert.Equal(replacementPath, updated.Path);
        Assert.Equal("New", updated.DisplayName);
        Assert.Equal(new CategoryId("dev-tools"), updated.Category);
        Assert.True(updated.IsAvailable);
        Assert.Equal(updated, Assert.Single(await fixture.Launcher().ListAsync()));
        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(replacementPath));
        string? launched = null;
        Assert.True((await fixture.Launcher((path, _) => { launched = path; return true; }).LaunchAsync(original.Id)).Started);
        Assert.Equal(replacementPath, launched);
    }

    [Fact]
    public async Task MissingEntryCanBeRenamedAndUncategorizedBeforeRepairWithoutChangingItsPath()
    {
        using var fixture = new Fixture();
        var path = fixture.Target("tool.exe");
        var original = await fixture.Launcher().AddAsync(path, "Old", new CategoryId("system"));
        File.Delete(path);
        var updated = await fixture.Launcher().UpdateAsync(original.Id, path.ToUpperInvariant(), "Renamed", null);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.AddedAt, updated.AddedAt);
        Assert.Equal(original.Path, updated.Path);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Null(updated.Category);
        Assert.False(updated.IsAvailable);
        Assert.Equal(updated, Assert.Single(await fixture.Launcher().ListAsync()));
    }

    [Fact]
    public async Task UpdateRejectsAnotherEntriesPathIgnoringCaseAndDoesNotChangeEitherEntry()
    {
        using var fixture = new Fixture();
        var first = await fixture.Launcher().AddAsync(fixture.Target("first.exe"), "First");
        var second = await fixture.Launcher().AddAsync(fixture.Target("second.lnk"), "Second");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        Assert.Equal("manual_launcher_duplicate_path", (await Assert.ThrowsAsync<LauncherException>(() =>
            fixture.Launcher().UpdateAsync(first.Id, second.Path.ToUpperInvariant(), "Renamed", null))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
        Assert.Equal(new[] { first, second }, await fixture.Launcher().ListAsync());
    }

    [Theory]
    [InlineData("missing", "manual_launcher_entry_missing")]
    [InlineData("relative", "manual_launcher_invalid_path")]
    [InlineData("unc", "manual_launcher_invalid_path")]
    [InlineData("type", "manual_launcher_invalid_path")]
    [InlineData("name", "manual_launcher_invalid_display_name")]
    [InlineData("category", "manual_launcher_invalid_category")]
    [InlineData("id", "manual_launcher_invalid_id")]
    [InlineData("unknown", "manual_launcher_not_found")]
    public async Task FailedUpdateNeverReplacesTheSavedIndex(string fault, string code)
    {
        using var fixture = new Fixture();
        var original = await fixture.Launcher().AddAsync(fixture.Target("original.exe"), "Original");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        var path = fault switch
        {
            "missing" => Path.Combine(fixture.Root, "absent.exe"),
            "relative" => "relative.exe",
            "unc" => @"\\server\share\tool.exe",
            "type" => fixture.Target("script.cmd"),
            _ => original.Path,
        };
        var id = fault == "id" ? " " : fault == "unknown" ? "unknown-id" : original.Id;
        var name = fault == "name" ? " " : "Renamed";
        CategoryId? category = fault == "category" ? new CategoryId("") : null;
        Assert.Equal(code, (await Assert.ThrowsAsync<LauncherException>(() =>
            fixture.Launcher().UpdateAsync(id, path, name, category))).Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    [Fact]
    public async Task UpdateRefusesToOverwriteCorruptIndex()
    {
        using var fixture = new Fixture();
        var original = await fixture.Launcher().AddAsync(fixture.Target("tool.exe"), "Original");
        const string corrupt = "{broken";
        await File.WriteAllTextAsync(fixture.Manifest, corrupt);
        Assert.Equal("manual_launcher_invalid_manifest", (await Assert.ThrowsAsync<LauncherException>(() =>
            fixture.Launcher().UpdateAsync(original.Id, original.Path, "Renamed", null))).Code);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(fixture.Manifest));
    }

    [Fact]
    public async Task CancelledUpdatePreservesExistingEntry()
    {
        using var fixture = new Fixture();
        var original = await fixture.Launcher().AddAsync(fixture.Target("tool.exe"), "Original");
        var before = await File.ReadAllBytesAsync(fixture.Manifest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Launcher().UpdateAsync(
            original.Id, original.Path, "Renamed", null, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Manifest));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "githubstore-manual-launcher-" + Guid.NewGuid().ToString("N"));
        public string DataDirectory => Path.Combine(Root, "data");
        public string Manifest => Path.Combine(DataDirectory, "manual-launcher.json");
        public string ManagedRoot => Path.Combine(Root, "managed");
        public Fixture() => Directory.CreateDirectory(Root);
        public ManualAppLauncher Launcher(Func<string, string, bool>? starter = null) => new(DataDirectory, starter);
        public string Target(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "test target");
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
