using System.Collections.Generic;
using System.Linq;
using VideoStudio.Models;

namespace VideoStudio.Services;

/// <summary>
/// Phase 8.8.2 — pure-data layout for the top-down DAG canvas.
///
/// Inputs: source <see cref="MediaFile"/>s (layer 0) + <see cref="PipelineStep"/>s wired via
/// <see cref="PipelineStep.Inputs"/> (8.8.1 backfill). Output: a flat list of <see cref="GraphNode"/>
/// with assigned <c>(layer, column, x, y)</c> in canvas coordinates so the renderer (8.8.3)
/// can drop each card at <c>Canvas.SetLeft(x); Canvas.SetTop(y)</c>.
///
/// Algorithm:
/// 1. Source nodes get layer 0, columns assigned by MediaFiles insertion order.
/// 2. Each step's layer = 1 + max(layer of upstream refs found in <see cref="PipelineStep.Inputs"/>).
///    Steps with no resolvable upstream sit at layer 1.
/// 3. Within each layer, columns assigned in <see cref="PipelineStep"/> insertion order
///    (stable tie-breaker). Layout is single-pass and idempotent.
///
/// Heights default to <see cref="DefaultRowHeight"/>; <see cref="GraphLayout.Build"/> accepts an
/// optional <c>nodeHeight</c> callback so the renderer can feed measured ActualHeights for
/// expanded cards (8.8.3 measure-aware packing).
/// </summary>
public static class GraphLayout
{
    // Phase 8.9 — left-to-right DAG. Layer = horizontal column (X), within-layer index = vertical row (Y).
    public const double ColumnWidth = 320;       // card width (wider for better readability)
    public const double ColumnGap = 80;          // horizontal gap between layers (room for wires)
    public const double DefaultRowHeight = 220;
    public const double RowGap = 24;             // vertical gap between sibling cards in a layer
    public const double SourceWidth = 200;       // compact source nodes
    public const double SourceHeight = 110;
    public const double TopPadding = 24;
    public const double LeftPadding = 24;

    /// <summary>Phase 8.10 — bucket node sizing. Header + per-file row.</summary>
    public const double BucketWidth = 280;
    public const double BucketBaseHeight = 96;     // header + footer (Add buttons)
    public const double BucketRowHeight = 56;      // per-file row (thumbnail + metadata)
    public const double BucketMaxHeight = 360;     // beyond this, the inner list scrolls

    public sealed class GraphNode
    {
        /// <summary>For step nodes — the underlying pipeline step.</summary>
        public PipelineStep? Step { get; init; }
        /// <summary>Phase 8.10 — true for the single input bucket node.</summary>
        public bool IsBucket { get; init; }
        /// <summary>Stable id used in ArtifactRefs ("bucket:input" or step.StepId).</summary>
        public string NodeId { get; init; } = string.Empty;
        /// <summary>LTR horizontal layer. 0 = bucket, 1+ = effects.</summary>
        public int Layer { get; set; }
        public int Column { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = ColumnWidth;
        public double Height { get; set; } = DefaultRowHeight;
    }

    public sealed class GraphLayoutResult
    {
        public IReadOnlyList<GraphNode> Nodes { get; init; } = System.Array.Empty<GraphNode>();
        public double TotalWidth { get; init; }
        public double TotalHeight { get; init; }

        public GraphNode? FindByNodeId(string nodeId) =>
            Nodes.FirstOrDefault(n => n.NodeId == nodeId);
    }

    /// <summary>
    /// Compute the layout. <paramref name="nodeHeight"/> is optional: return measured ActualHeight
    /// for each step card, or null/0 to use <see cref="DefaultRowHeight"/>.
    /// </summary>
    public static GraphLayoutResult Build(
        IReadOnlyList<MediaFile> mediaFiles,
        IReadOnlyList<PipelineStep> steps,
        System.Func<PipelineStep, double>? nodeHeight = null)
    {
        var nodes = new List<GraphNode>();

        // Layer 0: ONE input bucket node (always shown — even when empty so the user has a drop target).
        double bucketHeight = System.Math.Min(BucketMaxHeight, BucketBaseHeight + mediaFiles.Count * BucketRowHeight);
        nodes.Add(new GraphNode
        {
            IsBucket = true,
            NodeId = ViewModels.PipelineViewModel.BucketStepId,
            Layer = 0,
            Width = BucketWidth,
            Height = bucketHeight,
        });

        var nodeIdToLayer = new Dictionary<string, int>();
        foreach (var n in nodes) nodeIdToLayer[n.NodeId] = 0;

        // Layers 1+: assign in step insertion order. Layer = 1 + max(upstream layers from step.Inputs).
        // Effects with no upstream-step refs (only source refs) all sit in layer 1 → parallel by default.
        foreach (var step in steps)
        {
            int layer = 1;
            foreach (var kv in step.Inputs)
            {
                if (nodeIdToLayer.TryGetValue(kv.Value.StepId, out var upLayer))
                {
                    layer = System.Math.Max(layer, upLayer + 1);
                }
            }
            double h = nodeHeight?.Invoke(step) ?? 0;
            if (h <= 0) h = DefaultRowHeight;

            var node = new GraphNode
            {
                Step = step,
                NodeId = step.StepId,
                Layer = layer,
                Width = ColumnWidth,
                Height = h,
            };
            nodes.Add(node);
            nodeIdToLayer[node.NodeId] = layer;
        }

        // Within each layer assign Column = insertion order (top-to-bottom).
        var perLayerCount = new Dictionary<int, int>();
        foreach (var n in nodes)
        {
            perLayerCount.TryGetValue(n.Layer, out var c);
            n.Column = c;
            perLayerCount[n.Layer] = c + 1;
        }

        // X per layer = LeftPadding + layer * (max-width-in-layer + ColumnGap).
        // Sources are narrower than effects, so compute per-layer width from actual nodes.
        var layerWidths = new Dictionary<int, double>();
        foreach (var n in nodes)
        {
            layerWidths.TryGetValue(n.Layer, out var w);
            if (n.Width > w) layerWidths[n.Layer] = n.Width;
        }
        var layerXs = new Dictionary<int, double>();
        double x = LeftPadding;
        var sortedLayers = layerWidths.Keys.OrderBy(k => k).ToList();
        foreach (var l in sortedLayers)
        {
            layerXs[l] = x;
            x += layerWidths[l] + ColumnGap;
        }
        double totalWidth = x;

        // Y within layer = TopPadding + cumulative (height + RowGap) of prior siblings.
        var layerCursorY = new Dictionary<int, double>();
        double maxY = TopPadding;
        foreach (var n in nodes)
        {
            if (!layerCursorY.TryGetValue(n.Layer, out var cy)) cy = TopPadding;
            n.X = layerXs[n.Layer];
            n.Y = cy;
            cy += n.Height + RowGap;
            layerCursorY[n.Layer] = cy;
            if (cy > maxY) maxY = cy;
        }
        double totalHeight = maxY;

        return new GraphLayoutResult
        {
            Nodes = nodes,
            TotalWidth = totalWidth,
            TotalHeight = totalHeight,
        };
    }
}
