using Microsoft.AspNetCore.Components;

namespace BackWave.Dashboard.Components;

// Code-behind for DashboardLayout. The inline client scripts and SVG glyphs are C# raw string
// literals; they live here rather than in the .razor @code block so the C# compiler (not the
// per-target-framework Razor parser) reads them, which keeps the component compiling across every
// shipped TFM. The Razor markup partial sees these members by name.

/// <summary>The dashboard's outer shell: brand, sidebar navigation, topbar, and the content frame,
/// rendered as a full server-side HTML document with the design system inlined (no static assets to
/// host). Not consumer API — the dashboard middleware renders it.</summary>
public partial class DashboardLayout
{
    /// <summary>Path the dashboard is mounted at (the request PathBase), e.g. <c>/backwave</c>.</summary>
    [Parameter, EditorRequired] public string BasePath { get; set; } = "";

    /// <summary>Page title; suffixed with the product name in the document title.</summary>
    [Parameter, EditorRequired] public string Title { get; set; } = "";

    /// <summary>Nav key of the active section, so its item is highlighted.</summary>
    [Parameter] public string Active { get; set; } = "";

    /// <summary>The page body rendered inside the content frame (and, in live mode, inside the
    /// SSE-swapped region).</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>When true, renders only <see cref="ChildContent"/> (the #bw-live inner markup) —
    /// the fragment pushed over SSE. No document, no chrome, no script.</summary>
    [Parameter] public bool ContentOnly { get; set; }

    /// <summary>When true (full-document mode), wraps the content in #bw-live and inlines the
    /// EventSource client that streams updates into it. Off ⇒ a plain server-rendered page.</summary>
    [Parameter] public bool Live { get; set; }

    // Registered dashboard extensions, resolved from DI. Empty when no extension package is
    // installed (IEnumerable<T> resolves to none), so the shell renders byte-identically then.
    [Inject] private IEnumerable<IDashboardExtension> Extensions { get; set; } = [];

    // The built-in entries followed by the contributed ones, each contributed entry inserted right
    // after the built-in whose Key matches its After anchor (appended when null/unmatched). Skips a
    // null an extension might return so one misbehaving extension can't break the sidebar.
    private List<NavLink> MergedNav()
    {
        var merged = NavItems.Select(i => new NavLink(i.Key, i.Label, i.Href, Icon(i.Key))).ToList();
        foreach (var entry in Extensions.SelectMany(e => e.NavEntries() ?? []))
        {
            var link = new NavLink(entry.Key, entry.Label, entry.Href,
                entry.Icon is { } svg ? (MarkupString)svg : default);
            var anchor = entry.After is { } after ? merged.FindIndex(n => n.Key == after) : -1;
            if (anchor >= 0)
            {
                merged.Insert(anchor + 1, link);
            }
            else
            {
                merged.Add(link);
            }
        }
        return merged;
    }

    // Non-null banner fragments contributed by extensions, in registration order.
    private IEnumerable<string> ExtensionBanners()
        => Extensions.Select(e => e.Banner(BasePath)).OfType<string>();

    private record NavItem(string Key, string Label, string Href);

    // A rendered sidebar link: a built-in or contributed entry reduced to its display fields plus
    // the inline icon markup to draw beside the label.
    private record NavLink(string Key, string Label, string Href, MarkupString IconMarkup);

    // Runs in <head> before first paint: apply the stored theme (or fall back to the OS
    // preference) so the page never flashes the wrong palette. Sets data-theme on <html>,
    // which the design system's [data-theme="dark"] rules key off.
    private const string ThemeBootScript =
        """
        <script>
        (function () {
            try {
                var t = localStorage.getItem('bw-theme');
                if (t === 'dark' || (!t && window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches)) {
                    document.documentElement.setAttribute('data-theme', 'dark');
                }
            } catch (e) {}
        })();
        </script>
        """;

