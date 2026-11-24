using System.Text.Json;

namespace QUANGLNBrowser.Engine;

/// <summary>
/// Script trích xuất và áp dụng dịch thuật trực tiếp trên DOM trang web (giữ nguyên phiên đăng nhập, form và URL).
/// Thiết kế hoàn toàn không dùng innerHTML để tương thích với các trang bật Trusted Types CSP.
/// </summary>
public static class PageTranslationScript
{
    public static string GetCollectAndShowProgressScript(string accentHex, string chromeHex)
    {
        string safeAccent = JsonSerializer.Serialize(accentHex);
        string safeChrome = JsonSerializer.Serialize(chromeHex);

        return $$"""
            (function() {
                const accent = {{safeAccent}};
                const chromeBg = {{safeChrome}};
                const BAR_ID = 'quangln-translate-bar';

                function ensureBar(statusText, showToggle, showErrorFallback) {
                    let bar = document.getElementById(BAR_ID);
                    if (!bar) {
                        bar = document.createElement('div');
                        bar.id = BAR_ID;
                        Object.assign(bar.style, {
                            position: 'fixed',
                            top: '14px',
                            right: '18px',
                            zIndex: '2147483647',
                            display: 'flex',
                            alignItems: 'center',
                            gap: '10px',
                            padding: '8px 12px',
                            borderRadius: '10px',
                            background: chromeBg,
                            color: '#F3F4F6',
                            border: '1px solid ' + accent,
                            boxShadow: '0 8px 28px rgba(0, 0, 0, 0.45)',
                            fontFamily: 'Segoe UI, system-ui, -apple-system, sans-serif',
                            fontSize: '12.5px',
                            lineHeight: '1.3'
                        });
                        (document.body || document.documentElement).appendChild(bar);
                    }

                    while (bar.firstChild) bar.removeChild(bar.firstChild);

                    const dot = document.createElement('span');
                    Object.assign(dot.style, {
                        width: '8px',
                        height: '8px',
                        borderRadius: '999px',
                        background: accent,
                        flexShrink: '0'
                    });
                    bar.appendChild(dot);

                    const label = document.createElement('span');
                    label.textContent = statusText;
                    label.style.fontWeight = '600';
                    bar.appendChild(label);

                    if (showToggle) {
                        const btnToggle = document.createElement('button');
                        btnToggle.type = 'button';
                        const isVi = window.__quanglnTranslationState === 'vi';
                        btnToggle.textContent = isVi ? 'Xem bản gốc' : 'Dịch sang Tiếng Việt';
                        Object.assign(btnToggle.style, {
                            padding: '4px 10px',
                            borderRadius: '6px',
                            border: '1px solid ' + accent,
                            background: isVi ? 'rgba(255,255,255,0.08)' : accent,
                            color: isVi ? '#F3F4F6' : '#05080B',
                            fontWeight: '600',
                            fontSize: '11.5px',
                            cursor: 'pointer'
                        });
                        btnToggle.addEventListener('click', function() {
                            if (window.__quanglnTogglePageTranslation) {
                                window.__quanglnTogglePageTranslation();
                            }
                        });
                        bar.appendChild(btnToggle);
                    }

                    if (showErrorFallback && location.protocol.startsWith('http')) {
                        const btnGoogle = document.createElement('button');
                        btnGoogle.type = 'button';
                        btnGoogle.textContent = 'Mở Google Dịch';
                        Object.assign(btnGoogle.style, {
                            padding: '4px 10px',
                            borderRadius: '6px',
                            border: '1px solid ' + accent,
                            background: accent,
                            color: '#05080B',
                            fontWeight: '600',
                            fontSize: '11.5px',
                            cursor: 'pointer'
                        });
                        btnGoogle.addEventListener('click', function() {
                            location.href = 'https://translate.google.com/translate?sl=auto&tl=vi&u=' + encodeURIComponent(location.href);
                        });
                        bar.appendChild(btnGoogle);
                    }

                    const btnClose = document.createElement('button');
                    btnClose.type = 'button';
                    btnClose.textContent = '✕';
                    Object.assign(btnClose.style, {
                        padding: '2px 6px',
                        borderRadius: '6px',
                        border: 'none',
                        background: 'transparent',
                        color: '#9CA3AF',
                        fontSize: '12px',
                        cursor: 'pointer'
                    });
                    btnClose.addEventListener('click', function() {
                        bar.remove();
                    });
                    bar.appendChild(btnClose);
                }

                window.__quanglnEnsureTranslateBar = ensureBar;

                if (!window.__quanglnTranslateStore) {
                    window.__quanglnTranslateStore = {
                        nodeSet: new WeakSet(),
                        nodes: [],
                        attrMap: new WeakMap(),
                        attrs: [],
                        origTitle: document.title || '',
                        cachedMap: Object.create(null)
                    };
                }

                const store = window.__quanglnTranslateStore;
                const letterRegex = /[A-Za-z\u00C0-\u024F\u1E00-\u1EFF\u0400-\u04FF\u4E00-\u9FFF\u3040-\u30FF\uAC00-\uD7AF]/;
                const skipTags = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEXTAREA', 'CODE', 'PRE', 'SVG']);

                function replacePreservePadding(orig, trimmed, replacement) {
                    const idx = orig.indexOf(trimmed);
                    if (idx < 0) return replacement;
                    return orig.slice(0, idx) + replacement + orig.slice(idx + trimmed.length);
                }

                window.__quanglnApplyStoredTranslations = function(toVietnamese) {
                    window.__quanglnTranslationState = toVietnamese ? 'vi' : 'orig';
                    for (const item of store.nodes) {
                        if (!item.node || !item.node.isConnected) continue;
                        if (toVietnamese) {
                            const tr = store.cachedMap[item.trimmed];
                            item.node.nodeValue = tr ? replacePreservePadding(item.orig, item.trimmed, tr) : item.orig;
                        } else {
                            item.node.nodeValue = item.orig;
                        }
                    }
                    for (const item of store.attrs) {
                        if (!item.el || !item.el.isConnected) continue;
                        const nextVal = toVietnamese
                            ? (store.cachedMap[item.trimmed] ? replacePreservePadding(item.orig, item.trimmed, store.cachedMap[item.trimmed]) : item.orig)
                            : item.orig;
                        if (item.attr === 'placeholder') item.el.placeholder = nextVal;
                        else if (item.attr === 'value') item.el.value = nextVal;
                        else item.el.setAttribute(item.attr, nextVal);
                    }
                    if (store.origTitle) {
                        const titleTrim = store.origTitle.trim();
                        document.title = toVietnamese && store.cachedMap[titleTrim]
                            ? store.cachedMap[titleTrim]
                            : store.origTitle;
                    }
                };

                window.__quanglnTogglePageTranslation = function() {
                    const nextToVi = window.__quanglnTranslationState !== 'vi';
                    window.__quanglnApplyStoredTranslations(nextToVi);
                    ensureBar(
                        nextToVi ? 'Đã dịch sang Tiếng Việt' : 'Đang hiển thị ngôn ngữ gốc',
                        true,
                        false
                    );
                };

                const root = document.body || document.documentElement;
                const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
                let currentNode;
                const uniqueTexts = [];
                const seen = new Set();

                while ((currentNode = walker.nextNode())) {
                    if (store.nodes.length >= 900) break;
                    const parent = currentNode.parentElement;
                    if (!parent || skipTags.has(parent.tagName)) continue;
                    if (parent.closest && parent.closest('#' + BAR_ID)) continue;

                    const raw = currentNode.nodeValue || '';
                    const trimmed = raw.trim();
                    if (trimmed.length < 2 || !letterRegex.test(trimmed)) continue;

                    if (!store.nodeSet.has(currentNode)) {
                        store.nodeSet.add(currentNode);
                        store.nodes.push({ node: currentNode, orig: raw, trimmed: trimmed });
                    }

                    if (!store.cachedMap[trimmed] && !seen.has(trimmed)) {
                        seen.add(trimmed);
                        uniqueTexts.push(trimmed);
                    }
                }

                const attrElements = root.querySelectorAll('input[placeholder], textarea[placeholder], input[type="submit"][value], input[type="button"][value], [title]');
                for (const el of attrElements) {
                    if (el.closest && el.closest('#' + BAR_ID)) continue;
                    let trackedAttrs = store.attrMap.get(el);
                    if (!trackedAttrs) {
                        trackedAttrs = new Set();
                        store.attrMap.set(el, trackedAttrs);
                    }

                    const candidates = [];
                    if (el.hasAttribute('placeholder')) candidates.push(['placeholder', el.getAttribute('placeholder')]);
                    if ((el.type === 'submit' || el.type === 'button') && el.value) candidates.push(['value', el.value]);
                    if (el.hasAttribute('title')) candidates.push(['title', el.getAttribute('title')]);

                    for (const [attr, raw] of candidates) {
                        const trimmed = (raw || '').trim();
                        if (trimmed.length < 2 || !letterRegex.test(trimmed)) continue;
                        if (!trackedAttrs.has(attr)) {
                            trackedAttrs.add(attr);
                            store.attrs.push({ el: el, attr: attr, orig: raw, trimmed: trimmed });
                        }
                        if (!store.cachedMap[trimmed] && !seen.has(trimmed)) {
                            seen.add(trimmed);
                            uniqueTexts.push(trimmed);
                        }
                    }
                }

                const cleanTitle = (store.origTitle || '').trim();
                if (cleanTitle.length >= 2 && letterRegex.test(cleanTitle) && !store.cachedMap[cleanTitle] && !seen.has(cleanTitle)) {
                    seen.add(cleanTitle);
                    uniqueTexts.push(cleanTitle);
                }

                if (uniqueTexts.length === 0 && Object.keys(store.cachedMap).length > 0) {
                    window.__quanglnApplyStoredTranslations(true);
                    ensureBar('Đã dịch sang Tiếng Việt', true, false);
                    return JSON.stringify({ alreadyDone: true, texts: [] });
                }

                ensureBar('Đang dịch sang Tiếng Việt...', false, false);
                return JSON.stringify({ alreadyDone: false, texts: uniqueTexts });
            })();
            """;
    }

    public static string GetApplyTranslationsScript(string translationsMapJson)
    {
        return $$"""
            (function() {
                const map = {{translationsMapJson}};
                const store = window.__quanglnTranslateStore;
                if (!store) return 0;

                for (const key in map) {
                    if (Object.prototype.hasOwnProperty.call(map, key) && map[key]) {
                        store.cachedMap[key] = map[key];
                    }
                }

                if (window.__quanglnApplyStoredTranslations) {
                    window.__quanglnApplyStoredTranslations(true);
                }
                if (window.__quanglnEnsureTranslateBar) {
                    window.__quanglnEnsureTranslateBar('Đã dịch sang Tiếng Việt', true, false);
                }
                return store.nodes.length + store.attrs.length;
            })();
            """;
    }

    public static string GetShowErrorScript(string statusMessage)
    {
        string safeMessage = JsonSerializer.Serialize(statusMessage);
        return $$"""
            (function() {
                if (window.__quanglnEnsureTranslateBar) {
                    window.__quanglnEnsureTranslateBar({{safeMessage}}, false, true);
                }
            })();
            """;
    }
}
