using System.Net;
using System.Net.Http.Headers;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;
using QUANGLNBrowser.Engine;
using QUANGLNBrowser.Media;
using QUANGLNBrowser.Network;
using QUANGLNBrowser.Security;
using Xunit;

namespace QUANGLNBrowser.Tests;

public sealed class MediaAndDownloadTests : IDisposable
{
    private readonly string _tempRoot;

    public MediaAndDownloadTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "QUANGLNBrowser_MediaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void MediaStreamParser_ParsesFfmpegDurationAndCurrentTime()
    {
        Assert.True(MediaStreamParser.TryParseFfmpegDuration("  Duration: 00:04:30.50, start: 0.000000, bitrate: 1280 kb/s", out var duration));
        Assert.Equal(270.5, duration.TotalSeconds, 2);

        Assert.True(MediaStreamParser.TryParseFfmpegCurrentTime("frame= 3240 fps=120 q=-1.0 size=   15360kB time=00:02:15.25 bitrate= 930.1kbits/s speed=4.0x", out var current));
        Assert.Equal(135.25, current.TotalSeconds, 2);
    }

    [Fact]
    public void MediaStreamParser_ParsesYtDlpProgressAndJsonFormats()
    {
        Assert.True(MediaStreamParser.TryParseYtDlpProgress("[download]  64.8% of ~  45.20MiB at    5.12MiB/s ETA 00:03", out double pct));
        Assert.Equal(64.8, pct, 2);

        string sampleJson = """
            {"title":"Sample Clip","formats":[{"format_id":"137","height":1080,"filesize":52428800},{"format_id":"136","height":720,"filesize":26214400}]}
            """;

        var list = MediaStreamParser.ParseYtDlpJsonFormats(sampleJson, "https://example.com/watch?v=123", "SID=abc", "Mozilla/5.0");
        Assert.Equal(3, list.Count); // 1080p, 720p, AUDIO
        Assert.Contains(list, m => m.QualityLabel == "1080p" && m.Type == MediaStreamType.YtDlp && m.Cookie == "SID=abc");
        Assert.Contains(list, m => m.QualityLabel == "720p" && m.Type == MediaStreamType.YtDlp);
        Assert.Contains(list, m => m.QualityLabel == "AUDIO" && m.Type == MediaStreamType.YtDlp);
    }

    [Fact]
    public void MediaSnifferService_MergesMetadata_AndIgnoresBlobUrls()
    {
        var sniffer = new MediaSnifferService();
        Guid tabId = Guid.NewGuid();

        sniffer.CaptureMedia(tabId, new DetectedMedia { Url = "blob:https://example.com/1234-5678" });
        Assert.Empty(sniffer.GetDetectedMedia(tabId));

        sniffer.CaptureMedia(tabId, new DetectedMedia
        {
            Url = "https://cdn.example.com/video/master.m3u8",
            Referer = "https://example.com/watch",
            UserAgent = "CustomUA/1.0",
            Cookie = "auth=xyz",
            Type = MediaStreamType.HlsOrDash
        });

        // Response bổ sung MimeType và FileSize cho cùng URL
        sniffer.CaptureMedia(tabId, new DetectedMedia
        {
            Url = "https://cdn.example.com/video/master.m3u8",
            MimeType = "application/vnd.apple.mpegurl",
            FileSize = 4096,
            Type = MediaStreamType.HlsOrDash
        });

        var captured = sniffer.GetDetectedMedia(tabId);
        Assert.Single(captured);
        Assert.Equal("auth=xyz", captured[0].Cookie);
        Assert.Equal("https://example.com/watch", captured[0].Referer);
        Assert.Equal("application/vnd.apple.mpegurl", captured[0].MimeType);
        Assert.Equal(4096, captured[0].FileSize);
    }