    // Wires the topbar toggle: flip data-theme on <html> and persist the choice. The SSE client
    // only swaps #bw-live, so the theme set here survives live updates; full navigations re-apply
    // it via ThemeBootScript before paint.
    private const string ThemeToggleScript =
        """
        <script>
        (function () {
            var btn = document.getElementById('bw-theme-toggle');
            if (!btn) return;
            var dark = document.documentElement.getAttribute('data-theme') === 'dark';
            btn.setAttribute('aria-pressed', dark);
            btn.addEventListener('click', function () {
                dark = !dark;
                if (dark) document.documentElement.setAttribute('data-theme', 'dark');
                else document.documentElement.removeAttribute('data-theme');
                btn.setAttribute('aria-pressed', dark);
                try { localStorage.setItem('bw-theme', dark ? 'dark' : 'light'); } catch (e) {}
            });
        })();
        </script>
        """;

    // Copy buttons: one delegated listener for every [data-bw-copy] on the page. Copies the
    // sibling <pre>'s textContent — for highlighted JSON that is exactly the pretty-printed text,
    // since the highlight spans add no characters. Delegation on document means the handler keeps
    // working after the SSE client swaps #bw-live. Progressive enhancement: no JS, no button effect,
    // but the <pre> still shows. execCommand is the fallback where the async clipboard is blocked.
    private const string CopyScript =
        """
        <script>
        (function () {
            function flash(btn) {
                btn.classList.add('is-copied');
                setTimeout(function () { btn.classList.remove('is-copied'); }, 1600);
            }
            document.addEventListener('click', function (e) {
                var btn = e.target.closest ? e.target.closest('[data-bw-copy]') : null;
                if (!btn) return;
                var block = btn.closest('.bw-codeblock');
                var pre = block ? block.querySelector('pre') : null;
                if (!pre) return;
                var text = pre.textContent;
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(text).then(function () { flash(btn); }, function () {});
                    return;
                }
                try {
                    var ta = document.createElement('textarea');
                    ta.value = text;
                    ta.style.position = 'fixed';
                    ta.style.opacity = '0';
                    document.body.appendChild(ta);
                    ta.select();
                    document.execCommand('copy');
                    document.body.removeChild(ta);
                    flash(btn);
                } catch (err) {}
            });
        })();
        </script>
        """;

