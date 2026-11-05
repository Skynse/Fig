using System.Text.Json;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class EffectGraphTests
{
    // ---- builders ---------------------------------------------------------------------------

    private sealed class Builder
    {
        public EffectGraph Graph { get; } = new() { Name = "Test" };

        public string Node(string primitive, string id, Dictionary<string, ParamValue>? p = null, string? effect = null, bool bypass = false)
        {
            Graph.Nodes.Add(new GraphNode { Id = id, PrimitiveId = primitive, Params = p ?? new(), EffectTypeId = effect, Bypass = bypass });
            return id;
        }

        public Builder Wire(string from, string fromPort, string to, string toPort)
        {
            Graph.Edges.Add(new GraphEdge { FromNodeId = from, FromPort = fromPort, ToNodeId = to, ToPort = toPort });
            return this;
        }

        public Builder Promote(string key, string node, string param, double scale = 1, double? def = null)
        {
            Graph.Promoted.Add(new PromotedParam
            {
                Key = key, Label = key,
                Targets = { new PromotedTarget { NodeId = node, Param = param, Scale = scale } },
                Default = def,
            });
            return this;
        }
    }

    private static Dictionary<string, ParamValue> P(string key, double value) => new() { [key] = ParamValue.OfDouble(value) };

    private static DecodedFrame Picture(int seed = 1, int width = 32, int height = 20)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
    }

    private static byte[] Bytes(DecodedFrame f) => f.Pixels.Take(f.Width * f.Height * 4).ToArray();

    private static IReadOnlyDictionary<string, ParamValue> None => new Dictionary<string, ParamValue>();

    private static Builder InputToOutput(string middle, Dictionary<string, ParamValue>? p = null)
    {
        var b = new Builder();
        b.Node("input", "in");
        b.Node(middle, "mid", p);
        b.Node("output", "out");
        b.Wire("in", "image", "mid", "image").Wire("mid", "image", "out", "image");
        return b;
    }

    // ---- compile ----------------------------------------------------------------------------

    [Fact]
    public void Compile_ReportsEachKindOfProblemOnTheRightNode()
    {
        // no output
        var none = new Builder();
        none.Node("input", "in");
        Assert.Contains(GraphCompiler.Compile(none.Graph).Errors, e => e.Code == GraphErrorCode.NoOutput);

        // two outputs
        var two = InputToOutput("blur");
        two.Node("output", "out2");
        two.Wire("mid", "image", "out2", "image");
        Assert.Contains(GraphCompiler.Compile(two.Graph).Errors, e => e.Code == GraphErrorCode.MultipleOutputs && e.NodeId == "out2");

        // missing input on a node that matters
        var missing = new Builder();
        missing.Node("blur", "mid");
        missing.Node("output", "out");
        missing.Wire("mid", "image", "out", "image");
        Assert.Contains(GraphCompiler.Compile(missing.Graph).Errors, e => e.Code == GraphErrorCode.MissingInput && e.NodeId == "mid");

        // mask into an image input
        var mismatch = new Builder();
        mismatch.Node("noise", "noise");
        mismatch.Node("output", "out");
        mismatch.Wire("noise", "mask", "out", "image");
        Assert.Contains(GraphCompiler.Compile(mismatch.Graph).Errors, e => e.Code == GraphErrorCode.TypeMismatch && e.NodeId == "out");

        // two wires into one input
        var twice = InputToOutput("blur");
        twice.Node("solid", "solid");
        twice.Wire("solid", "image", "mid", "image");
        Assert.Contains(GraphCompiler.Compile(twice.Graph).Errors, e => e.Code == GraphErrorCode.InputAlreadyConnected && e.NodeId == "mid");

        // unknown primitive
        var unknown = InputToOutput("not-a-node");
        Assert.Contains(GraphCompiler.Compile(unknown.Graph).Errors, e => e.Code == GraphErrorCode.UnknownPrimitive && e.NodeId == "mid");

        // a wire to a node that is gone
        var dangling = InputToOutput("blur");
        dangling.Wire("ghost", "image", "mid", "image");
        Assert.Contains(GraphCompiler.Compile(dangling.Graph).Errors, e => e.Code == GraphErrorCode.BadEdge);
    }

    [Fact]
    public void Compile_RejectsLoops()
    {
        var b = new Builder();
        b.Node("blur", "a");
        b.Node("blur", "b");
        b.Node("output", "out");
        b.Wire("a", "image", "b", "image").Wire("b", "image", "a", "image").Wire("b", "image", "out", "image");
        Assert.Contains(GraphCompiler.Compile(b.Graph).Errors, e => e.Code == GraphErrorCode.Cycle);
    }

    // ---- rendering --------------------------------------------------------------------------

    [Fact]
    public void Apply_StockEffectNode_MatchesRunningTheEffectDirectly()
    {
        var b = InputToOutput("effect");
        b.Graph.Nodes[1].EffectTypeId = EffectCatalog.Sepia;
        var source = Picture(4);
        var original = Bytes(source);

        var viaGraph = new GraphEffectProcessor(b.Graph).Apply(source, None, 0);
        var direct = EffectCatalog.Resolve(EffectCatalog.Sepia)!.Apply(source, EffectCatalog.Find(EffectCatalog.Sepia)!.DefaultParams(), 0);
        try
        {
            Assert.Equal(Bytes(direct), Bytes(viaGraph));
            Assert.Equal(original, Bytes(source));
        }
        finally { FramePool.Return(viaGraph.Pixels); FramePool.Return(direct.Pixels); }
    }

    [Fact]
    public void Apply_InvalidGraph_FailsSafeAndReturnsTheInput()
    {
        var b = new Builder();
        b.Node("blur", "a"); // no output
        var processor = new GraphEffectProcessor(b.Graph);
        var source = Picture();
        Assert.False(processor.Compile.IsValid);
        Assert.Same(source, processor.Apply(source, None, 0));
    }

    [Fact]
    public void Apply_IsRepeatable_AndNeverMutatesTheInput()
    {
        var b = new Builder();
        b.Node("input", "in");
        b.Node("blur", "blur", P("radius", 0.05));
        b.Node("duotone", "duo");
        b.Node("noise", "noise");
        b.Node("mix", "mix", P("amount", 0.6));
        b.Node("output", "out");
        b.Wire("in", "image", "blur", "image").Wire("blur", "image", "duo", "image")
            .Wire("in", "image", "mix", "a").Wire("duo", "image", "mix", "b").Wire("noise", "mask", "mix", "mask")
            .Wire("mix", "image", "out", "image");
        var processor = new GraphEffectProcessor(b.Graph);
        var source = Picture(11, 64, 36);
        var original = Bytes(source);

        var first = Bytes(processor.Apply(source, None, 0.5));
        for (int i = 0; i < 4; i++)
        {
            var again = processor.Apply(source, None, 0.5);
            Assert.Equal(first, Bytes(again));
            FramePool.Return(again.Pixels);
        }
        Assert.Equal(original, Bytes(source));
    }

    // ---- library & catalog ------------------------------------------------------------------

    private static EffectGraph Soft(string name = "Soft glow")
    {
        var b = InputToOutput("blur", P("radius", 0.03));
        b.Promote("softness", "mid", "radius");
        b.Graph.Name = name;
        return b.Graph;
    }

    [Fact]
    public void Library_ExportAndImport_RoundTripTheGraph_AndAvoidIdClashes()
    {
        var library = new GraphEffectLibrary();
        library.Attach(new List<EffectGraph>());
        var path = Path.Combine(Path.GetTempPath(), $"fig-graph-{Guid.NewGuid():N}.figgraph.json");
        try
        {
            var graph = Soft();
            library.Save(graph);
            library.Export(graph.Id, path);

            var imported = library.Import(path);
            Assert.NotEqual(graph.Id, imported.Id);       // the id was taken, so it became a new effect
            Assert.Equal(graph.Name, imported.Name);
            Assert.Equal(2, library.Graphs.Count);
            Assert.Equal(graph.Promoted[0].Key, imported.Promoted[0].Key);
            Assert.Equal(0.03, imported.Nodes[1].Params["radius"].AsDouble, 6);
        }
        finally { library.Detach(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Graph_SerializesWithTheProject()
    {
        var project = Fig.Core.Project.Project.Create("p");
        project.EffectGraphs.Add(Soft());
        var json = JsonSerializer.Serialize(project);
        var back = JsonSerializer.Deserialize<Fig.Core.Project.Project>(json)!;
        var graph = Assert.Single(back.EffectGraphs);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal("softness", graph.Promoted[0].Key);
    }
}
