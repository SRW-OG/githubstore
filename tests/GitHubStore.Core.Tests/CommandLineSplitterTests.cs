using GitHubStore.Core.Inventory;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class CommandLineSplitterTests
{
    [Fact]
    public void QuotedPathWithArgument_SeparatesPathFromArguments()
    {
        var (fileName, arguments) = CommandLineSplitter.Split(
            "\"C:\\Program Files\\Some App\\uninstall.exe\" /S");
        Assert.Equal("C:\\Program Files\\Some App\\uninstall.exe", fileName);
        Assert.Equal("/S", arguments);
    }

    [Fact]
    public void QuotedPathWithoutArgument_YieldsEmptyArguments()
    {
        var (fileName, arguments) = CommandLineSplitter.Split("\"C:\\App\\uninstall.exe\"");
        Assert.Equal("C:\\App\\uninstall.exe", fileName);
        Assert.Equal(string.Empty, arguments);
    }

    [Theory]
    [InlineData("C:\\App Folder\\uninstall.exe /quiet")]
    [InlineData("C:\\App Folder\\uninstall.exe")]
    [InlineData("\"C:\\App\\u.exe /S")]
    [InlineData("\"\" /S")]
    [InlineData("\"   \" /S")]
    [InlineData("\"C:\\App\\u.exe\"/S")]
    [InlineData("C:\\App\\u.exe \"unclosed")]
    [InlineData("C:\\App\\u.exe\" /X")]
    [InlineData("C:\\App\\u.exe\n/X")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void InvalidOrAmbiguousInput_IsRejectedAndCompatibleSplitReturnsEmpty(string? commandLine)
    {
        Assert.False(CommandLineSplitter.TrySplit(commandLine, out var fileName, out var arguments));
        Assert.Equal(string.Empty, fileName);
        Assert.Equal(string.Empty, arguments);
        Assert.Equal((string.Empty, string.Empty), CommandLineSplitter.Split(commandLine));
    }

    [Fact]
    public void BareFileNameWithoutArguments_IsParsedForPlatformValidation()
    {
        Assert.Equal(("uninstall.exe", string.Empty), CommandLineSplitter.Split("uninstall.exe"));
    }

    [Fact]
    public void SurroundingWhitespace_IsTrimmedFromBothParts()
    {
        var (fileName, arguments) = CommandLineSplitter.Split("   \"C:\\App\\u.exe\"    /S /Q   ");
        Assert.Equal("C:\\App\\u.exe", fileName);
        Assert.Equal("/S /Q", arguments);
    }

    [Fact]
    public void ArgumentsArePreservedVerbatim_NeverRewritten()
    {
        var (fileName, arguments) = CommandLineSplitter.Split("\"C:\\App\\u.exe\" /x:\"a b\"  --flag=1");
        Assert.Equal("C:\\App\\u.exe", fileName);
        Assert.Equal("/x:\"a b\"  --flag=1", arguments);
    }

    [Theory]
    [InlineData("C:\\App\\u.exe\t/X {GUID}")]
    [InlineData("\"C:\\App\\u.exe\"\t/X {GUID}")]
    public void TabSeparatesExecutableAndArguments(string commandLine)
    {
        Assert.Equal(("C:\\App\\u.exe", "/X {GUID}"), CommandLineSplitter.Split(commandLine));
    }

    [Theory]
    [InlineData("MsiExec.exe")]
    [InlineData("msiexec")]
    public void MsiExecStyleCommand_KeepsProductCodeAsArgument(string executable)
    {
        Assert.Equal((executable, "/X{12345678-1234-1234-1234-123456789012}"),
            CommandLineSplitter.Split(executable + " /X{12345678-1234-1234-1234-123456789012}"));
    }

    [Fact]
    public void EscapedQuoteInsideArguments_DoesNotOpenAnUnclosedQuote()
    {
        Assert.True(CommandLineSplitter.TrySplit("C:\\App\\u.exe /label:a\\\"b", out _, out var arguments));
        Assert.Equal("/label:a\\\"b", arguments);
    }
}
