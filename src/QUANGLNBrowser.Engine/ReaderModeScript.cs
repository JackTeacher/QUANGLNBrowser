namespace QUANGLNBrowser.Engine;

/// <summary>
/// Script bật/tắt Chế độ đọc (Reader Mode — Alt+Shift+R) sạch quảng cáo, an toàn với Trusted Types CSP (không dùng innerHTML).
/// </summary>
public static class ReaderModeScript
{
    public static string GetToggleScript() => """
        (function() {
            const OVERLAY_ID = 'quangln-reader-mode-overlay';
            const existing = document.getElementById(OVERLAY_ID);
            if (existing) {
                existing.remove();
                document.documentElement.style.overflow = '';
                return 'closed';
            }

            const title = (document.querySelector('h1')?.innerText || document.title || location.hostname).trim();

            function findMainContainer() {
                const candidates = [
                    ...document.querySelectorAll('article, main, [role="main"], .article-content, .post-content, .entry-content, .fck_detail, .singular-content')
                ];
                let best = null;
                let bestScore = 0;
                for (const el of candidates) {
                    const textLen = (el.innerText || '').trim().length;
                    if (textLen > bestScore) {
                        bestScore = textLen;
                        best = el;
                    }
                }
                return bestScore >= 200 ? best : document.body;
            }

            const sourceRoot = findMainContainer();
            const blocks = [];
            const nodes = sourceRoot.querySelectorAll('h1, h2, h3, h4, p, blockquote, li, pre');
            const seen = new Set();

            for (const n of nodes) {
                if (n.closest('nav, header, footer, aside, [role="navigation"], script, style, noscript')) continue;
                const txt = (n.innerText || '').replace(/\s+/g, ' ').trim();
                if (!txt || txt.length < 20 || seen.has(txt)) continue;
                seen.add(txt);
                blocks.push({ tag: n.tagName.toLowerCase(), text: txt });
            }

            if (blocks.length === 0) {
                const raw = (document.body?.innerText || '').split(/\n+/).map(s => s.trim()).filter(s => s.length > 20);
                for (const line of raw.slice(0, 120)) {
                    blocks.push({ tag: 'p', text: line });
                }
            }

            const overlay = document.createElement('div');
            overlay.id = OVERLAY_ID;
            let isDark = true;
            let fontSize = 18;

            function applyTheme() {
                overlay.style.background = isDark ? '#15161c' : '#faf8f4';
                overlay.style.color = isDark ? '#e5e7eb' : '#1f2937';
                toolbar.style.background = isDark ? 'rgba(28, 30, 38, 0.96)' : 'rgba(240, 238, 232, 0.96)';
                toolbar.style.borderBottom = isDark ? '1px solid #2e3242' : '1px solid #d6d3cb';
                article.style.fontSize = fontSize + 'px';
            }

            overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483647;overflow-y:auto;font-family:Georgia,"Segoe UI",serif;line-height:1.8;';

            const toolbar = document.createElement('div');
            toolbar.style.cssText = 'position:sticky;top:0;display:flex;align-items:center;justify-content:space-between;padding:10px 24px;font-family:"Segoe UI",sans-serif;z-index:2;';

            const brand = document.createElement('div');
            brand.textContent = '📖 Chế độ đọc — QUANGLN Browser';
            brand.style.cssText = 'font-size:13px;font-weight:700;color:#38bdf8;';

            const controls = document.createElement('div');
            controls.style.cssText = 'display:flex;gap:8px;align-items:center;';

            function makeBtn(label, onClick) {
                const b = document.createElement('button');
                b.type = 'button';
                b.textContent = label;
                b.style.cssText = 'background:#2b2f3f;color:#f3f4f6;border:1px solid #3f455a;border-radius:7px;padding:5px 11px;font-size:12px;font-weight:600;cursor:pointer;';
                b.addEventListener('click', onClick);
                return b;
            }

            controls.appendChild(makeBtn('A−', () => { fontSize = Math.max(14, fontSize - 2); applyTheme(); }));
            controls.appendChild(makeBtn('A+', () => { fontSize = Math.min(28, fontSize + 2); applyTheme(); }));
            controls.appendChild(makeBtn('☀/🌙 Giao diện', () => { isDark = !isDark; applyTheme(); }));
            controls.appendChild(makeBtn('✕ Đóng (Alt+Shift+R)', () => {
                overlay.remove();
                document.documentElement.style.overflow = '';
            }));

            toolbar.appendChild(brand);
            toolbar.appendChild(controls);

            const article = document.createElement('article');
            article.style.cssText = 'max-width:760px;margin:32px auto 80px auto;padding:0 24px;';

            const h1 = document.createElement('h1');
            h1.textContent = title;
            h1.style.cssText = 'font-size:1.8em;line-height:1.3;margin:0 0 10px 0;font-family:"Segoe UI",sans-serif;';
            article.appendChild(h1);

            const meta = document.createElement('div');
            meta.textContent = location.href;
            meta.style.cssText = 'font-size:12px;color:#94a3b8;margin-bottom:26px;font-family:"Segoe UI",sans-serif;word-break:break-all;';
            article.appendChild(meta);

            for (const item of blocks) {
                const tag = (item.tag === 'h1' || item.tag === 'h2' || item.tag === 'h3' || item.tag === 'h4') ? 'h2' : (item.tag === 'blockquote' ? 'blockquote' : 'p');
                const el = document.createElement(tag);
                el.textContent = item.text;
                if (tag === 'h2') {
                    el.style.cssText = 'font-size:1.25em;margin:26px 0 10px 0;font-family:"Segoe UI",sans-serif;';
                } else if (tag === 'blockquote') {
                    el.style.cssText = 'border-left:3px solid #38bdf8;margin:18px 0;padding:6px 16px;opacity:0.9;font-style:italic;';
                } else {
                    el.style.cssText = 'margin:0 0 16px 0;';
                }
                article.appendChild(el);
            }

            overlay.appendChild(toolbar);
            overlay.appendChild(article);
            applyTheme();
            document.documentElement.style.overflow = 'hidden';
            document.documentElement.appendChild(overlay);
            return 'opened';
        })();
        """;

    public static string GetExtractReadableTextScript() => """
        (function() {
            const title = (document.querySelector('h1')?.innerText || document.title || '').trim();
            const root = document.querySelector('article, main, [role="main"]') || document.body;
            const text = (root?.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 14000);
            return JSON.stringify({ title, text, url: location.href });
        })();
        """;
}