    [Fact]
    public async Task DownloadManager_Direct8ThreadRandomAccess_DownloadsAndVerifiesByteIntegrity()
    {
        // Tạo dữ liệu giả lập 512 KB
        byte[] expectedData = new byte[512 * 1024];
        for (int i = 0; i < expectedData.Length; i++)
        {
            expectedData[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        var handler = new MockRangeHttpMessageHandler(expectedData);
        using var httpClient = new HttpClient(handler);
        using var jobManager = new JobObjectManager();
        using var downloadManager = new DownloadManager(jobManager, vaultService: null, defaultDownloadsDir: _tempRoot, httpClient: httpClient);

        var tcs = new TaskCompletionSource<DownloadProgressEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        downloadManager.DownloadProgressChanged += (_, e) =>
        {
            if (e.Status == "Completed" || e.Status == "Failed")
            {
                tcs.TrySetResult(e);
            }
        };

        var media = new DetectedMedia
        {
            Url = "https://cdn.example.com/movie.mp4",
            Title = "Test_Movie_8Threads",
            MimeType = "video/mp4",
            Referer = "https://example.com/player",
            UserAgent = "QUANGLNTest/1.0",
            Cookie = "session_token=secret123",
            Type = MediaStreamType.Direct
        };

        var item = await downloadManager.EnqueueDownloadAsync(media);
        var finalProgress = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("Completed", finalProgress.Status);
        Assert.Equal(100.0, finalProgress.ProgressPercent, 1);
        Assert.True(File.Exists(item.SavePath));

        byte[] actualData = await File.ReadAllBytesAsync(item.SavePath);
        Assert.Equal(expectedData.Length, actualData.Length);
        Assert.Equal(expectedData, actualData);

        // Xác nhận đủ 8 chunk request + 1 probe request kèm đúng Cookie & Referer
        Assert.True(handler.RangeRequestCount >= 8);
        Assert.True(handler.ReceivedValidHeaders);
    }

    [Fact]
    public async Task VaultSecurityService_PersistsDownloadHistory_InSqlCipherVault()
    {
        using var vault = new VaultSecurityService(_tempRoot);
        await vault.InitializeNewVaultAsync("Vault#2026", decoyPin: null, enableWindowsHello: false);

        var dl = new DownloadItem
        {
            Id = "dl_001",
            Url = "https://cdn.example.com/clip.mp4",
            FileName = "clip.mp4",
            SavePath = Path.Combine(_tempRoot, "clip.mp4"),
            TotalBytes = 10_000_000,
            DownloadedBytes = 10_000_000,
            Status = "Completed",
            MediaType = "Direct"
        };

        await vault.UpsertDownloadAsync(dl);
        vault.Lock();

        var unlock = await vault.UnlockWithPinAsync("Vault#2026");
        Assert.True(unlock.Success);

        var items = await vault.GetDownloadsAsync();
        Assert.Single(items);
        Assert.Equal("clip.mp4", items[0].FileName);
        Assert.Equal("Completed", items[0].Status);

        await vault.DeleteDownloadAsync("dl_001");
        Assert.Empty(await vault.GetDownloadsAsync());
    }

    [Fact]
    public void VideoOverlayScript_ReturnsShadowDomAwareScript()
    {
        string script = VideoOverlayScript.GetScript();
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("MutationObserver", script);
        Assert.Contains("shadowRoot", script);
        Assert.Contains("QUANGLN_VIDEO_ACTION", script);
    }

    [Fact]
    public void MediaStreamParser_FiltersOutRawCdnFragments_AndDetectsWatchPages_LikeCocCoc()
    {
        // Chặn bắt nhầm chunk DASH/MSE lẻ (nguyên nhân gây file vài trăm KB không xem được)
        Assert.True(MediaStreamParser.IsRawStreamingCdnChunk("https://rr3---sn-8pxuuxa-i5oz.googlevideo.com/videoplayback?expire=170000&range=0-654321"));
        Assert.True(MediaStreamParser.IsRawStreamingCdnChunk("https://video.fsgn2-1.fna.fbcdn.net/v/t42.1790-2/12345.mp4?bytestart=0&byteend=512000"));
        Assert.True(MediaStreamParser.IsRawStreamingCdnChunk("https://v16-webapp.tiktokcdn.com/abc/video/tos/useast2a/chunk.mp4"));
        Assert.True(MediaStreamParser.IsRawStreamingCdnChunk("https://cdn.example.com/hls/segment_001.ts"));
        Assert.True(MediaStreamParser.IsRawStreamingCdnChunk("https://cdn.example.com/dash/frag_2.m4s"));

        // File MP4 trực tiếp hoàn chỉnh không bị chặn
        Assert.False(MediaStreamParser.IsRawStreamingCdnChunk("https://cdn.example.com/videos/full_movie_1080p.mp4"));

        // Nhận diện đúng các trang xem video YouTube / TikTok / Facebook để tự động kích hoạt yt-dlp
        Assert.True(MediaStreamParser.IsLikelyVideoWatchPage("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
        Assert.True(MediaStreamParser.IsLikelyVideoWatchPage("https://www.youtube.com/shorts/abc123xyz"));
        Assert.True(MediaStreamParser.IsLikelyVideoWatchPage("https://www.tiktok.com/@user/video/7123456789"));
        Assert.True(MediaStreamParser.IsLikelyVideoWatchPage("https://www.facebook.com/watch/?v=123456789"));
        Assert.False(MediaStreamParser.IsLikelyVideoWatchPage("https://www.google.com/search?q=test"));

        // Selector <= 1080p ưu tiên H.264 (avc1) + AAC (mp4a); selector 2160p (4K) không bị khóa vào avc1 để tải đúng 4K
        string selector1080 = MediaStreamParser.BuildCompatibleMp4FormatSelector(1080);
        Assert.Contains("vcodec^=avc1", selector1080);
        Assert.Contains("acodec^=mp4a", selector1080);

        string selector4k = MediaStreamParser.BuildCompatibleMp4FormatSelector(2160);
        Assert.Contains("height<=2160", selector4k);
        Assert.DoesNotContain("vcodec^=avc1", selector4k);
    }

    private sealed class MockRangeHttpMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _data;
        private int _rangeRequestCount;

        public int RangeRequestCount => _rangeRequestCount;
        public bool ReceivedValidHeaders { get; private set; }

        public MockRangeHttpMessageHandler(byte[] data)
        {
            _data = data;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool hasCookie = request.Headers.TryGetValues("Cookie", out var cookies) && cookies.Contains("session_token=secret123");
            bool hasReferer = request.Headers.Referrer?.ToString() == "https://example.com/player";
            if (hasCookie && hasReferer)
            {
                ReceivedValidHeaders = true;
            }

            if (request.Headers.Range is not null && request.Headers.Range.Ranges.Count > 0)
            {
                Interlocked.Increment(ref _rangeRequestCount);
                var range = request.Headers.Range.Ranges.First();
                long start = range.From ?? 0;
                long end = range.To ?? (_data.Length - 1);
                end = Math.Min(end, _data.Length - 1);
                int length = (int)(end - start + 1);

                byte[] slice = new byte[length];
                Array.Copy(_data, (int)start, slice, 0, length);

                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                resp.Headers.AcceptRanges.Add("bytes");
                resp.Content.Headers.ContentLength = length;
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _data.Length);
                return Task.FromResult(resp);
            }

            var fullResp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_data)
            };
            fullResp.Headers.AcceptRanges.Add("bytes");
            fullResp.Content.Headers.ContentLength = _data.Length;
            return Task.FromResult(fullResp);
        }
    }

