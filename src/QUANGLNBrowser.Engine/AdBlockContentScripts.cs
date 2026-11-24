namespace QUANGLNBrowser.Engine;

/// <summary>
/// Bộ script tầng nội dung cho tính năng Chặn quảng cáo kiểu Cốc Cốc:
/// 1. YouTube Ad Cleaner &amp; Auto-Skipper (ẩn banner/feed ads, tự động bấm Bỏ qua quảng cáo &amp; tua qua pre-roll).
/// 2. Element Picker (Chặn phần tử thủ công trực tiếp trên trang web như Cốc Cốc / Adblock Plus).
/// </summary>
public static class AdBlockContentScripts
{
    public static string GetYouTubeAdBlockerScript() =>
        """
        (function() {
            if (window.__quanglnYtAdBlockActive) return;
            window.__quanglnYtAdBlockActive = true;

            const AD_SELECTORS = [
                'ytd-ad-slot-renderer',
                'ytd-in-feed-ad-layout-renderer',
                'ytd-promoted-sparkles-web-renderer',
                'ytd-promoted-video-renderer',
                'ytd-display-ad-renderer',
                'ytd-banner-promo-renderer',
                'ytd-statement-banner-renderer',
                '#masthead-ad',
                '#player-ads',
                '.ytp-ad-overlay-container',
                '.ytp-ad-message-container',
                'ytd-engagement-panel-section-list-renderer[target-id="engagement-panel-ads"]'
            ];

            function cleanYouTubeAds() {
                try {
                    const player = document.querySelector('.html5-video-player');
                    const isAdShowing = player && (player.classList.contains('ad-showing') || player.classList.contains('ad-interrupting'));

                    if (isAdShowing) {
                        const skipBtn = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-slot button');
                        if (skipBtn) {
                            skipBtn.click();
                        }

                        const video = player.querySelector('video');
                        if (video && isFinite(video.duration) && video.duration > 0 && video.currentTime < video.duration - 0.1) {
                            video.muted = true;
                            video.currentTime = video.duration;
                        }
                    }

                    for (let i = 0; i < AD_SELECTORS.length; i++) {
                        const nodes = document.querySelectorAll(AD_SELECTORS[i]);
                        for (let j = 0; j < nodes.length; j++) {
                            if (nodes[j].style.display !== 'none') {
                                nodes[j].style.setProperty('display', 'none', 'important');
                            }
                        }
                    }
                } catch (e) {}
            }

            cleanYouTubeAds();
            const obs = new MutationObserver(cleanYouTubeAds);
            if (document.documentElement) {
                obs.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
            }
            setInterval(cleanYouTubeAds, 600);
        })();
        """;

