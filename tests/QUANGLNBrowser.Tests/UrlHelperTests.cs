using QUANGLNBrowser.Core.Services;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class UrlHelperTests
{
    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("https://a.b/c", "https://a.b/c")]
    [InlineData("hello world", "https://www.google.com/search?q=hello%20world")]
    [InlineData("localhost:5000", "http://localhost:5000")]
    [InlineData("   ", "")]
    public void NormalizeUrlOrSearch_NormalizesInputsCorrectly(string input, string expected)
    {
        string actual = UrlHelper.NormalizeUrlOrSearch(input);
        Assert.Equal(expected, actual);
    }
}
