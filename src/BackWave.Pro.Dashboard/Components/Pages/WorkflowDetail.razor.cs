using System.Globalization;
using BackWave.Dashboard;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.AspNetCore.Components;

namespace BackWave.Pro.Dashboard;

// Code-behind for WorkflowDetail. The status glyphs and the graph client script are C# raw string
// literals; they live here rather than in the .razor @code block so the C# compiler (not the
// per-target-framework Razor parser) reads them, which keeps the component compiling across every
// shipped TFM. The Razor markup partial sees these members by name.

/// <summary>The BackWave Pro Workflow graph page: the member DAG laid out by dependency depth with
/// each member's live state, the immutable Dependency edges, and an inline Job detail panel for the
/// selected member. Not consumer API — the Pro dashboard surface renders it.</summary>
public partial class WorkflowDetail
{
    /// <summary>Path the dashboard is mounted at (the request PathBase), e.g. <c>/backwave</c>.</summary>
    [Parameter, EditorRequired] public string BasePath { get; set; } = "";

    /// <summary>The Workflow being viewed: its members, structural edges, and derived status.</summary>
    [Parameter, EditorRequired] public WorkflowView Workflow { get; set; } = null!;

    /// <summary>The actions the viewer is permitted to take (gates the Cancel Workflow control).</summary>
    [Parameter, EditorRequired] public DashboardActions Actions { get; set; } = DashboardActions.None;

    /// <summary>The member selected via <c>?member=</c>, whose Job detail renders below the graph; null when none.</summary>
    [Parameter] public JobSnapshot? Selected { get; set; }

    /// <summary>The selected member's remaining gating parents; empty unless it is Awaiting Parent.</summary>
    [Parameter] public IReadOnlyList<Guid> SelectedGatingParents { get; set; } = [];

    /// <summary>The selected member's Transition Log, oldest first.</summary>
    [Parameter] public IReadOnlyList<JobTransition> SelectedHistory { get; set; } = [];

    /// <summary>Whether the global Job History Policy is Off — applies to every job.</summary>
    [Parameter] public bool HistoryDisabled { get; set; }

    /// <summary>Whether the viewer may see the selected member's sensitive content.</summary>
    [Parameter] public bool SelectedCanViewSensitiveData { get; set; }

    /// <summary>The selected member's payload, or null when withheld/absent.</summary>
    [Parameter] public JobPayloadView? SelectedPayload { get; set; }

    /// <summary>The selected member's Job Output, or null when withheld/absent.</summary>
    [Parameter] public JobPayloadView? SelectedOutput { get; set; }

