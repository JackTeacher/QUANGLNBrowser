using System.IO;
using System.Reflection;

namespace QUANGLNBrowser.Engine;

/// <summary>
/// Cung cấp thư viện và script tương thích chuẩn Video web hiện đại:
/// - Tự động nạp Hls.js (MediaSource Extensions) vào các trang web có video HLS (.m3u8).
/// - Giữ nguyên canPlayType('') của Chromium desktop để các thư viện player (JWPlayer, Video.js, P2P-Media-Loader)
///   biết kích hoạt đúng pipeline MSE/Hls.js thay vì chọn nhầm Safari native HLS.
/// - Tự động phục hồi lỗi Autoplay NotAllowedError (chuyển sang muted autoplay nếu âm thanh bị chặn).
/// - Tự động đóng thông báo nhắc nhở kiểm tra quảng cáo (Anti-Adblock notices) trên các nguồn embed.
/// - Tự động phát hiện lỗi trình phát web (như JW Player Error 102630 do giới hạn codec Chromium) và hiển thị
///   thẻ chuyển tiếp thông minh "Xem Bằng QUANGLN Player" (libmpv 1080p + AI Vietsub + AI Lồng Tiếng).
/// </summary>
public static class WebVideoCompatibilityScript
{
    private static string? _cachedScript;

    public static string GetScript()
    {
        if (!string.IsNullOrEmpty(_cachedScript))
        {
            return _cachedScript;
        }

        string hlsLib = LoadHlsLibrary();
        string bridgeScript = GetHlsBridgeScript();

        _cachedScript = hlsLib + "\n" + bridgeScript;
        return _cachedScript;
    }

