namespace PycRune.CFG;

public sealed class NaturalLoop
{
    public required BasicBlock Header { get; init; }
    public required HashSet<BasicBlock> Body { get; init; }
    public required HashSet<BasicBlock> Exits { get; init; }
    public required HashSet<BasicBlock> Latches { get; init; }
    public required HashSet<BasicBlock> ExitingBlocks { get; init; }

    public bool Contains(BasicBlock block) => Body.Contains(block);

    public override string ToString() =>
        $"Loop (Header: Block #{Header.Id}, Body: {Body.Count} blocks, Latches: {Latches.Count}, Exits: {Exits.Count})";
}

public sealed class LoopAnalysis
{
    public IReadOnlyList<NaturalLoop> Loops { get; }
    public DominatorTree DominatorTree { get; }

    public LoopAnalysis(ControlFlowGraph cfg)
        : this(cfg.Blocks, new DominatorTree(cfg))
    {
    }

    public LoopAnalysis(ControlFlowGraph cfg, DominatorTree domTree)
        : this(cfg.Blocks, domTree)
    {
    }

    public LoopAnalysis(IReadOnlyList<BasicBlock> blocks, DominatorTree domTree)
    {
        DominatorTree = domTree;
        Loops = DetectLoops(blocks, domTree);
    }

    public bool IsLoopHeader(BasicBlock block) =>
        Loops.Any(l => ReferenceEquals(l.Header, block));

    public NaturalLoop? GetLoopByHeader(BasicBlock header) =>
        Loops.FirstOrDefault(l => ReferenceEquals(l.Header, header));

    public IReadOnlyList<NaturalLoop> GetLoopsForBlock(BasicBlock block) =>
        Loops.Where(l => l.Contains(block)).ToList();

    public NaturalLoop? GetInnermostLoop(BasicBlock block) =>
        Loops.Where(l => l.Contains(block)).OrderBy(l => l.Body.Count).FirstOrDefault();

    public bool IsInLoop(BasicBlock block) =>
        Loops.Any(l => l.Contains(block));

    private static IReadOnlyList<NaturalLoop> DetectLoops(IReadOnlyList<BasicBlock> blocks, DominatorTree domTree)
    {
        var backEdges = new Dictionary<BasicBlock, HashSet<BasicBlock>>();

        foreach (var tail in blocks)
        {
            foreach (var head in tail.Successors)
            {
                if (domTree.Dominates(head, tail))
                {
                    if (!backEdges.TryGetValue(head, out var latches))
                    {
                        latches = [];
                        backEdges[head] = latches;
                    }
                    latches.Add(tail);
                }
            }
        }

        var result = new List<NaturalLoop>();

        foreach (var (header, latches) in backEdges)
        {
            var body = new HashSet<BasicBlock> { header };
            var workList = new Stack<BasicBlock>();

            foreach (var latch in latches)
            {
                if (body.Add(latch))
                {
                    workList.Push(latch);
                }
            }

            while (workList.Count > 0)
            {
                var current = workList.Pop();
                foreach (var pred in current.Predecessors)
                {
                    if (body.Add(pred))
                    {
                        workList.Push(pred);
                    }
                }
            }

            var exits = new HashSet<BasicBlock>();
            var exitingBlocks = new HashSet<BasicBlock>();

            foreach (var b in body)
            {
                foreach (var succ in b.Successors)
                {
                    if (!body.Contains(succ))
                    {
                        exits.Add(succ);
                        exitingBlocks.Add(b);
                    }
                }
            }

            result.Add(new NaturalLoop
            {
                Header = header,
                Body = body,
                Exits = exits,
                Latches = latches,
                ExitingBlocks = exitingBlocks
            });
        }

        return result.OrderBy(l => l.Header.StartOffset).ToList();
    }
}