    /// <summary>Pixels, invariant-formatted — never the ambient culture's decimal comma in a style or SVG path.</summary>
    private static string Px(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One structural edge as a horizontal cubic bezier from the parent's right anchor to the
    /// child's left anchor (the React-Flow look), with a minimum control reach so short hops still curve.</summary>
    private static string EdgePath(WorkflowGraphLayout.Edge e)
    {
        var dx = Math.Max(28, (e.X2 - e.X1) / 2);
        return string.Create(CultureInfo.InvariantCulture,
            $"M {e.X1} {e.Y1} C {e.X1 + dx} {e.Y1} {e.X2 - dx} {e.Y2} {e.X2} {e.Y2}");
    }

    /// <summary>The small status glyph drawn on a node, by job state.</summary>
    private static MarkupString StateGlyph(JobState state) => (MarkupString)(state switch
    {
        JobState.Succeeded =>
            """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M8.5 12.5 11 15l4.5-5"/></svg>""",
        JobState.Leased =>
            """<svg class="bw-gnode__spin" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M12 3a9 9 0 1 0 9 9" opacity="0.9"/></svg>""",
        JobState.DeadLettered or JobState.Quarantined =>
            """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7.5v5"/><path d="M12 16h.01" stroke-width="2.2"/></svg>""",
        JobState.Cancelled =>
            """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M9 9l6 6M15 9l-6 6"/></svg>""",
        JobState.AwaitingParent =>
            """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>""",
        _ => // Scheduled and any future non-terminal state: a clock
            """<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7.5V12l3 2"/></svg>""",
    });

    // The dashboard's one graph script: pan (drag), zoom (wheel + buttons), fit-to-view, and a live
    // minimap viewport rectangle. Progressive enhancement — without JS the .bw-graph__pane scrolls
    // natively and the controls/minimap stay hidden (they only show under .is-interactive). The whole
    // canvas (edges + nodes) is one transformed element, so a node link still navigates on a plain
    // click; a real drag suppresses the trailing click so panning never selects.
    private const string GraphScript =
        """
        <script>
        (function () {
            var root = document.querySelector('[data-graph]');
            if (!root) return;
            var pane = root.querySelector('[data-graph-pane]');
            var canvas = root.querySelector('[data-graph-canvas]');
            if (!pane || !canvas) return;
            var mini = root.querySelector('[data-graph-minimap]');
            var miniView = root.querySelector('[data-graph-view]');
            var cw = parseFloat(canvas.getAttribute('data-w')) || canvas.offsetWidth;
            var ch = parseFloat(canvas.getAttribute('data-h')) || canvas.offsetHeight;
            var scale = 1, tx = 0, ty = 0, minS = 0.2, maxS = 2.5;

            function clamp(s) { return Math.min(maxS, Math.max(minS, s)); }
            function size() { return { w: pane.clientWidth, h: pane.clientHeight }; }
            function apply() {
                canvas.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + scale + ')';
                if (!miniView) return;
                // The visible region in canvas coords, clamped to the canvas so the indicator reads
                // cleanly: it fills the whole minimap at fit-zoom and shrinks to the sub-region in.
                var vx = Math.max(0, -tx / scale), vy = Math.max(0, -ty / scale);
                miniView.setAttribute('x', vx);
                miniView.setAttribute('y', vy);
                miniView.setAttribute('width', Math.min(cw - vx, pane.clientWidth / scale));
                miniView.setAttribute('height', Math.min(ch - vy, pane.clientHeight / scale));
            }
            // The initial framing. We never zoom out below a readable floor: a graph wider than the
            // pane is pinned to the top-left and panned, rather than shrunk until the node text is
            // illegible. (River does the same — fit frames, it doesn't cram.)
            var fitFloor = 0.85;
            function fit() {
                var v = size();
                scale = Math.max(fitFloor, clamp(Math.min(v.w / cw, v.h / ch) * 0.92));
                tx = cw * scale <= v.w ? (v.w - cw * scale) / 2 : 16;
                ty = ch * scale <= v.h ? (v.h - ch * scale) / 2 : 16;
                apply();
            }
            function zoomAt(factor, cx, cy) {
                var ns = clamp(scale * factor);
                if (ns === scale) return;
                tx = cx - (cx - tx) * (ns / scale);
                ty = cy - (cy - ty) * (ns / scale);
                scale = ns;
                apply();
            }

            root.classList.add('is-interactive');

            pane.addEventListener('wheel', function (e) {
                e.preventDefault();
                var r = pane.getBoundingClientRect();
                zoomAt(e.deltaY < 0 ? 1.1 : 0.9, e.clientX - r.left, e.clientY - r.top);
            }, { passive: false });

            var dragging = false, moved = false, sx = 0, sy = 0, otx = 0, oty = 0;
            pane.addEventListener('pointerdown', function (e) {
                if (e.button !== 0) return;
                dragging = true; moved = false;
                sx = e.clientX; sy = e.clientY; otx = tx; oty = ty;
            });
            pane.addEventListener('pointermove', function (e) {
                if (!dragging) return;
                var dx = e.clientX - sx, dy = e.clientY - sy;
                // Only treat this as a pan once it crosses the slop threshold — and only THEN
                // capture the pointer. Capturing on pointerdown would retarget the trailing click
                // to the pane, so a plain click on a node never reached its link (it just panned 0px).
                if (!moved && Math.abs(dx) + Math.abs(dy) > 4) {
                    moved = true;
                    try { pane.setPointerCapture(e.pointerId); } catch (_) {}
                }
                if (!moved) return;
                tx = otx + dx; ty = oty + dy;
                apply();
            });
            function end(e) {
                if (!dragging) return;
                dragging = false;
                try { pane.releasePointerCapture(e.pointerId); } catch (_) {}
            }
            pane.addEventListener('pointerup', end);
            pane.addEventListener('pointercancel', end);
            pane.addEventListener('click', function (e) {
                if (moved) { e.preventDefault(); e.stopPropagation(); moved = false; }
            }, true);

            root.querySelectorAll('[data-graph-zoom]').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var v = size(), kind = btn.getAttribute('data-graph-zoom');
                    if (kind === 'fit') fit();
                    else zoomAt(kind === 'in' ? 1.2 : 0.8, v.w / 2, v.h / 2);
                });
            });

            if (mini) {
                mini.addEventListener('click', function (e) {
                    var svg = mini.querySelector('svg');
                    if (!svg) return;
                    var r = svg.getBoundingClientRect();
                    var s = Math.min(r.width / cw, r.height / ch);
                    var ox = (r.width - cw * s) / 2, oy = (r.height - ch * s) / 2;
                    var canX = (e.clientX - r.left - ox) / s, canY = (e.clientY - r.top - oy) / s;
                    var v = size();
                    tx = v.w / 2 - canX * scale;
                    ty = v.h / 2 - canY * scale;
                    apply();
                });
            }

            // Selecting a member is a full navigation (?member=) — the page reloads, so without help the
            // viewer would snap back to the top at fit-zoom every time they inspect a different node.
            // Persist the scroll + the graph transform per Workflow (keyed by pathname, which is stable
            // across ?member= changes) and restore them on the next load, so picking another node feels
            // in place. sessionStorage = this tab only; cleared when the tab closes.
            var stateKey = 'bw-graph:' + location.pathname;
            function saveState() {
                try { sessionStorage.setItem(stateKey, JSON.stringify({ s: scale, x: tx, y: ty, sc: window.scrollY })); } catch (_) {}
            }
            function restoreState() {
                try {
                    var st = JSON.parse(sessionStorage.getItem(stateKey) || 'null');
                    if (!st || typeof st.s !== 'number') return false;
                    scale = clamp(st.s); tx = st.x; ty = st.y;
                    apply();
                    if (typeof st.sc === 'number') {
                        window.scrollTo(0, st.sc);
                        // Re-assert after load in case late layout (web fonts, the inline panel) shifted things.
                        window.addEventListener('load', function () { window.scrollTo(0, st.sc); });
                    }
                    return true;
                } catch (_) { return false; }
            }

            if (!restoreState()) fit();
            // Capture the latest view right before we leave (a node click, the back/forward button, …).
            window.addEventListener('pagehide', saveState);
            // On resize (window reflow, the inspector docking open, etc.) keep the current zoom — only
            // re-sync the minimap viewport. Re-fitting here was what shrank the node text to unreadable
            // whenever the pane got narrower; the zoom the viewer chose (or the initial fit) is preserved.
            window.addEventListener('resize', apply);
        })();
        </script>
        """;
}
