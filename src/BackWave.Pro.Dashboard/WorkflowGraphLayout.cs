using BackWave.Monitor;
using BackWave.Pro;
using BackWave.Storage;

namespace BackWave.Pro.Dashboard;

/// <summary>
/// A server-computed layered layout for a Workflow's member DAG. The members are
/// laid out left-to-right in columns by their longest-path depth (a parent always sits in an
/// earlier column than its children), the row order within a column following the stable
/// topological order so the picture is deterministic. The dashboard renders the result as
/// absolutely-positioned nodes over an SVG edge overlay, then a small client script pans/zooms
/// the whole canvas. Nodes are returned in topological order, so the DOM order is parents-before-
/// children regardless of pixel position.
/// </summary>
internal static class WorkflowGraphLayout
{
    // The node box and the gaps between boxes, in CSS pixels at scale 1. The client script scales
    // the whole canvas, so these are just the intrinsic geometry.
    public const double NodeWidth = 224;
    public const double NodeHeight = 68;
    public const double ColumnGap = 72;
    public const double RowGap = 28;
    public const double Padding = 28;

    /// <summary>One laid-out member: its snapshot, its column/row in the grid, and its top-left pixel origin.</summary>
    public sealed record Node(JobSnapshot Member, int Column, int Row, double X, double Y);

    /// <summary>One structural edge, with the parent's right-anchor and the child's left-anchor in pixels.
    /// <paramref name="Pending"/> is true while the child is still Awaiting Parent — the gate has not yet
    /// released — which the view draws as a dashed line (a satisfied/flowing edge is solid).</summary>
    public sealed record Edge(Guid Parent, Guid Child, double X1, double Y1, double X2, double Y2, bool Pending);

    /// <summary>The full layout: nodes in topological order, edges, and the canvas extents.</summary>
    public sealed record Result(IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges, double Width, double Height);

    public static Result Compute(WorkflowView workflow)
    {
        var ordered = TopologicalOrder(workflow);
        if (ordered.Count == 0)
        {
            return new Result([], [], Padding * 2, Padding * 2);
        }

        var byId = ordered.ToDictionary(m => m.JobId);
        var parents = ParentsByChild(workflow.Edges, byId.Keys);

        // Longest-path layering: a node's column is one past its deepest parent. `ordered` is
        // topological, so every parent's depth is already settled when we reach the child.
        var depth = new Dictionary<Guid, int>(ordered.Count);
        foreach (var member in ordered)
        {
            var parentDepth = -1;
            if (parents.TryGetValue(member.JobId, out var ps))
            {
                foreach (var p in ps)
                {
                    if (depth.TryGetValue(p, out var d) && d > parentDepth) parentDepth = d;
                }
            }
            depth[member.JobId] = parentDepth + 1;
        }

        // Row index within each column, in topological order, so siblings stack stably.
        var rowInColumn = new Dictionary<Guid, int>(ordered.Count);
        var columnCount = new Dictionary<int, int>();
        var nodes = new List<Node>(ordered.Count);
        foreach (var member in ordered)
        {
            var col = depth[member.JobId];
            var row = columnCount.TryGetValue(col, out var c) ? c : 0;
            columnCount[col] = row + 1;
            rowInColumn[member.JobId] = row;
            nodes.Add(new Node(member, col, row, 0, 0)); // positions filled below once heights are known
        }

        // Each column is centred vertically against the tallest column, so the graph reads as a
        // balanced tree rather than top-stacked.
        var tallestRows = columnCount.Values.Max();
        var canvasHeight = Padding * 2 + tallestRows * NodeHeight + (tallestRows - 1) * RowGap;
        var maxColumn = depth.Values.Max();
        var canvasWidth = Padding * 2 + (maxColumn + 1) * NodeWidth + maxColumn * ColumnGap;

        double ColumnTop(int col)
        {
            var rows = columnCount[col];
            var columnHeight = rows * NodeHeight + (rows - 1) * RowGap;
            return Padding + (canvasHeight - Padding * 2 - columnHeight) / 2;
        }

        var positioned = new List<Node>(nodes.Count);
        var originById = new Dictionary<Guid, (double X, double Y)>(nodes.Count);
        foreach (var n in nodes)
        {
            var x = Padding + n.Column * (NodeWidth + ColumnGap);
            var y = ColumnTop(n.Column) + n.Row * (NodeHeight + RowGap);
            positioned.Add(n with { X = x, Y = y });
            originById[n.Member.JobId] = (x, y);
        }

        // Edges anchor parent-right-centre to child-left-centre. Edges touching a non-member (which
        // store-side containment rejects on a normal enqueue) are skipped — nothing to anchor to.
        var edges = new List<Edge>(workflow.Edges.Count);
        foreach (var e in workflow.Edges)
        {
            if (!originById.TryGetValue(e.Parent, out var p) || !originById.TryGetValue(e.Child, out var c))
            {
                continue;
            }
            var pending = byId[e.Child].State == JobState.AwaitingParent;
            edges.Add(new Edge(
                e.Parent, e.Child,
                p.X + NodeWidth, p.Y + NodeHeight / 2,
                c.X, c.Y + NodeHeight / 2,
                pending));
        }

        return new Result(positioned, edges, canvasWidth, canvasHeight);
    }

    /// <summary>The structural parents (the edge Parent ends) keyed by child — the "depends on" set.</summary>
    private static Dictionary<Guid, List<Guid>> ParentsByChild(IReadOnlyList<WorkflowEdge> edges, IEnumerable<Guid> members)
    {
        var known = members.ToHashSet();
        var map = new Dictionary<Guid, List<Guid>>();
        foreach (var e in edges)
        {
            if (!known.Contains(e.Child) || !known.Contains(e.Parent)) continue;
            (map.TryGetValue(e.Child, out var list) ? list : map[e.Child] = []).Add(e.Parent);
        }
        return map;
    }

    // Acyclicity is validated at enqueue time (ADR 0023), so the workflow is a DAG by construction.
    /// <summary>Members in dependency order (parents before children) via a stable topological sort over the
    /// structural edges; ties keep the Monitor's member order. A DAG by construction, so the sort always
    /// terminates; any member not reached still appears, never dropped.</summary>
    private static IReadOnlyList<JobSnapshot> TopologicalOrder(WorkflowView workflow)
    {
        var byId = workflow.Members.ToDictionary(m => m.JobId);
        var remainingParents = workflow.Members.ToDictionary(
            m => m.JobId, m => workflow.Edges.Count(e => e.Child == m.JobId && byId.ContainsKey(e.Parent)));
        var childrenOf = workflow.Edges
            .Where(e => byId.ContainsKey(e.Parent) && byId.ContainsKey(e.Child))
            .GroupBy(e => e.Parent)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Child).ToList());

        var ready = new Queue<JobSnapshot>(workflow.Members.Where(m => remainingParents[m.JobId] == 0));
        var ordered = new List<JobSnapshot>(workflow.Members.Count);
        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            ordered.Add(node);
            if (!childrenOf.TryGetValue(node.JobId, out var children)) continue;
            foreach (var child in children)
            {
                if (--remainingParents[child] == 0 && byId.TryGetValue(child, out var snapshot))
                {
                    ready.Enqueue(snapshot);
                }
            }
        }
        foreach (var member in workflow.Members)
        {
            if (!ordered.Contains(member)) ordered.Add(member);
        }
        return ordered;
    }
}
