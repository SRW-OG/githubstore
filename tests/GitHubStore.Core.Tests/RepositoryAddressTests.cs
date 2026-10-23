using GitHubStore.Core.Abstractions;
using GitHubStore.Core.Catalog;
using Xunit;

namespace GitHubStore.Core.Tests;

[Trait("Category", "Unit")]
public sealed class RepositoryAddressTests
{
    [Theory]
    [InlineData("microsoft/PowerToys", "microsoft", "PowerToys")]
    [InlineData("https://github.com/microsoft/PowerToys", "microsoft", "PowerToys")]
    [InlineData("https://github.com/microsoft/PowerToys/", "microsoft", "PowerToys")]
    [InlineData("microsoft/PowerToys.git", "microsoft", "PowerToys")]
    [InlineData("https://github.com/microsoft/PowerToys.git", "microsoft", "PowerToys")]
    [InlineData("  microsoft/PowerToys  ", "microsoft", "PowerToys")]
    [InlineData("https://github.com/Microsoft/PowerToys", "Microsoft", "PowerToys")]
    [InlineData("microsoft/power-toys.2", "microsoft", "power-toys.2")]
    public void Parse_AcceptsPublicGitHubRepositoryAddresses(string input, string owner, string repo)
    {
        var address = RepositoryAddress.Parse(input);

        Assert.Equal(owner, address.Owner);
        Assert.Equal(repo, address.Repo);
        Assert.Equal($"{owner}/{repo}", address.FullName);
        Assert.Equal("https://github.com/" + owner + "/" + repo, address.Url.AbsoluteUri);
    }

    [Fact]
    public void Parse_NormalizesKeyToLowerInvariantForIdentityComparison()
    {
        Assert.Equal("microsoft/powertoys", RepositoryAddress.Parse("Microsoft/PowerToys").Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("microsoft")]
    [InlineData("microsoft/")]
    [InlineData("microsoft/PowerToys/extra")]
    [InlineData("microsoft/tools/PowerToys")]
    [InlineData("github.com/microsoft/PowerToys")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("https://gitlab.com/microsoft/PowerToys")]
    [InlineData("https://api.github.com/repos/microsoft/PowerToys")]
    [InlineData("http://github.com/microsoft/PowerToys")]
    [InlineData("https://github.com/microsoft/PowerToys?tab=readme")]
    [InlineData("https://github.com/microsoft/PowerToys#readme")]
    [InlineData("microsoft/Power Toys")]
    [InlineData("microsoft\\PowerToys")]
    [InlineData("microsoft/Power%20Toys")]
    [InlineData("-microsoft/PowerToys")]
    [InlineData("microsoft/..")]
    [InlineData("microsoft/.")]
    [InlineData("microsoft/PowerToys/../other")]
    public void Parse_RejectsAnythingThatIsNotASinglePublicGitHubRepository(string input)
    {
        var exception = Assert.Throws<RepositoryCatalogException>(() => RepositoryAddress.Parse(input));

        Assert.Equal("invalid_repository", exception.Code);
    }

    [Fact]
    public void Parse_RejectsCredentialsEmbeddedInTheUrl()
    {
        // Built at runtime so no credential-shaped literal lives in the test source.
        var address = string.Concat("https://user", ":", "secret", "@github.com/microsoft/PowerToys");

        var exception = Assert.Throws<RepositoryCatalogException>(() => RepositoryAddress.Parse(address));

        Assert.Equal("invalid_repository", exception.Code);
    }

    [Fact]
    public void Parse_KeepsOwnerWithTrailingHyphenlessSingleCharacter()
    {
        Assert.Equal("ab/Tool", RepositoryAddress.Parse("ab/Tool").FullName);
    }
}