    // Tag Suggest dropdown (issues 0214/0215) — the dashboard's only client-side fetch. Progressive
    // enhancement: each [data-bw-suggest] input type-ahead-queries the JSON suggest endpoint. Absent
    // JS/fetch, the input is inert and the facet card + form still filter.
    //
    // Two-stage (ADR 0042). STAGE ONE sends no key param (Key=null) so the endpoint mixes Label
    // suggestions (key="") with KEY drill-ins (value=""). Picking a Label navigates to a has-label
    // filter (tl=); picking a key enters STAGE TWO for that key. STAGE TWO sends key=<selected> so the
    // endpoint returns that key's values; picking one navigates to a has-key/value filter — a
    // POSITIONAL tk=&tv= pair (matching TagFilterUrl), so a colon inside a value never corrupts the
    // pair. Escape backs stage two out to stage one; from stage one it dismisses the dropdown. A
    // breadcrumb chip (key ›) shows the active key and clicks back out.
    //
    // Virtualized by scroll (keyset cursor): each window is <max> rows; scrolling near the bottom
    // fetches the next window with ak/av set to the last suggestion's (key,value) and APPENDS it, so an
    // operator browses an arbitrarily large value set a window at a time. When a fresh window doesn't
    // overflow the menu, the next window is pulled eagerly so the scroll affordance can appear at all.
    // A single sequence number tags every fetch: a stale window (superseded by newer typing or a
    // stage change) is dropped, and an in-flight guard blocks duplicate concurrent windows.
    //
    // Keyboard (ADR 0043) — the hand-written WAI-ARIA combobox slice. The markup ships the static
    // role="combobox"/listbox scaffolding; this script tags each pill role="option" with a unique id,
    // then Arrow/Home/End move an active-descendant highlight (aria-activedescendant + .is-active),
    // Enter fires the active pill's own .click() (so drill-in and navigation stay one code path), and
    // aria-expanded tracks the menu. No round-trip: the highlight is pure client state.
    private const string TagSuggestScript =
        """
        <script>
        (function () {
            if (!window.fetch) return;
            document.querySelectorAll('[data-bw-suggest]').forEach(function (box) {
                var input = box.querySelector('[data-bw-suggest-input]');
                var menu = box.querySelector('[data-bw-suggest-menu]');
                if (!input || !menu) return;
                var endpoint = box.getAttribute('data-bw-suggest');
                var base = box.getAttribute('data-bw-suggest-base') || '';
                var windowSize = parseInt(box.getAttribute('data-bw-suggest-window'), 10) || 20;
                var timer = null, seq = 0, optSeq = 0;
                var activeKey = null;   // null ⇒ stage one; a key string ⇒ stage two under that key
                var items = [];         // the suggestions currently shown (the paging cursor lives at its tail)
                var loading = false, exhausted = false;
                var activeIdx = -1;     // keyboard-highlighted option, -1 ⇒ none (aria-activedescendant)

                function sep() { return base.indexOf('?') === -1 ? '?' : '&'; }
                function labelHref(v) { return base + sep() + 'tl=' + encodeURIComponent(v); }
                function keyValueHref(k, v) {
                    return base + sep() + 'tk=' + encodeURIComponent(k) + '&tv=' + encodeURIComponent(v);
                }

                function reset() { activeKey = null; items = []; loading = false; exhausted = false; activeIdx = -1; }
                function hide() {
                    menu.hidden = true;
                    menu.innerHTML = '';
                    input.setAttribute('aria-expanded', 'false');
                    input.removeAttribute('aria-activedescendant');
                    reset();
                }

                function optionEls() { return menu.querySelectorAll('[role="option"]'); }

                // Move the active-descendant highlight to option `idx` (clamped into range): repaint
                // aria-selected/.is-active, point the input's aria-activedescendant at it, and keep it
                // scrolled into view (which, near the tail, lets the scroll pager pull the next window).
                function setActive(idx) {
                    var list = optionEls();
                    if (!list.length) return;
                    idx = idx < 0 ? 0 : (idx >= list.length ? list.length - 1 : idx);
                    var cur = menu.querySelector('[role="option"][aria-selected="true"]');
                    if (cur) { cur.setAttribute('aria-selected', 'false'); cur.classList.remove('is-active'); }
                    var el = list[idx];
                    el.setAttribute('aria-selected', 'true');
                    el.classList.add('is-active');
                    input.setAttribute('aria-activedescendant', el.id);
                    el.scrollIntoView({ block: 'nearest' });
                    activeIdx = idx;
                }

                function note(text) {
                    var n = document.createElement('span');
                    n.className = 'bw-suggest__note';
                    n.textContent = text;
                    return n;
                }

                function crumb() {
                    var row = document.createElement('span');
                    row.className = 'bw-suggest__crumb';
                    var a = document.createElement('a');
                    a.className = 'bw-tag bw-tag--active';
                    a.href = '#';
                    a.textContent = activeKey + ' ›';
                    a.title = 'Back to all tags (Esc)';
                    a.addEventListener('click', function (e) { e.preventDefault(); backOut(); });
                    row.appendChild(a);
                    return row;
                }

                function pill(it) {
                    var a = document.createElement('a');
                    a.className = 'bw-tag';
                    a.setAttribute('role', 'option');
                    a.id = menu.id + '-opt-' + (optSeq++);   // stable, unique target for aria-activedescendant
                    a.setAttribute('aria-selected', 'false');
                    if (it.value === '' && it.key !== '') {
                        // Stage-one KEY drill-in: enter stage two rather than navigate.
                        a.href = '#';
                        a.textContent = it.key + ' ›';
                        a.addEventListener('click', function (e) { e.preventDefault(); drillInto(it.key); });
                    } else if (it.key === '') {
                        a.href = labelHref(it.value);
                        a.textContent = it.value;
                    } else {
                        // Stage-two value (the key is on the breadcrumb): show just the value.
                        a.href = keyValueHref(it.key, it.value);
                        a.textContent = it.value;
                    }
                    return a;
                }

                function render(data, append) {
                    if (!append) {
                        menu.innerHTML = '';
                        menu.scrollTop = 0;
                        activeIdx = -1;                              // a fresh list drops any prior highlight
                        input.removeAttribute('aria-activedescendant');
                        if (activeKey !== null) menu.appendChild(crumb());
                        if (!data.length) {
                            menu.appendChild(note(activeKey !== null ? 'No matching values' : 'No matching Labels or keys'));
                            menu.hidden = false;
                            input.setAttribute('aria-expanded', 'true');
                            return;
                        }
                    }
                    data.forEach(function (it) { menu.appendChild(pill(it)); });
                    menu.hidden = false;
                    input.setAttribute('aria-expanded', 'true');
                    maybeFill();
                }

                // If a window doesn't overflow the menu, the scroll event can never fire, so pull the
                // next window eagerly until the menu overflows or the set is exhausted.
                function maybeFill() {
                    if (exhausted || loading || menu.hidden || items.length === 0) return;
                    if (menu.scrollHeight <= menu.clientHeight + 4) fetchWindow(true);
                }

                function fetchWindow(append) {
                    var mine = ++seq;
                    loading = true;
                    var url = endpoint + '?';
                    if (activeKey !== null) url += 'key=' + encodeURIComponent(activeKey) + '&';
                    url += 'prefix=' + encodeURIComponent(input.value) + '&max=' + windowSize;
                    if (append && items.length) {
                        var last = items[items.length - 1];
                        url += '&ak=' + encodeURIComponent(last.key) + '&av=' + encodeURIComponent(last.value);
                    }
                    fetch(url, { headers: { 'Accept': 'application/json' } })
                        .then(function (r) { return r.ok ? r.json() : []; })
                        .then(function (data) {
                            if (mine !== seq) return;   // superseded by newer typing or a stage change
                            data = data || [];
                            loading = false;
                            if (data.length < windowSize) exhausted = true;
                            items = append ? items.concat(data) : data;
                            render(data, append);
                        })
                        .catch(function () { if (mine === seq) { loading = false; hide(); } });
                }

                function fresh() { exhausted = false; items = []; fetchWindow(false); }

                function drillInto(key) { activeKey = key; input.value = ''; fresh(); input.focus(); }
                function backOut() { activeKey = null; input.value = ''; fresh(); input.focus(); }

                input.addEventListener('input', function () {
                    if (timer) clearTimeout(timer);
                    timer = setTimeout(function () {
                        // Empty prefix in stage one hides; in stage two it lists every value under the key.
                        if (input.value === '' && activeKey === null) { hide(); return; }
                        fresh();
                    }, 150);
                });
                input.addEventListener('keydown', function (e) {
                    if (e.key === 'Escape') {
                        // Stage two backs out to stage one; stage one dismisses the menu.
                        if (activeKey !== null) { e.preventDefault(); backOut(); } else hide();
                        return;
                    }
                    if (menu.hidden) return;
                    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(activeIdx + 1); }
                    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(activeIdx < 0 ? optionEls().length - 1 : activeIdx - 1); }
                    else if (e.key === 'Home') { e.preventDefault(); setActive(0); }
                    else if (e.key === 'End') { e.preventDefault(); setActive(optionEls().length - 1); }
                    else if (e.key === 'Enter') {
                        // Fire the highlighted pill's own handler/navigation — the same as a mouse click.
                        var list = optionEls();
                        if (activeIdx >= 0 && list[activeIdx]) { e.preventDefault(); list[activeIdx].click(); }
                    }
                });
                menu.addEventListener('scroll', function () {
                    if (loading || exhausted || items.length === 0 || menu.hidden) return;
                    if (menu.scrollTop + menu.clientHeight >= menu.scrollHeight - 24) fetchWindow(true);
                });
                document.addEventListener('click', function (e) { if (!box.contains(e.target)) hide(); });
            });
        })();
        </script>
        """;

