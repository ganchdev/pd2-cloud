using PD2Cloud.Desktop;
using Xunit;

namespace PD2Cloud.Tests;

/// <summary>
/// The GitHub App client ID is public metadata. Release artifacts embed it so a
/// downloaded build works without environment configuration, while the
/// environment variable remains available for development.
/// </summary>
public sealed class ClientIdTests
{
    [Fact]
    public void EnvironmentVariableTakesPrecedenceOverEmbeddedValue()
    {
        Assert.Equal("development-value", ClientId.Choose("development-value", "embedded-value"));
    }

    [Fact]
    public void EmbeddedValueIsUsedWhenEnvironmentIsAbsent()
    {
        Assert.Equal("embedded-value", ClientId.Choose(null, "embedded-value"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankEnvironmentVariableFallsBackToEmbeddedValue(string configured)
    {
        Assert.Equal("embedded-value", ClientId.Choose(configured, "embedded-value"));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        Assert.Equal("development-value", ClientId.Choose("  development-value  ", "embedded-value"));
    }

    [Fact]
    public void MissingEmbeddedValueYieldsEmptyConfiguration()
    {
        Assert.Equal("", ClientId.Choose(null, ""));
    }

    [Fact]
    public void MissingConfigurationOnBothSourcesYieldsEmptyConfiguration()
    {
        Assert.Equal("", ClientId.Choose(null, null));
    }
}
