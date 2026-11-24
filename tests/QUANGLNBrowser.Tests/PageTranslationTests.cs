using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QUANGLNBrowser.Core.Services;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class PageTranslationTests
{
    [Fact]
    public void ParseGoogleTranslateGtxJson_ExtractsMultiSegmentTextAndSourceLanguage()
    {
        const string sampleJson = """
            [
              [
                ["Chữ ký điện tử Vietsens ", "Vietsens electronic signature ", null, null, 10],
                ["Đăng nhập hệ thống", "Sign in to system", null, null, 10]
              ],
              null,
              "en"
            ]
            """;

        var (translated, detectedLang) = PageTranslationService.ParseGoogleTranslateGtxJson(sampleJson);

        Assert.Equal("Chữ ký điện tử Vietsens Đăng nhập hệ thống", translated);
        Assert.Equal("en", detectedLang);
    }

    [Fact]
    public void NeedsTranslation_FiltersOutNumbersSymbolsAndUrls()
    {
        Assert.False(PageTranslationService.NeedsTranslation(null));
        Assert.False(PageTranslationService.NeedsTranslation("   "));
        Assert.False(PageTranslationService.NeedsTranslation("123456"));
        Assert.False(PageTranslationService.NeedsTranslation("--- / ***"));
        Assert.False(PageTranslationService.NeedsTranslation("https://example.com/login"));

        Assert.True(PageTranslationService.NeedsTranslation("Sign in"));
        Assert.True(PageTranslationService.NeedsTranslation("Vietsens electronic signature"));
    }

    [Fact]
    public async Task TranslateBatchAsync_UsesCacheAndFallsBackCleanlyWhenDelimiterSplitsDifferently()
    {
        int httpCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            httpCalls++;
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            string q = Uri.UnescapeDataString(body.StartsWith("q=", StringComparison.Ordinal) ? body[2..] : body);

            // Giả lập server trả sai số delimiter ở lần gọi gộp để test cơ chế fallback từng câu độc lập
            string translated = q.Contains("[[QTR8899]]", StringComparison.Ordinal)
                ? "Broken combined output without delimiters"
                : q switch
                {
                    "Username" => "Tên đăng nhập",
                    "Password" => "Mật khẩu",
                    _ => q
                };

            string gtxPayload = JsonSerializer.Serialize(new object[]
            {
                new object[] { new object[] { translated, q } },
                null!,
                "en"
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(gtxPayload, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var service = new PageTranslationService(http);

        var firstRun = await service.TranslateBatchAsync(["Username", "Password", "12345", "Username"]);
        Assert.Equal(2, firstRun.Count);
        Assert.Equal("Tên đăng nhập", firstRun["Username"]);
        Assert.Equal("Mật khẩu", firstRun["Password"]);
        int callsAfterFirstRun = httpCalls;
        Assert.True(callsAfterFirstRun >= 3);

        // Lần gọi thứ 2 với cùng chuỗi phải lấy từ cache, không phát sinh HTTP call mới
        var cachedRun = await service.TranslateBatchAsync(["Username", "Password"]);
        Assert.Equal("Tên đăng nhập", cachedRun["Username"]);
        Assert.Equal("Mật khẩu", cachedRun["Password"]);
        Assert.Equal(callsAfterFirstRun, httpCalls);
    }

    [Fact]
    public async Task LiveGoogleTranslate_TranslatesBatchProperly()
    {
        var service = new PageTranslationService();
        var results = await service.TranslateBatchAsync([
            "Hello world",
            "How are you today?",
            "Artificial Intelligence is amazing"
        ], "vi");

        Assert.Equal(3, results.Count);
        Assert.True(!string.IsNullOrWhiteSpace(results["Hello world"]));
        Assert.True(!string.IsNullOrWhiteSpace(results["How are you today?"]));
        Assert.True(!string.IsNullOrWhiteSpace(results["Artificial Intelligence is amazing"]));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