    [Fact]
    public void MediaStreamParser_SanitizesYouTubeWatchUrls_RemovingPlaylistAndRadioParams()
    {
        string dirtyMixUrl = "https://www.youtube.com/watch?v=Af-vjyJIZJA&list=RDAf-vjyJIZJA&start_radio=1";
        string sanitized = MediaStreamParser.SanitizeWatchUrl(dirtyMixUrl);
        Assert.Equal("https://www.youtube.com/watch?v=Af-vjyJIZJA", sanitized);

        string dirtyShortUrl = "https://youtu.be/Af-vjyJIZJA?list=RDAf-vjyJIZJA";
        string sanitizedShort = MediaStreamParser.SanitizeWatchUrl(dirtyShortUrl);
        Assert.Equal("https://www.youtube.com/watch?v=Af-vjyJIZJA", sanitizedShort);

        string directM3u8 = "https://a.kvp726.com/20261010/B9EF5CgO/index.m3u8";
        Assert.Equal(directM3u8, MediaStreamParser.SanitizeWatchUrl(directM3u8));
    }

    [Fact]
    public void SubtitleTranscriptService_ParsesSrtAndAss_AndExportsBilingualSrt()
    {
        string rawSrt = """
            1
            00:00:01,500 --> 00:00:08,500
            Vào năm 2077, thành phố neon này chưa bao giờ ngủ.
            In the year 2077, this neon city never sleeps.

            2
            00:00:10,000 --> 00:00:17,500
            Did you receive the neural signal?
            """;

        var segments = QUANGLNBrowser.Core.Services.SubtitleTranscriptService.ParseSubtitleContent(rawSrt);
        Assert.Equal(2, segments.Count);
        Assert.Equal(1.5, segments[0].StartSeconds, 2);
        Assert.Equal(8.5, segments[0].EndSeconds, 2);
        Assert.Equal("Vào năm 2077, thành phố neon này chưa bao giờ ngủ.", segments[0].VietnameseText);
        Assert.Equal("In the year 2077, this neon city never sleeps.", segments[0].OriginalText);

        segments[1].CustomVietnameseText = "Anh đã nhận được tín hiệu nơ-ron chưa?";
        string exportedDual = QUANGLNBrowser.Core.Services.SubtitleTranscriptService.ExportToSrt(segments, "dual");
        Assert.Contains("00:00:01,500 --> 00:00:08,500", exportedDual);
        Assert.Contains("Anh đã nhận được tín hiệu nơ-ron chưa?\nDid you receive the neural signal?", exportedDual);

        string rawAss = "Dialogue: 0,0:00:02.00,0:00:06.50,Default,Hana,0,0,0,,{\\i1}Wait! Can you hear that voice?{\\i0}";
        var assSegments = QUANGLNBrowser.Core.Services.SubtitleTranscriptService.ParseSubtitleContent(rawAss);
        Assert.Single(assSegments);
        Assert.Equal("Hana", assSegments[0].Speaker);
        Assert.Equal("Wait! Can you hear that voice?", assSegments[0].OriginalText);
    }