    // The dashboard's one client-side script: open a single SSE connection to this same URL
    // (?live=1) and swap #bw-live with each pushed fragment. Progressive enhancement — absent
    // EventSource (or JS), the server-rendered page stands on its own.
    //
    // The stream is closed on pagehide. An open EventSource holds an HTTP/1.1 socket, and the
    // dashboard navigates between sections as full-page loads; a stream left dangling across a
    // navigation lingers in the ~6-per-origin connection pool, so the next section's document GET
    // sits queued behind it (stalled, then cancelled on the next click). Closing on the way out
    // frees the socket immediately; pageshow reopens it if the page comes back from the bfcache.
    private const string LiveScript =
        """
        <script>
        (function () {
            if (!window.EventSource) return;
            var url = new URL(window.location.href);
            url.searchParams.set('live', '1');
            var es = null;
            function open() {
                es = new EventSource(url.toString());
                es.addEventListener('update', function (e) {
                    var region = document.getElementById('bw-live');
                    if (!region) return;
                    // Swapping the region's innerHTML momentarily shrinks the document, so a page
                    // scrolled past the new (smaller) height gets clamped to the top mid-swap. Only
                    // bites where the page is tall enough to scroll (narrow viewports). Capture the
                    // offset and restore it after the swap so a live tick never yanks the reader up.
                    var x = window.scrollX, y = window.scrollY;
                    region.innerHTML = e.data;
                    if (window.scrollX !== x || window.scrollY !== y) window.scrollTo(x, y);
                });
            }
            function close() {
                if (es) { es.close(); es = null; }
            }
            open();
            window.addEventListener('pagehide', close);
            window.addEventListener('pageshow', function (e) { if (e.persisted && !es) open(); });
        })();
        </script>
        """;