    private static string LoadHlsLibrary()
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, "Resources", "hls.light.min.js");
        if (File.Exists(filePath))
        {
            return File.ReadAllText(filePath);
        }

        var assembly = Assembly.GetExecutingAssembly();
        string? resName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("hls.light.min.js", StringComparison.OrdinalIgnoreCase));
        if (resName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        return string.Empty;
    }

    private static string GetHlsBridgeScript() =>
        """
        (function() {
            if (window.__quanglnHlsBridgeInstalled) return;
            window.__quanglnHlsBridgeInstalled = true;

            try {
                // 1. Tự động phục hồi Autoplay nếu bị chặn chính sách âm thanh
                const origPlay = HTMLMediaElement.prototype.play;
                HTMLMediaElement.prototype.play = function() {
                    const res = origPlay.apply(this, arguments);
                    if (res && typeof res.catch === 'function') {
                        return res.catch((err) => {
                            if (err && err.name === 'NotAllowedError') {
                                this.muted = true;
                                return origPlay.apply(this, arguments);
                            }
                            throw err;
                        });
                    }
                    return res;
                };
            } catch (e) {}

            // 2. Tự động đóng các popup thông báo kiểm tra quảng cáo (Anti-Adblock notices)
            function autoDismissStreamingNotices() {
                try {
                    const buttons = document.querySelectorAll('button, .stream-guard-secondary, [role="button"]');
                    for (let i = 0; i < buttons.length; i++) {
                        const btn = buttons[i];
                        const text = (btn.textContent || '').trim();
                        if (text === 'Đóng thông báo' || text === 'Đóng' || text.includes('Đóng thông báo')) {
                            btn.click();
                        }
                    }
                } catch (e) {}
            }

            setInterval(autoDismissStreamingNotices, 400);

            // 3. Tự động gắn Hls.js (MSE) vào các thẻ <video> độc lập có nguồn .m3u8 (không can thiệp vào JWPlayer/Video.js/Plyr)
            function attachHlsIfNeeded(video) {
                if (!video || video.__quanglnHlsAttached) return;

                // Nếu video thuộc trình phát quản lý chuyên nghiệp, để trình phát tự khởi tạo provider
                if (video.closest && (video.closest('.jwplayer') || video.closest('.video-js') || video.closest('.plyr') || video.closest('#fake_jwplayer'))) {
                    return;
                }

                const src = video.currentSrc || video.src || '';
                let hlsUrl = '';
                if (src.includes('.m3u8')) {
                    hlsUrl = src;
                } else if (video.querySelector) {
                    const sourceEl = video.querySelector('source[src*=".m3u8"]');
                    if (sourceEl && sourceEl.src) {
                        hlsUrl = sourceEl.src;
                    }
                }

                if (!hlsUrl) return;

                if (window.Hls && window.Hls.isSupported && window.Hls.isSupported()) {
                    video.__quanglnHlsAttached = true;
                    try {
                        const hls = new window.Hls({
                            enableWorker: true,
                            lowLatencyMode: true,
                            backBufferLength: 90,
                            maxBufferLength: 60,
                            maxMaxBufferLength: 120
                        });
                        hls.loadSource(hlsUrl);
                        hls.attachMedia(video);
                        video.__quanglnHls = hls;

                        hls.on(window.Hls.Events.ERROR, function(event, data) {
                            if (data.fatal) {
                                switch (data.type) {
                                    case window.Hls.ErrorTypes.NETWORK_ERROR:
                                        hls.startLoad();
                                        break;
                                    case window.Hls.ErrorTypes.MEDIA_ERROR:
                                        hls.recoverMediaError();
                                        break;
                                    default:
                                        hls.destroy();
                                        break;
                                }
                            }
                        });
                    } catch (err) {
                        console.warn('QUANGLN Hls Bridge attachment failed:', err);
                    }
                }
            }

            // Theo dõi lỗi MEDIA_ERR_SRC_NOT_SUPPORTED để cứu luồng video .m3u8 độc lập
            document.addEventListener('error', function(ev) {
                const target = ev.target;
                if (target && target.tagName === 'VIDEO') {
                    attachHlsIfNeeded(target);
                }
            }, true);

            // Quét DOM định kỳ để hỗ trợ thẻ <video> thuần
            function scanAllVideos() {
                const videos = document.querySelectorAll('video');
                for (let i = 0; i < videos.length; i++) {
                    attachHlsIfNeeded(videos[i]);
                }
            }

            scanAllVideos();

            const obs = new MutationObserver(scanAllVideos);
            if (document.documentElement) {
                obs.observe(document.documentElement, { childList: true, subtree: true });
            }

            window.__quanglnPlayHls = function(videoEl, url) {
                if (!videoEl || !url) return;
                videoEl.__quanglnHlsAttached = false;
                videoEl.src = url;
                attachHlsIfNeeded(videoEl);
            };

            // 4. Cứu hộ thông minh khi Web Player nảy sinh lỗi (JW Player Error 102630 / Màn hình lỗi)
            function findBestStreamUrl() {
                let url = '';
                // Ưu tiên server đang chọn
                const activeServer = document.querySelector('.streaming-server.btn-success[data-link], .streaming-server.active[data-link]');
                if (activeServer && activeServer.dataset.link) {
                    return activeServer.dataset.link;
                }
                // Tìm server VIP #1 hoặc server có link m3u8
                const vip1Server = document.querySelector('.streaming-server[data-source="main"][data-link], .streaming-server[data-link*=".m3u8"]');
                if (vip1Server && vip1Server.dataset.link) {
                    return vip1Server.dataset.link;
                }
                const anyServer = document.querySelector('.streaming-server[data-link]');
                if (anyServer && anyServer.dataset.link) {
                    return anyServer.dataset.link;
                }
                return url;
            }

            function renderQuanglnPlayerRescueCard(container, streamUrl) {
                if (!container || container.__quanglnRescueRendered) return;
                container.__quanglnRescueRendered = true;

                const card = document.createElement('div');
                card.id = 'quangln-player-rescue-banner';
                card.style.cssText = [
                    'position: relative',
                    'width: 100%',
                    'min-height: 380px',
                    'background: linear-gradient(135deg, #0c1015 0%, #161c24 100%)',
                    'border: 2px solid #10b981',
                    'border-radius: 14px',
                    'box-sizing: border-box',
                    'padding: 36px 24px',
                    'display: flex',
                    'flex-direction: column',
                    'align-items: center',
                    'justify-content: center',
                    'text-align: center',
                    'color: #ffffff',
                    'font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
                    'box-shadow: 0 12px 36px rgba(0,0,0,0.6)',
                    'z-index: 1000'
                ].join(';');

                card.innerHTML = `
                    <div style="display:flex;align-items:center;gap:12px;margin-bottom:12px;">
                        <span style="font-size:32px;">🎬</span>
                        <div style="font-size:22px;font-weight:800;letter-spacing:0.5px;color:#10b981;">
                            QUANGLN Player — Sẵn sàng phát HD 1080p
                        </div>
                    </div>
                    <p style="font-size:14px;line-height:1.6;color:#94a3b8;max-width:580px;margin:0 0 24px 0;">
                        Nhân Chromium trên web hạn chế giải mã H.264 bản quyền. QUANGLN Browser đã tự động bắt được luồng phim VIP 1080p gốc. Phát ngay bằng QUANGLN Player để xem siêu mượt, hỗ trợ AI Vietsub và AI Lồng Tiếng!
                    </p>
                    <div style="display:flex;flex-wrap:wrap;gap:14px;justify-content:center;margin-bottom:20px;">
                        <button id="quangln-btn-play-mpv" style="
                            background: #10b981;
                            color: #042f2e;
                            border: none;
                            border-radius: 8px;
                            padding: 12px 28px;
                            font-size: 15px;
                            font-weight: 700;
                            cursor: pointer;
                            display: flex;
                            align-items: center;
                            gap: 8px;
                            box-shadow: 0 4px 14px rgba(16,185,129,0.35);
                            transition: transform 0.15s ease, background 0.15s ease;
                        ">
                            <span>▶</span> Phát Ngay Bằng QUANGLN Player
                        </button>
                        <button id="quangln-btn-download-media" style="
                            background: rgba(255,255,255,0.08);
                            color: #e2e8f0;
                            border: 1px solid rgba(255,255,255,0.2);
                            border-radius: 8px;
                            padding: 12px 22px;
                            font-size: 14px;
                            font-weight: 600;
                            cursor: pointer;
                            display: flex;
                            align-items: center;
                            gap: 8px;
                            transition: background 0.15s ease;
                        ">
                            <span>⬇</span> Tải Phim Về Máy
                        </button>
                    </div>
                    <div style="display:flex;gap:12px;font-size:12px;color:#64748b;">
                        <span style="background:rgba(255,255,255,0.05);padding:4px 10px;border-radius:12px;border:1px solid rgba(255,255,255,0.1);">⚡ D3D11VA GPU Accel</span>
                        <span style="background:rgba(255,255,255,0.05);padding:4px 10px;border-radius:12px;border:1px solid rgba(255,255,255,0.1);">🎙️ AI Vietsub & Lồng Tiếng</span>
                        <span style="background:rgba(255,255,255,0.05);padding:4px 10px;border-radius:12px;border:1px solid rgba(255,255,255,0.1);">🔊 Âm lượng 200%</span>
                    </div>
                `;

                // Xóa nội dung lỗi bên trong container và thay bằng Rescue Card
                container.innerHTML = '';
                container.appendChild(card);

                function sendHostMessage(payload) {
                    if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
                        window.chrome.webview.postMessage(payload);
                    } else if (window.CefSharp && window.CefSharp.PostMessage) {
                        window.CefSharp.PostMessage(payload);
                    }
                }

                const playBtn = card.querySelector('#quangln-btn-play-mpv');
                if (playBtn) {
                    playBtn.addEventListener('mouseenter', () => playBtn.style.transform = 'scale(1.04)');
                    playBtn.addEventListener('mouseleave', () => playBtn.style.transform = 'scale(1)');
                    playBtn.addEventListener('click', (ev) => {
                        ev.preventDefault();
                        ev.stopPropagation();
                        sendHostMessage({
                            type: 'QUANGLN_VIDEO_ACTION',
                            action: 'popout',
                            src: streamUrl,
                            pageUrl: window.location.href,
                            title: document.title
                        });
                    });
                }

                const dlBtn = card.querySelector('#quangln-btn-download-media');
                if (dlBtn) {
                    dlBtn.addEventListener('click', (ev) => {
                        ev.preventDefault();
                        ev.stopPropagation();
                        sendHostMessage({
                            type: 'QUANGLN_VIDEO_ACTION',
                            action: 'download',
                            src: streamUrl,
                            pageUrl: window.location.href,
                            title: document.title
                        });
                    });
                }
            }

            function checkForPlayerErrors() {
                try {
                    const streamUrl = findBestStreamUrl();
                    if (!streamUrl) return;

                    // Kiểm tra các dấu hiệu lỗi phổ biến của trình phát web
                    const hasJwError = document.querySelector('.jw-error, .jw-error-msg, [id*="jwplayer"].jw-error');
                    let hasTextError = false;

                    const notices = document.querySelectorAll('div, p, span, h2, h3');
                    for (let i = 0; i < notices.length; i++) {
                        const t = (notices[i].textContent || '').trim();
                        if (t.includes('Đã xảy ra lỗi khi phát video') || t.includes('Chưa thể khởi động video')) {
                            hasTextError = true;
                            break;
                        }
                    }

                    if (hasJwError || hasTextError) {
                        const targetContainer = document.getElementById('player') ||
                                                document.getElementById('fake_jwplayer') ||
                                                document.querySelector('.jwplayer') ||
                                                document.querySelector('.box-player');
                        if (targetContainer) {
                            renderQuanglnPlayerRescueCard(targetContainer, streamUrl);
                        }
                    }
                } catch (e) {}
            }

            setInterval(checkForPlayerErrors, 800);
        })();
        """;
}