    public static string GetElementPickerScript() =>
        """
        (function() {
            if (window.__quanglnElementPickerCleanup) {
                window.__quanglnElementPickerCleanup();
            }

            const UI_ATTR = 'data-quangln-picker-ui';
            let hoveredEl = null;
            let lockedEl = null;

            const highlightBox = document.createElement('div');
            highlightBox.setAttribute(UI_ATTR, '1');
            highlightBox.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483645;border:2px solid #00E676;background:rgba(0,230,118,0.16);border-radius:4px;box-shadow:0 0 0 1px rgba(0,0,0,0.45),0 0 16px rgba(0,230,118,0.35);transition:all 60ms ease;display:none;';
            document.documentElement.appendChild(highlightBox);

            const toolbar = document.createElement('div');
            toolbar.setAttribute(UI_ATTR, '1');
            toolbar.style.cssText = 'position:fixed;bottom:20px;left:50%;transform:translateX(-50%);z-index:2147483647;background:#0F141B;color:#F8FAFC;border:1px solid #00E676;border-radius:12px;padding:10px 14px;display:flex;align-items:center;gap:10px;font-family:"Segoe UI",system-ui,sans-serif;font-size:12.5px;box-shadow:0 12px 32px rgba(0,0,0,0.65);max-width:92vw;';
            toolbar.innerHTML =
                '<span style="color:#00E676;font-weight:700;white-space:nowrap;">🎯 Chặn phần tử</span>' +
                '<span id="quangln-picker-sel" style="color:#CBD5E1;font-family:Consolas,monospace;font-size:11.5px;max-width:340px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">Di chuột và bấm vào quảng cáo cần chặn (ESC để hủy)</span>' +
                '<button id="quangln-picker-parent" style="display:none;background:#1E293B;color:#E2E8F0;border:1px solid #334155;border-radius:7px;padding:5px 10px;cursor:pointer;font-size:12px;font-weight:600;">Mở rộng (Cha)</button>' +
                '<button id="quangln-picker-block" style="display:none;background:#00E676;color:#05140B;border:none;border-radius:7px;padding:5px 12px;cursor:pointer;font-size:12px;font-weight:700;">Chặn phần tử này</button>' +
                '<button id="quangln-picker-cancel" style="background:#27272A;color:#F87171;border:1px solid #3F3F46;border-radius:7px;padding:5px 10px;cursor:pointer;font-size:12px;font-weight:600;">Hủy</button>';
            document.documentElement.appendChild(toolbar);

            const selLabel = toolbar.querySelector('#quangln-picker-sel');
            const btnParent = toolbar.querySelector('#quangln-picker-parent');
            const btnBlock = toolbar.querySelector('#quangln-picker-block');
            const btnCancel = toolbar.querySelector('#quangln-picker-cancel');

            function isPickerUi(el) {
                return !el || (el.closest && el.closest('[' + UI_ATTR + ']'));
            }

            function buildSelector(el) {
                if (!el || el.nodeType !== 1) return '';
                if (el.id && /^[A-Za-z_][A-Za-z0-9_-]{1,60}$/.test(el.id)) {
                    return '#' + el.id;
                }
                const tag = el.tagName.toLowerCase();
                const classes = Array.from(el.classList || [])
                    .filter(c => /^[A-Za-z_][A-Za-z0-9_-]{1,50}$/.test(c) && !c.startsWith('hover') && !c.startsWith('active'))
                    .slice(0, 2);
                if (classes.length > 0) {
                    return tag + '.' + classes.join('.');
                }
                const parent = el.parentElement;
                if (parent && parent !== document.body && parent !== document.documentElement) {
                    const parentSel = parent.id && /^[A-Za-z_][A-Za-z0-9_-]{1,60}$/.test(parent.id)
                        ? '#' + parent.id
                        : parent.tagName.toLowerCase();
                    return parentSel + ' > ' + tag;
                }
                return tag;
            }

            function updateHighlight(el, isLocked) {
                if (!el || el === document.body || el === document.documentElement) {
                    highlightBox.style.display = 'none';
                    return;
                }
                const r = el.getBoundingClientRect();
                highlightBox.style.display = 'block';
                highlightBox.style.left = Math.max(0, r.left) + 'px';
                highlightBox.style.top = Math.max(0, r.top) + 'px';
                highlightBox.style.width = Math.max(4, r.width) + 'px';
                highlightBox.style.height = Math.max(4, r.height) + 'px';
                highlightBox.style.borderColor = isLocked ? '#EF4444' : '#00E676';
                highlightBox.style.background = isLocked ? 'rgba(239,68,68,0.22)' : 'rgba(0,230,118,0.16)';

                const sel = buildSelector(el);
                selLabel.textContent = sel + ' (' + Math.round(r.width) + '×' + Math.round(r.height) + ')';
            }

            function onMouseMove(e) {
                if (lockedEl) return;
                const el = document.elementFromPoint(e.clientX, e.clientY);
                if (isPickerUi(el)) return;
                hoveredEl = el;
                updateHighlight(el, false);
            }

            function onClick(e) {
                if (isPickerUi(e.target)) return;
                e.preventDefault();
                e.stopPropagation();
                const el = document.elementFromPoint(e.clientX, e.clientY);
                if (!el || isPickerUi(el)) return;
                lockedEl = el;
                updateHighlight(lockedEl, true);
                btnParent.style.display = 'inline-block';
                btnBlock.style.display = 'inline-block';
            }

            function onKeyDown(e) {
                if (e.key === 'Escape') {
                    e.preventDefault();
                    cleanup();
                }
            }

            btnParent.addEventListener('click', function(e) {
                e.stopPropagation();
                if (lockedEl && lockedEl.parentElement && lockedEl.parentElement !== document.body && lockedEl.parentElement !== document.documentElement) {
                    lockedEl = lockedEl.parentElement;
                    updateHighlight(lockedEl, true);
                }
            });

            btnBlock.addEventListener('click', function(e) {
                e.stopPropagation();
                if (!lockedEl) return;
                const selector = buildSelector(lockedEl);
                const host = (location.hostname || '').replace(/^www\./i, '');
                if (selector) {
                    try {
                        document.querySelectorAll(selector).forEach(n => {
                            n.style.setProperty('display', 'none', 'important');
                            n.style.setProperty('visibility', 'hidden', 'important');
                        });
                    } catch (err) {
                        lockedEl.style.setProperty('display', 'none', 'important');
                    }

                    const rule = host ? (host + '##' + selector) : ('##' + selector);
                    const pickPayload = {
                        type: 'QUANGLN_ADBLOCK_PICK',
                        domain: host,
                        selector: selector,
                        rule: rule
                    };
                    if (window.chrome && window.chrome.webview) {
                        window.chrome.webview.postMessage(pickPayload);
                    } else if (window.CefSharp && typeof window.CefSharp.PostMessage === 'function') {
                        window.CefSharp.PostMessage(pickPayload);
                    }
                }
                cleanup();
            });

            btnCancel.addEventListener('click', function(e) {
                e.stopPropagation();
                cleanup();
            });

            function cleanup() {
                document.removeEventListener('mousemove', onMouseMove, true);
                document.removeEventListener('click', onClick, true);
                document.removeEventListener('keydown', onKeyDown, true);
                highlightBox.remove();
                toolbar.remove();
                window.__quanglnElementPickerCleanup = null;
            }

            window.__quanglnElementPickerCleanup = cleanup;
            document.addEventListener('mousemove', onMouseMove, true);
            document.addEventListener('click', onClick, true);
            document.addEventListener('keydown', onKeyDown, true);
        })();
        """;
}