    // Only sections that exist today are linked — the nav never points at an empty page.
    private static readonly NavItem[] NavItems =
    [
        new("overview", "Overview", "/"),
        new("executing", "Executing now", "/executing"),
        new("jobs", "Jobs", "/jobs"),
        new("queues", "Queues", "/queues"),
        new("failures", "Failures", "/failures"),
        new("observers", "Observers", "/observers"),
        new("schedules", "Recurring Schedules", "/schedules"),
    ];

    // Browser-tab favicon so the tab shows the BackWave mark instead of the generic globe. The
    // dashboard hosts no static files, so the icon rides in <head> as an inline SVG data URI: the
    // brand azure squircle with the white "B" monogram (the same mark as assets/icon.svg). A
    // data-URI SVG loads as a standalone image with no CSS context, so currentColor can't be used
    // here — the colours are literal. Built once at type-load and URL-encoded so it is a valid URI.
    private const string FaviconSvg =
        """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128"><rect width="128" height="128" rx="28" fill="#2D72F0"/><svg x="24" y="24" width="80" height="80" viewBox="-12 -12 528 525"><path fill="#FFFFFF" fill-rule="evenodd" d="M2.45,498.55C1.03,497.12 0,494.93 0,493.32C0,491.8 2.24,481.99 4.97,471.53C7.71,461.06 13.14,440.12 17.04,425C42.28,327.17 42.12,327.71 50.61,310.74C69.27,273.47 98.5,248.58 136.69,237.46C155.53,231.98 154.64,232.01 284.5,232.01C365.57,232.01 407.42,232.36 413.5,233.1C441.47,236.52 463.98,246.68 480.54,263.39C490.68,273.62 497.15,284.56 501.02,298.04C504.37,309.72 504.82,329.51 502.05,342.92C499.72,354.16 493.04,372.17 487.24,382.85C456.69,439.05 394.69,481.83 322.54,496.49C301.3,500.8 294.38,500.98 146.2,500.99L4.91,501L2.45,498.55ZM24.63,184.93C22.93,183.59 22,181.88 22,180.1C22,178.59 26.26,158.26 31.46,134.92C47.18,64.44 55.02,29.1 57.62,17.03C60.19,5.11 61.31,2.94 65.85,1.06C67.83,0.24 113.39,0.03 234.53,0.28C386.32,0.6 401.23,0.77 409,2.34C457.51,12.1 490.51,43.09 496.63,84.63C500.42,110.3 492.41,135.13 474.17,154.22C462.11,166.84 447.64,175.58 429.17,181.4C410.69,187.21 419.12,186.99 214.88,187L27.27,187L24.63,184.93Z"/></svg></svg>
        """;

