using GitHubStore.App.Services.Launcher;
using GitHubStore.Core.Abstractions;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class LauncherHotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+K", LauncherHotkeyParser.MOD_CONTROL | LauncherHotkeyParser.MOD_ALT, (uint)'K', "Ctrl+Alt+K")]
    [InlineData("shift+win+f5", LauncherHotkeyParser.MOD_SHIFT | LauncherHotkeyParser.MOD_WIN, 0x74u, "Shift+Win+F5")]
    [InlineData("Alt+1", LauncherHotkeyParser.MOD_ALT, (uint)'1', "Alt+1")]
    public void ValidGesturesParseAndNormalize(string text, uint modifiers, uint key, string normalized)
    {
        Assert.True(LauncherHotkeyParser.TryParse(text, out var parsedModifiers, out var parsedKey, out var parsedNormalized));
        Assert.Equal(modifiers, parsedModifiers);
        Assert.Equal(key, parsedKey);
        Assert.Equal(normalized, parsedNormalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("Shift+K")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl+F12")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Space")]
    public void InvalidGesturesAreRejected(string text) =>
        Assert.False(LauncherHotkeyParser.TryParse(text, out _, out _, out _));

    [Fact]
    public void ConflictingShortcutKeepsThePreviousBinding()
    {
        var registrations = new List<uint>();
        using var registry = Create(registrations, out _);
        registry.Attach(1);
        Assert.True(registry.TrySet(false, "first", "Ctrl+Alt+K"));
        Assert.False(registry.TrySet(false, "second", "Ctrl+Alt+K"));
        Assert.Single(registrations);
    }

    [Fact]
    public void SameIdDifferentSourcesAreIndependentAndTriggerCarriesSource()
    {
        var registrations = new List<uint>();
        using var registry = Create(registrations, out _);
        registry.Attach(1);
        Assert.True(registry.TrySet(false, "same", "Ctrl+Alt+K"));
        Assert.True(registry.TrySet(true, "same", "Ctrl+Alt+J"));
        Assert.Equal(2, registrations.Count);
        LauncherHotkeyEventArgs? received = null;
        registry.Triggered += (_, args) => received = args;
        registry.OnHotkey((int)registrations[1]);
        Assert.NotNull(received);
        Assert.True(received!.IsManual);
        Assert.Equal("same", received.EntryId);
    }

    [Fact]
    public void ClearingAndRetainingReleaseOnlyStaleRegistrations()
    {
        var registrations = new List<uint>();
        using var registry = Create(registrations, out var unregistered);
        registry.Attach(1);
        Assert.True(registry.TrySet(false, "keep", "Ctrl+Alt+K"));
        Assert.True(registry.TrySet(false, "drop", "Ctrl+Alt+J"));
        registry.Retain([(false, "keep")]);
        Assert.Single(unregistered);
        Assert.True(registry.TrySet(false, "keep", null));
        Assert.Equal(2, unregistered.Count);
    }

    [Fact]
    public void FailedNativeRegistrationDoesNotReportSuccess()
    {
        using var registry = new LauncherHotkeyRegistry((_, _, _, _) => false, (_, _) => { }, nativeSubclass: false);
        registry.Attach(1);
        Assert.False(registry.TrySet(false, "entry", "Ctrl+Alt+K"));
    }

    private static LauncherHotkeyRegistry Create(List<uint> registrations, out List<uint> unregistered)
    {
        var registered = registrations;
        var released = new List<uint>();
        unregistered = released;
        return new LauncherHotkeyRegistry(
            (_, id, _, _) => { registered.Add(id); return true; },
            (_, id) => released.Add(id),
            nativeSubclass: false);
    }
}
