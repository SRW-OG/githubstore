using System.ComponentModel;
using System.Globalization;
using GitHubStore.App.Services.Localization;
using Xunit;

namespace GitHubStore.Core.Tests;

/// <summary>Serialized: these tests replace the process-wide UI culture while they run.</summary>
[CollectionDefinition("UICulture", DisableParallelization = true)]
public sealed class UiCultureCollection;

[Trait("Category", "Unit")]
[Collection("UICulture")]
public sealed class LocalizationServiceTests
{
    [Fact]
    public void Indexer_ReturnsDifferentTextPerSupportedLanguage()
    {
        var chinese = new LocalizationService("zh-CN");
        var english = new LocalizationService("en-US");

        Assert.Equal("首页", chinese["Home"]);
        Assert.Equal("Home", english["Home"]);
        Assert.Equal("zh-CN", chinese.LanguageCode);
        Assert.False(chinese.IsEnglish);
        Assert.True(english.IsEnglish);
    }

    [Fact]
    public void Indexer_ReturnsTheKeyItselfWhenNoEntryExists()
    {
        var service = new LocalizationService("en-US");

        Assert.Equal("NotARealKey", service["NotARealKey"]);
    }

    [Fact]
    public void EveryCategoryKeyIsLocalizedInBothLanguages()
    {
        string[] categories =
        [
            "dev-tools", "system", "network", "media", "graphics", "office",
            "security", "games", "ai", "terminal", "files", "science"
        ];
        var chinese = new LocalizationService("zh-CN");
        var english = new LocalizationService("en-US");

        foreach (var category in categories)
        {
            var key = $"category.{category}";
            Assert.NotEqual(key, chinese[key]);
            Assert.NotEqual(key, english[key]);
            Assert.NotEqual(chinese[key], english[key]);
        }
    }

    [Theory]
    [InlineData("zh-CN", false)]
    [InlineData("zh-cn", false)]
    [InlineData("en-US", true)]
    [InlineData("EN-us", true)]
    public void ApplyLanguage_NormalizesSupportedTags(string language, bool expectEnglish)
    {
        var service = new LocalizationService(language);

        Assert.Equal(expectEnglish, service.IsEnglish);
    }

    [Fact]
    public void ApplyLanguage_SystemFollowsTheCurrentUiCulture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            var chinese = new LocalizationService("system");
            Assert.Equal("zh-CN", chinese.LanguageCode);

            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            var other = new LocalizationService("system");
            Assert.Equal("en-US", other.LanguageCode);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ApplyLanguage_DoesNotMutateTheProcessCulture()
    {
        var originalName = CultureInfo.CurrentUICulture.Name;
        var service = new LocalizationService("zh-CN");

        service.ApplyLanguage("en-US");

        Assert.Equal(originalName, CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void ApplyLanguage_RaisesLanguageChangedAndItemNotifications()
    {
        var service = new LocalizationService("zh-CN");
        var languageChanged = 0;
        var properties = new List<string?>();
        service.LanguageChanged += (_, _) => languageChanged++;
        ((INotifyPropertyChanged)service).PropertyChanged += (_, args) => properties.Add(args.PropertyName);

        service.ApplyLanguage("en-US");

        Assert.Equal(1, languageChanged);
        Assert.Contains("Item[]", properties);
        Assert.Contains("LanguageCode", properties);
        Assert.Contains("IsEnglish", properties);
    }

    [Fact]
    public void ApplyLanguage_ToTheSameLanguage_DoesNotRaiseAgain()
    {
        var service = new LocalizationService("zh-CN");
        var languageChanged = 0;
        service.LanguageChanged += (_, _) => languageChanged++;

        service.ApplyLanguage("zh-cn");

        Assert.Equal(0, languageChanged);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData("zz")]
    public void ApplyLanguage_RejectsUnsupportedTags(string language)
    {
        var service = new LocalizationService("zh-CN");

        Assert.Throws<ArgumentException>(() => service.ApplyLanguage(language));
    }

    [Fact]
    public void Format_UsesTheSelectedCultureForArguments()
    {
        var service = new LocalizationService("zh-CN");

        Assert.Equal("已显示 3 / 12 个应用", service.Format("ShowingCount", 3, 12));
    }
}