    // The <link> tag for the head — the favicon SVG collapsed to one line and percent-encoded.
    private static readonly MarkupString FaviconLink = (MarkupString)
        $"""<link rel="icon" href="data:image/svg+xml,{Uri.EscapeDataString(FaviconSvg)}" />""";

    // The BackWave "B" monogram: two stacked rounded lobes. Inlined like the nav icons (zero
    // static assets). Single-colour via currentColor so the mark inherits the brand ink, the
    // same treatment as the wordmark beside it.
    private static readonly MarkupString Logo = (MarkupString)
        """
        <svg width="22" height="22" viewBox="-12 -12 528 525" fill="none" role="img" aria-label="BackWave"><path fill="currentColor" fill-rule="evenodd" d="M2.45,498.55C1.03,497.12 0,494.93 0,493.32C0,491.8 2.24,481.99 4.97,471.53C7.71,461.06 13.14,440.12 17.04,425C42.28,327.17 42.12,327.71 50.61,310.74C69.27,273.47 98.5,248.58 136.69,237.46C155.53,231.98 154.64,232.01 284.5,232.01C365.57,232.01 407.42,232.36 413.5,233.1C441.47,236.52 463.98,246.68 480.54,263.39C490.68,273.62 497.15,284.56 501.02,298.04C504.37,309.72 504.82,329.51 502.05,342.92C499.72,354.16 493.04,372.17 487.24,382.85C456.69,439.05 394.69,481.83 322.54,496.49C301.3,500.8 294.38,500.98 146.2,500.99L4.91,501L2.45,498.55ZM24.63,184.93C22.93,183.59 22,181.88 22,180.1C22,178.59 26.26,158.26 31.46,134.92C47.18,64.44 55.02,29.1 57.62,17.03C60.19,5.11 61.31,2.94 65.85,1.06C67.83,0.24 113.39,0.03 234.53,0.28C386.32,0.6 401.23,0.77 409,2.34C457.51,12.1 490.51,43.09 496.63,84.63C500.42,110.3 492.41,135.13 474.17,154.22C462.11,166.84 447.64,175.58 429.17,181.4C410.69,187.21 419.12,186.99 214.88,187L27.27,187L24.63,184.93Z"/></svg>
        """;

