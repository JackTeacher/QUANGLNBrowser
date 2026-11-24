(function () {
    if (window.__quanglnVideoOverlayInstalled) {
        return;
    }
    window.__quanglnVideoOverlayInstalled = true;

    const trackedVideos = new Set();
    const dismissedVideos = new WeakSet();
    let activeVideo = null;
    let hideTimer = null;
    let isMenuOpen = false;

    const QUALITY_LABELS = {
        '2160': '4K UHD',
        '1440': '2K QHD',
        '1080': 'Full HD',
        '720': 'HD',
        '480': 'Tiêu chuẩn',
        '360': 'Trung bình',
        '240': 'Thấp',
        '144': 'Di động',
        'best': 'Gốc / Tốt nhất'
    };

    // Theo dõi luồng manifest (.m3u8, .mpd) và phụ đề (.vtt, .srt) được tải qua fetch/XHR
    let latestCapturedManifestUrl = '';
    let latestCapturedSubtitleUrl = '';

    try {
        const origFetch = window.fetch;
        if (typeof origFetch === 'function') {
            window.fetch = function (resource, init) {
                try {
                    const u = typeof resource === 'string' ? resource : (resource && resource.url ? resource.url : '');
                    if (u && typeof u === 'string') {
                        if (u.includes('.m3u8') || u.includes('.mpd') || u.includes('/hls/')) {
                            latestCapturedManifestUrl = u;
                        } else if (u.includes('.vtt') || u.includes('.srt')) {
                            latestCapturedSubtitleUrl = u;
                        }
                    }
                } catch (e) {}
                return origFetch.apply(this, arguments);
            };
        }

        const origXhrOpen = XMLHttpRequest.prototype.open;
        if (typeof origXhrOpen === 'function') {
            XMLHttpRequest.prototype.open = function (method, url) {
                try {
                    const u = typeof url === 'string' ? url : '';
                    if (u) {
                        if (u.includes('.m3u8') || u.includes('.mpd') || u.includes('/hls/')) {
                            latestCapturedManifestUrl = u;
                        } else if (u.includes('.vtt') || u.includes('.srt')) {
                            latestCapturedSubtitleUrl = u;
                        }
                    }
                } catch (e) {}
                return origXhrOpen.apply(this, arguments);
            };
        }
    } catch (e) {}

    let currentVideoItems = [
        { value: '2160', name: '4K UHD', res: '2160p', ext: '.mp4' },
        { value: '1440', name: '2K QHD', res: '1440p', ext: '.mp4' },
        { value: '1080', name: 'Full HD', res: '1080p', ext: '.mp4' },
        { value: '720', name: 'HD', res: '720p', ext: '.mp4' },
        { value: '480', name: 'Tiêu chuẩn', res: '480p', ext: '.mp4' },
        { value: '360', name: 'Trung bình', res: '360p', ext: '.mp4' },
        { value: '240', name: 'Thấp', res: '240p', ext: '.mp4' },
        { value: '144', name: 'Di động', res: '144p', ext: '.mp4' }
    ];

    let currentAudioItems = [
        { value: 'audio', name: 'Trung bình', res: '128kbps', ext: '.m4a' }
    ];

    // Wrapper cố định ở góc trên bên trái của khung video (kiểu Cốc Cốc Savior, tương thích 100% Trusted Types CSP)
    const rootWrapper = document.createElement('div');
    rootWrapper.id = 'quangln-floating-video-wrapper';
    rootWrapper.style.cssText = [
        'position: fixed',
        'z-index: 2147483647',
        'display: none',
        'flex-direction: column',
        'align-items: flex-start',
        'gap: 6px',
        'font-family: Segoe UI, system-ui, sans-serif',
        'user-select: none',
        'pointer-events: auto'
    ].join(';');

    // Thanh icon nhỏ gọn phía trên: [ × ] [ ⬇ ] [ ⧉ ]
    const bar = document.createElement('div');
    bar.id = 'quangln-floating-video-bar';
    bar.style.cssText = [
        'display: flex',
        'align-items: center',
        'gap: 4px',
        'padding: 4px 6px',
        'background: rgba(24, 24, 30, 0.94)',
        'border: 1px solid rgba(255, 255, 255, 0.16)',
        'border-radius: 16px',
        'box-shadow: 0 4px 16px rgba(0,0,0,0.5)',
        'backdrop-filter: blur(8px)'
    ].join(';');

    function createIconBtn(symbolText, bgColor, titleText, onClick) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.title = titleText;
        btn.textContent = symbolText;
        btn.style.cssText = [
            'all: unset',
            'cursor: pointer',
            'width: 24px',
            'height: 24px',
            'border-radius: 12px',
            'display: flex',
            'align-items: center',
            'justify-content: center',
            'font-size: 13px',
            'font-weight: 700',
            'color: #FFFFFF',
            'background: ' + bgColor,
            'transition: transform 0.12s ease, opacity 0.12s ease'
        ].join(';');
        btn.addEventListener('mouseenter', () => { btn.style.opacity = '0.88'; btn.style.transform = 'scale(1.06)'; });
        btn.addEventListener('mouseleave', () => { btn.style.opacity = '1'; btn.style.transform = 'scale(1)'; });
        btn.addEventListener('click', (ev) => {
            ev.preventDefault();
            ev.stopPropagation();
            onClick();
        });
        return btn;
    }

    const btnDismiss = createIconBtn('✕', 'rgba(255,255,255,0.14)', 'Ẩn thanh tải video', () => {
        if (activeVideo) {
            dismissedVideos.add(activeVideo);
        }
        closeMenu();
        rootWrapper.style.display = 'none';
    });

    const btnDownloadToggle = createIconBtn('⬇', '#10B981', 'Chọn tệp để tải (4K / 2K / Full HD / Audio)', () => {
        if (isMenuOpen) {
            closeMenu();
        } else {
            openMenu();
        }
    });

    const btnPopout = createIconBtn('⧉', '#F59E0B', 'Xem riêng (libmpv GPU)', async () => {
        if (activeVideo) {
            const curInfo = findBestStreamForVideo(activeVideo);
            const curSrc = curInfo.streamUrl || (activeVideo.currentSrc || activeVideo.src || '');
            if (!curSrc || curSrc.startsWith('blob:') || curSrc.startsWith('data:')) {
                try {
                    if (activeVideo.paused) {
                        await activeVideo.play();
                        await new Promise(r => setTimeout(r, 600));
                        try { activeVideo.pause(); } catch (e) {}
                    }
                } catch (e) {}
            }
        }
        sendVideoAction('popout', 'best');
    });

    bar.appendChild(btnDismiss);
    bar.appendChild(btnDownloadToggle);
    bar.appendChild(btnPopout);

    // Card Popup "Chọn tệp để tải" kiểu Cốc Cốc
    const menuCard = document.createElement('div');
    menuCard.id = 'quangln-download-quality-menu';
    menuCard.style.cssText = [
        'display: none',
        'width: 256px',
        'background: #23232A',
        'border: 1px solid #3A3A46',
        'border-radius: 12px',
        'box-shadow: 0 10px 30px rgba(0,0,0,0.65)',
        'overflow: hidden',
        'color: #F3F4F6',
        'font-size: 12px'
    ].join(';');

    function findBestStreamForVideo(video) {
        if (!video) return { streamUrl: '', subtitleUrl: '' };

        let streamUrl = video.currentSrc || video.src || '';
        let subtitleUrl = '';

        if (video._quanglnStreamUrl) {
            streamUrl = video._quanglnStreamUrl;
        }

        // Nếu src rỗng hoặc là blob/data, truy quét toàn diện trong DOM của trang xem phim (Motchill, Phimmoi, RoPhim, Haychill...)
        if (!streamUrl || streamUrl.startsWith('blob:') || streamUrl.startsWith('data:')) {
            // 1. Thử manifest bắt được qua fetch/XHR
            if (latestCapturedManifestUrl) {
                streamUrl = latestCapturedManifestUrl;
            }

            // 2. Quét các nút server streaming trên trang hiện tại
            try {
                const activeServer = document.querySelector(
                    '.streaming-server.btn-primary, .streaming-server.btn-success, .streaming-server.active, .btn-sv.btn-primary, .btn-sv.btn-success, .btn-sv.active, [data-link].active, [data-link].selected, .streaming-server, .btn-sv'
                );
                if (activeServer && activeServer.dataset && activeServer.dataset.link) {
                    const lk = activeServer.dataset.link.trim();
                    if (lk.includes('.m3u8') || lk.includes('.mp4') || lk.includes('/stream/')) {
                        streamUrl = lk.replace(/^http:\/\//i, 'https://');
                        if (activeServer.dataset.subtitle) {
                            subtitleUrl = activeServer.dataset.subtitle.trim().replace(/^http:\/\//i, 'https://');
                        }
                    }
                }

                // 3. Quét bất kỳ nút server nào chứa trực tiếp link .m3u8 hoặc .mp4 (ví dụ VIP #1, VIP #2 trên Motchill)
                if (!streamUrl || streamUrl.startsWith('blob:')) {
                    const m3u8Btn = document.querySelector(
                        'a[data-link*=".m3u8"], button[data-link*=".m3u8"], [data-type="m3u8"][data-link], a[data-link*=".mp4"], button[data-link*=".mp4"], [data-link*="/stream/"]'
                    );
                    if (m3u8Btn && m3u8Btn.dataset && m3u8Btn.dataset.link) {
                        streamUrl = m3u8Btn.dataset.link.trim().replace(/^http:\/\//i, 'https://');
                        if (m3u8Btn.dataset.subtitle) {
                            subtitleUrl = m3u8Btn.dataset.subtitle.trim().replace(/^http:\/\//i, 'https://');
                        }
                    }
                }

                // 4. Quét JSON-LD hoặc thẻ <script> chứa "contentUrl" hoặc .m3u8
                if (!streamUrl || streamUrl.startsWith('blob:')) {
                    const scripts = document.querySelectorAll('script[type="application/ld+json"], script:not([src])');
                    for (let i = 0; i < scripts.length; i++) {
                        const txt = scripts[i].textContent || '';
                        const m = txt.match(/"contentUrl"\s*:\s*"([^"]+\.m3u8[^"]*)"/i) ||
                                  txt.match(/["'](https?:\/\/[^"'\s]+\.m3u8[^"'\s]*)["']/i);
                        if (m && m[1]) {
                            streamUrl = m[1].replace(/\\/g, '');
                            break;
                        }
                    }
                }

                // 5. Quét các đối tượng player thông dụng (Hls.js, DPlayer, ArtPlayer, JWPlayer...)
                if (!streamUrl || streamUrl.startsWith('blob:')) {
                    if (window.hls && window.hls.url) {
                        streamUrl = window.hls.url;
                    } else if (window.player && window.player.src) {
                        streamUrl = window.player.src;
                    } else if (window.art && window.art.option && window.art.option.url) {
                        streamUrl = window.art.option.url;
                    } else if (window.dp && window.dp.video && window.dp.video.src) {
                        streamUrl = window.dp.video.src;
                    }
                }

                // 6. Nếu đang nằm trong iframe, thử quét qua window.parent / window.top (nếu cùng origin hoặc cho phép)
                if ((!streamUrl || streamUrl.startsWith('blob:')) && window.parent && window.parent !== window) {
                    try {
                        const pDoc = window.parent.document;
                        if (pDoc) {
                            const pBtn = pDoc.querySelector(
                                'a[data-link*=".m3u8"], button[data-link*=".m3u8"], [data-type="m3u8"][data-link], .streaming-server[data-link]'
                            );
                            if (pBtn && pBtn.dataset && pBtn.dataset.link) {
                                streamUrl = pBtn.dataset.link.trim().replace(/^http:\/\//i, 'https://');
                                if (pBtn.dataset.subtitle) {
                                    subtitleUrl = pBtn.dataset.subtitle.trim().replace(/^http:\/\//i, 'https://');
                                }
                            }
                        }
                    } catch (e) {}
                }

                // 7. Quét phụ đề trong các thẻ <track> của <video>
                if (!subtitleUrl) {
                    const tracks = video.querySelectorAll('track');
                    for (let i = 0; i < tracks.length; i++) {
                        const tr = tracks[i];
                        if (tr.src && (tr.src.includes('.vtt') || tr.src.includes('.srt'))) {
                            subtitleUrl = tr.src;
                            break;
                        }
                    }
                }
            } catch (err) {}
        }

        if (!subtitleUrl && latestCapturedSubtitleUrl) {
            subtitleUrl = latestCapturedSubtitleUrl;
        }

        return { streamUrl, subtitleUrl };
    }

    function sendVideoAction(actionName, selectedQuality) {
        if (!activeVideo) return;

        let info = findBestStreamForVideo(activeVideo);
        let resolvedSrc = info.streamUrl || (activeVideo.currentSrc || activeVideo.src || '');
        let resolvedSub = info.subtitleUrl || '';

        // Tạm dừng video trang web khi chuyển sang phát riêng
        if (actionName === 'popout') {
            try { activeVideo.pause(); } catch (e) {}
        }

        let topUrl = window.location.href;
        try {
            if (window.top && window.top.location && window.top.location.href) {
                topUrl = window.top.location.href;
            }
        } catch (e) {
            topUrl = document.referrer || window.location.href;
        }

        const msgPayload = {
            type: 'QUANGLN_VIDEO_ACTION',
            action: actionName,
            quality: selectedQuality || '1080',
            src: resolvedSrc,
            subtitleUrl: resolvedSub,
            pageUrl: topUrl,
            frameUrl: window.location.href,
            title: document.title || 'Video',
            cookie: document.cookie || '',
            userAgent: navigator.userAgent || ''
        };

        if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
            window.chrome.webview.postMessage(msgPayload);
        } else if (window.CefSharp && typeof window.CefSharp.PostMessage === 'function') {
            window.CefSharp.PostMessage(msgPayload);
        }
    }

    function renderMenuCard() {
        // Dùng replaceChildren() thay vì innerHTML để không bao giờ bị YouTube Trusted Types CSP chặn
        menuCard.replaceChildren();

        const header = document.createElement('div');
        header.style.cssText = [
            'display: flex',
            'align-items: center',
            'justify-content: space-between',
            'padding: 10px 12px',
            'border-bottom: 1px solid #343440'
        ].join(';');

        const titleWrap = document.createElement('div');
        titleWrap.style.cssText = 'display: flex; align-items: center; gap: 8px; font-weight: 600; font-size: 13px; color: #FFFFFF;';
        const badge = document.createElement('span');
        badge.textContent = '⬇';
        badge.style.cssText = 'width: 20px; height: 20px; border-radius: 10px; background: #10B981; color: #fff; display: inline-flex; align-items: center; justify-content: center; font-size: 11px;';
        const titleSpan = document.createElement('span');
        titleSpan.textContent = 'Chọn tệp để tải';
        titleWrap.appendChild(badge);
        titleWrap.appendChild(titleSpan);

        const closeX = document.createElement('button');
        closeX.type = 'button';
        closeX.textContent = '✕';
        closeX.style.cssText = 'all: unset; cursor: pointer; color: #9CA3AF; font-size: 13px; padding: 2px 4px;';
        closeX.addEventListener('click', (ev) => {
            ev.preventDefault();
            ev.stopPropagation();
            closeMenu();
        });

        header.appendChild(titleWrap);
        header.appendChild(closeX);
        menuCard.appendChild(header);

        const body = document.createElement('div');
        body.style.cssText = 'padding: 6px 0; max-height: 360px; overflow-y: auto;';

        function appendSectionHeader(iconText, labelText) {
            const sec = document.createElement('div');
            sec.style.cssText = 'padding: 6px 12px 4px 12px; font-size: 10.5px; font-weight: 700; color: #9CA3AF; letter-spacing: 0.4px; display: flex; align-items: center; gap: 6px;';
            sec.textContent = iconText + ' ' + labelText;
            body.appendChild(sec);
        }

        function appendOptionRow(item) {
            const row = document.createElement('div');
            row.style.cssText = [
                'display: grid',
                'grid-template-columns: 1fr auto auto',
                'align-items: center',
                'gap: 10px',
                'padding: 7px 12px',
                'cursor: pointer',
                'border-bottom: 1px solid rgba(255,255,255,0.04)',
                'transition: background 0.12s ease'
            ].join(';');
            row.addEventListener('mouseenter', () => { row.style.background = '#32323D'; });
            row.addEventListener('mouseleave', () => { row.style.background = 'transparent'; });
            row.addEventListener('click', (ev) => {
                ev.preventDefault();
                ev.stopPropagation();
                closeMenu();
                sendVideoAction('download', item.value);
            });

            const colName = document.createElement('span');
            colName.textContent = item.name;
            colName.style.cssText = 'font-weight: 600; color: #F9FAFB;';

            const colRes = document.createElement('span');
            colRes.textContent = item.res;
            colRes.style.cssText = 'color: #D1D5DB; font-size: 11.5px; text-align: right;';

            const colExt = document.createElement('span');
            colExt.textContent = item.ext;
            colExt.style.cssText = 'color: #9CA3AF; font-size: 11px; min-width: 40px; text-align: right;';

            row.appendChild(colName);
            row.appendChild(colRes);
            row.appendChild(colExt);
            body.appendChild(row);
        }

        appendSectionHeader('🎬', 'VIDEO');
        for (let i = 0; i < currentVideoItems.length; i++) {
            appendOptionRow(currentVideoItems[i]);
        }

        appendSectionHeader('🎵', 'AUDIO');
        for (let i = 0; i < currentAudioItems.length; i++) {
            appendOptionRow(currentAudioItems[i]);
        }

        menuCard.appendChild(body);
    }

    function openMenu() {
        clearTimeout(hideTimer);
        renderMenuCard();
        menuCard.style.display = 'block';
        isMenuOpen = true;
    }

    function closeMenu() {
        menuCard.style.display = 'none';
        isMenuOpen = false;
    }

    rootWrapper.appendChild(bar);
    rootWrapper.appendChild(menuCard);

    // Cho phép C# cập nhật danh sách độ phân giải thực tế sau khi yt-dlp quét xong
    window.__quanglnSetQualities = function (items) {
        if (!Array.isArray(items) || items.length === 0) {
            return;
        }

        const nextVideos = [];
        const nextAudios = [];

        for (let i = 0; i < items.length; i++) {
            const it = items[i];
            const val = String(it.value || '');
            if (val === 'audio') {
                nextAudios.push({
                    value: 'audio',
                    name: 'Trung bình',
                    res: it.sizeText ? it.sizeText : '128kbps',
                    ext: '.m4a'
                });
            } else {
                const name = QUALITY_LABELS[val] || (val + 'p');
                const res = /^\d+$/.test(val) ? (val + 'p') : 'HD';
                const ext = it.sizeText ? ('.mp4 · ' + it.sizeText) : '.mp4';
                nextVideos.push({ value: val, name: name, res: res, ext: ext });
            }
        }

        if (nextVideos.length > 0) {
            currentVideoItems = nextVideos;
        }
        if (nextAudios.length > 0) {
            currentAudioItems = nextAudios;
        }
        if (isMenuOpen) {
            renderMenuCard();
        }
    };

    function ensureMounted() {
        const root = document.body || document.documentElement;
        if (root && !rootWrapper.parentNode) {
            root.appendChild(rootWrapper);
        }
    }

    function positionForVideo(video) {
        if (!video || dismissedVideos.has(video)) return;
        const rect = video.getBoundingClientRect();
        if (rect.width < 140 || rect.height < 90) {
            rootWrapper.style.display = 'none';
            return;
        }
        ensureMounted();
        rootWrapper.style.display = 'flex';
        const top = Math.max(8, rect.top + 8);
        const left = Math.max(8, rect.left + 8);
        rootWrapper.style.top = top + 'px';
        rootWrapper.style.left = left + 'px';
    }

    function scheduleHide() {
        clearTimeout(hideTimer);
        hideTimer = setTimeout(() => {
            if (isMenuOpen) {
                return;
            }
            rootWrapper.style.display = 'none';
        }, 1600);
    }

    rootWrapper.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    rootWrapper.addEventListener('mouseleave', () => scheduleHide());

    function scanRoot(root) {
        if (!root || !root.querySelectorAll) return;
        const videos = root.querySelectorAll('video');
        for (let i = 0; i < videos.length; i++) {
            trackedVideos.add(videos[i]);
        }

        const allElements = root.querySelectorAll('*');
        for (let i = 0; i < allElements.length; i++) {
            if (allElements[i].shadowRoot) {
                scanRoot(allElements[i].shadowRoot);
            }
        }
    }

    // Hit-test theo tọa độ con trỏ chuột để không bị các lớp overlay của YouTube/Facebook che mất sự kiện hover của thẻ <video>
    document.addEventListener('mousemove', (ev) => {
        if (rootWrapper.contains(ev.target)) {
            clearTimeout(hideTimer);
            return;
        }

        if (trackedVideos.size === 0) {
            scanRoot(document);
        }

        let hoveredVideo = null;
        const x = ev.clientX;
        const y = ev.clientY;

        trackedVideos.forEach((v) => {
            if (!v.isConnected) {
                trackedVideos.delete(v);
                return;
            }
            if (dismissedVideos.has(v)) {
                return;
            }
            const rect = v.getBoundingClientRect();
            if (rect.width >= 140 && rect.height >= 90 &&
                x >= rect.left && x <= rect.right &&
                y >= rect.top && y <= rect.bottom) {
                hoveredVideo = v;
            }
        });

        if (hoveredVideo) {
            clearTimeout(hideTimer);
            activeVideo = hoveredVideo;
            positionForVideo(hoveredVideo);
        } else if (!isMenuOpen && rootWrapper.style.display === 'flex') {
            scheduleHide();
        }
    }, { passive: true });

    // Đóng menu khi bấm ra ngoài
    document.addEventListener('click', (ev) => {
        if (isMenuOpen && !rootWrapper.contains(ev.target)) {
            closeMenu();
        }
    }, true);

    scanRoot(document);

    const observer = new MutationObserver(() => {
        scanRoot(document);
    });

    if (document.documentElement) {
        observer.observe(document.documentElement, {
            childList: true,
            subtree: true
        });
    }

    window.addEventListener('scroll', () => {
        if (activeVideo && rootWrapper.style.display === 'flex') {
            positionForVideo(activeVideo);
        }
    }, { passive: true });
})();