    [Fact]
    public void StreamPlaybackResolver_ExtractsProgressiveMp4AndCaptionUrls_FromYtDlpJson()
    {
        string ytDlpJson = """
            {
              "title": "Cyberpunk 2077 Official Trailer",
              "duration": 124.5,
              "formats": [
                {"ext": "mp4", "height": 360, "vcodec": "avc1", "acodec": "mp4a", "url": "https://cdn.example.com/360.mp4"},
                {"ext": "mp4", "height": 1080, "vcodec": "avc1", "acodec": "none", "url": "https://cdn.example.com/1080_video_only.mp4"},
                {"ext": "mp4", "height": 720, "vcodec": "avc1", "acodec": "mp4a", "url": "https://cdn.example.com/720_av.mp4"}
              ],
              "subtitles": {
                "vi": [{"ext": "vtt", "url": "https://cdn.example.com/sub_vi.vtt"}]
              },
              "automatic_captions": {
                "en": [{"ext": "vtt", "url": "https://cdn.example.com/sub_en.vtt"}]
              }
            }
            """;

        var resolved = StreamPlaybackResolver.ParseYtDlpSingleVideoJson(ytDlpJson);
        Assert.True(resolved.Success);
        Assert.Equal("Cyberpunk 2077 Official Trailer", resolved.Title);
        Assert.Equal("https://cdn.example.com/720_av.mp4", resolved.StreamUrl);
        Assert.Null(resolved.AudioUrl);
        Assert.Equal("720p", resolved.QualityLabel);
        Assert.Equal(124.5, resolved.DurationSeconds, 1);
        Assert.Equal("https://cdn.example.com/sub_vi.vtt", resolved.VietnameseSubtitleUrl);
        Assert.Equal("https://cdn.example.com/sub_en.vtt", resolved.OriginalSubtitleUrl);

        // Kiểm tra trường hợp YouTube DASH chứa "height": null / "abr": null và ưu tiên WebM (VP9 + Opus) cho CefSharp
        string youtubeWebmDashJson = """
            {
              "title": "THINK and SPEAK in English",
              "duration": 570,
              "formats": [
                {"ext": "m4a", "height": null, "vcodec": "none", "acodec": "mp4a.40.2", "abr": 129, "protocol": "https", "url": "https://cdn.example.com/audio140.m4a"},
                {"ext": "webm", "height": null, "vcodec": "none", "acodec": "opus", "abr": 148.5, "protocol": "https", "url": "https://cdn.example.com/audio251.webm"},
                {"ext": "mp4", "height": 1080, "vcodec": "avc1.640028", "acodec": "none", "abr": null, "protocol": "https", "url": "https://cdn.example.com/video137.mp4"},
                {"ext": "webm", "height": 1080, "vcodec": "vp9", "acodec": "none", "abr": null, "protocol": "https", "url": "https://cdn.example.com/video248.webm"}
              ]
            }
            """;

        var webmResolved = StreamPlaybackResolver.ParseYtDlpSingleVideoJson(youtubeWebmDashJson);
        Assert.True(webmResolved.Success);
        Assert.Equal("https://cdn.example.com/video248.webm", webmResolved.StreamUrl);
        Assert.Equal("https://cdn.example.com/audio251.webm", webmResolved.AudioUrl);
        Assert.Equal("1080p", webmResolved.QualityLabel);

        // Kiểm tra fallback MP4 + M4A khi không có WebM
        string dashOnlyJson = """
            {
              "title": "MP4 Fallback Clip",
              "duration": 300,
              "formats": [
                {"ext": "m4a", "height": null, "vcodec": "none", "acodec": "mp4a.40.2", "abr": 129, "protocol": "https", "url": "https://cdn.example.com/audio140.m4a"},
                {"ext": "mp4", "height": 1080, "vcodec": "avc1.640028", "acodec": "none", "abr": null, "protocol": "https", "url": "https://cdn.example.com/video137.mp4"}
              ]
            }
            """;

        var dashResolved = StreamPlaybackResolver.ParseYtDlpSingleVideoJson(dashOnlyJson);
        Assert.True(dashResolved.Success);
        Assert.Equal("https://cdn.example.com/video137.mp4", dashResolved.StreamUrl);
        Assert.Equal("https://cdn.example.com/audio140.m4a", dashResolved.AudioUrl);
        Assert.Equal("1080p", dashResolved.QualityLabel);
    }