    // Nav glyphs from the Hugeicons free set (stroke-rounded, 24×24), inlined like the rest of
    // the dashboard's SVG (zero static assets). currentColor lets them inherit the nav ink. The
    // short stroke-width:2 segments are Hugeicons' dot trick (a near-zero-length round-cap stroke).
    private static MarkupString Icon(string key) => (MarkupString)(key switch
    {
        "overview" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M15.0002 17C14.2007 17.6224 13.1504 18 12.0002 18C10.8499 18 9.79971 17.6224 9.00018 17"/><path d="M2.35157 13.2135C1.99855 10.9162 1.82204 9.76763 2.25635 8.74938C2.69065 7.73112 3.65421 7.03443 5.58132 5.64106L7.02117 4.6C9.41847 2.86667 10.6171 2 12.0002 2C13.3832 2 14.5819 2.86667 16.9792 4.6L18.419 5.64106C20.3462 7.03443 21.3097 7.73112 21.744 8.74938C22.1783 9.76763 22.0018 10.9162 21.6488 13.2135L21.3478 15.1724C20.8473 18.4289 20.5971 20.0572 19.4292 21.0286C18.2613 22 16.5538 22 13.139 22H10.8614C7.44652 22 5.73909 22 4.57118 21.0286C3.40327 20.0572 3.15305 18.4289 2.65261 15.1724L2.35157 13.2135Z"/></svg>""",
        "executing" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 8V12L14 14"/></svg>""",
        "jobs" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M7.99805 16H11.998M7.99805 11H15.998"/><path d="M7.5 3.5C5.9442 3.54667 5.01661 3.71984 4.37477 4.36227C3.49609 5.24177 3.49609 6.6573 3.49609 9.48836L3.49609 15.9944C3.49609 18.8255 3.49609 20.241 4.37477 21.1205C5.25345 22 6.66767 22 9.49609 22L14.4961 22C17.3245 22 18.7387 22 19.6174 21.1205C20.4961 20.241 20.4961 18.8255 20.4961 15.9944V9.48836C20.4961 6.6573 20.4961 5.24177 19.6174 4.36228C18.9756 3.71984 18.048 3.54667 16.4922 3.5"/><path d="M7.49609 3.75C7.49609 2.7835 8.2796 2 9.24609 2H14.7461C15.7126 2 16.4961 2.7835 16.4961 3.75C16.4961 4.7165 15.7126 5.5 14.7461 5.5H9.24609C8.2796 5.5 7.49609 4.7165 7.49609 3.75Z"/></svg>""",
        "queues" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 5L20 5"/><path d="M8 12L20 12"/><path d="M8 19L20 19"/><path d="M4 5H4.00898" stroke-width="2"/><path d="M4 12H4.00898" stroke-width="2"/><path d="M4 19H4.00898" stroke-width="2"/></svg>""",
        "failures" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M13.9248 21H10.0752C5.44476 21 3.12955 21 2.27636 19.4939C1.42317 17.9879 2.60736 15.9914 4.97574 11.9985L6.90057 8.75333C9.17559 4.91778 10.3131 3 12 3C13.6869 3 14.8244 4.91777 17.0994 8.75332L19.0243 11.9985C21.3926 15.9914 22.5768 17.9879 21.7236 19.4939C20.8704 21 18.5552 21 13.9248 21Z"/><path d="M12 9V13"/><path d="M12.125 16.75H12M12.25 16.75C12.25 16.8881 12.1381 17 12 17C11.8619 17 11.75 16.8881 11.75 16.75C11.75 16.6119 11.8619 16.5 12 16.5C12.1381 16.5 12.25 16.6119 12.25 16.75Z"/></svg>""",
        "schedules" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M16 2V6M8 2V6"/><path d="M13 4H11C7.22876 4 5.34315 4 4.17157 5.17157C3 6.34315 3 8.22876 3 12V14C3 17.7712 3 19.6569 4.17157 20.8284C5.34315 22 7.22876 22 11 22H13C16.7712 22 18.6569 22 19.8284 20.8284C21 19.6569 21 17.7712 21 14V12C21 8.22876 21 6.34315 19.8284 5.17157C18.6569 4 16.7712 4 13 4Z"/><path d="M3 10H21"/><path d="M11.9955 14H12.0045M11.9955 18H12.0045M15.991 14H16M8 14H8.00897M8 18H8.00897" stroke-width="2"/></svg>""",
        "observers" => """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21.544 11.045C21.848 11.4713 22 11.6845 22 12C22 12.3155 21.848 12.5287 21.544 12.955C20.1779 14.8706 16.6892 19 12 19C7.31078 19 3.8221 14.8706 2.45604 12.955C2.15201 12.5287 2 12.3155 2 12C2 11.6845 2.15201 11.4713 2.45604 11.045C3.8221 9.12944 7.31078 5 12 5C16.6892 5 20.1779 9.12944 21.544 11.045Z"/><path d="M15 12C15 13.6569 13.6569 15 12 15C10.3431 15 9 13.6569 9 12C9 10.3431 10.3431 9 12 9C13.6569 9 15 10.3431 15 12Z"/></svg>""",
        _ => "",
    });
}