    [Fact]
    public void SubtitleTranscriptService_ParsesYoutubeVttAndDeduplicates_AndVietnameseTtsSplitsChunks()
    {
        string youtubeVtt = """
            WEBVTT
            Kind: captions
            Language: en

            00:00.000 --> 00:01.440
            Vanessa:
            Are you ready to&nbsp;&nbsp;

            00:01.440 --> 00:07.800
            think and speak in English today?

            00:07.800 --> 00:08.500
            think and speak in English today?
            """;

        var segs = QUANGLNBrowser.Core.Services.SubtitleTranscriptService.ParseSubtitleContent(youtubeVtt);
        Assert.Equal(2, segs.Count);
        Assert.Equal("Vanessa", segs[0].Speaker);
        Assert.Equal("Are you ready to", segs[0].OriginalText);
        Assert.Equal(8.5, segs[1].EndSeconds, 2);

        var chunks = QUANGLNBrowser.Core.Services.VietnameseTtsService.SplitIntoSpeechChunks(
            "Xin chào các bạn đã đến với trình phát video thông minh QUANGLN Player hỗ trợ phụ đề AI Vietsub song ngữ và lồng tiếng Tiếng Việt tự động theo thời gian thực.",
            maxLen: 80);
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 80));
    }

    [Theory]
    [InlineData("quan", "male", "natural")]
    [InlineData("linh", "female", "natural")]
    [InlineData("nam", "male", "dramatic")]
    [InlineData("vy", "female", "soft")]
    [InlineData("auto", "female", "natural")]
    public async Task EdgeTts_AllVoiceProfiles_SynthesizeValidDataUri(string voiceId, string gender, string emotion)
    {
        using var tts = new QUANGLNBrowser.Core.Services.VietnameseTtsService();
        string? uri = await tts.SynthesizeDataUriAsync("Xin chào QUANGLN Player", voiceId, gender, emotion, 1.05);
        Assert.NotNull(uri);
        Assert.StartsWith("data:audio/mpeg;base64,", uri);
    }

    [Fact]
    public void WebVideoCompatibilityScript_LoadsHlsLibraryAndBridgeScript()
    {
        string script = WebVideoCompatibilityScript.GetScript();
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.Contains("__quanglnHlsBridgeInstalled", script);
        Assert.Contains("autoDismissStreamingNotices", script);
        Assert.Contains("attachHlsIfNeeded", script);
        Assert.Contains("NotAllowedError", script);
        Assert.Contains("renderQuanglnPlayerRescueCard", script);
        Assert.Contains("QUANGLN_VIDEO_ACTION", script);
    }

    [Fact]
    public void ManagedAdBlockEngine_NeverBlocksMediaResourceType()
    {
        var engine = new ManagedAdBlockEngine(loadBaseline: true);
        bool shouldBlockMedia = engine.ShouldBlock("https://cdn.example.com/stream.mp4", "https://example.com", "media");
        Assert.False(shouldBlockMedia);

        bool shouldBlockMainFrame = engine.ShouldBlock("https://example.com/watch", "https://google.com", "main_frame");
        Assert.False(shouldBlockMainFrame);
    }

    [Theory]
    [InlineData("https://embed10.streamc.xyz/assets/player-messages-r25.js", "https://embed10.streamc.xyz/embed.php", "script")]
    [InlineData("https://embed10.streamc.xyz/assets/ads.957adeab469d90c1.js", "https://embed10.streamc.xyz/embed.php", "script")]
    [InlineData("https://ssl.p.jwpcdn.com/player/v/8.51.3/jwplayer.js", "https://embed10.streamc.xyz/embed.php", "script")]
    [InlineData("https://cdn.example.com/playlist.m3u8?token=123", "https://motphim.skin/xem-phim", "xhr")]
    [InlineData("https://cdn.example.com/segment-001.ts", "https://motphim.skin/xem-phim", "xhr")]
    [InlineData("https://cdn.example.com/enc.key", "https://motphim.skin/xem-phim", "xhr")]
    [InlineData("https://cdn.example.com/dash/manifest.mpd", "https://motphim.skin/xem-phim", "xhr")]
    public void WebView2ResourceHandler_ExemptsVideoPlaybackAndPlayerScriptsFromBlocking(string url, string sourceUrl, string resourceType)
    {
        bool isExempt = WebView2ResourceHandler.IsVideoPlaybackResource(url, sourceUrl, resourceType);
        Assert.True(isExempt, $"URL '{url}' phải được nhận diện là tài nguyên video/player hợp lệ.");
    }

    [Fact]
    public void WebView2ResourceHandler_RecognizesHlsManifests_AndDistinguishesSegments()
    {
        Assert.True(WebView2ResourceHandler.IsHlsOrDashUrl("https://a.kvp726.com/20261010/B9EF5CgO/index.m3u8"));
        Assert.True(WebView2ResourceHandler.IsHlsOrDashUrl("https://v13.streamvsmov.com/stream/b9c1ec1c-8772-4f94-b3c1-6ab1b77e1917/master.m3u8"));
        Assert.True(WebView2ResourceHandler.IsHlsOrDashUrl("https://cdn.example.com/hls/live/stream.m3u8?token=xyz"));
        Assert.True(WebView2ResourceHandler.IsHlsOrDashUrl("https://cdn.example.com/dash/manifest.mpd?sign=1"));

        // Phân đoạn .ts / .m4s không được xem là playlist manifest
        Assert.False(WebView2ResourceHandler.IsHlsOrDashUrl("https://a.kvp726.com/20261010/B9EF5CgO/1.ts"));
        Assert.False(WebView2ResourceHandler.IsHlsOrDashUrl("https://a.kvp726.com/20261010/B9EF5CgO/segment.m4s?range=0-100"));
        Assert.True(WebView2ResourceHandler.IsMediaSegmentOrCdnChunk("https://a.kvp726.com/20261010/B9EF5CgO/1.ts"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Bỏ qua nếu file đang nhả handle
        }
    }
}
