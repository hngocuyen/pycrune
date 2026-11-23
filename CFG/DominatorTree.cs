namespace PycRune.CFG;

public sealed class DominatorTree
{
    private readonly DominanceEngine _forwardEngine;
    private readonly DominanceEngine _reverseEngine;
    private readonly BasicBlock? _artificialExit;
    private readonly Dictionary<BasicBlock, HashSet<BasicBlock>> _frontiers = [];

    public BasicBlock? Entry { get; }
    public IReadOnlyList<BasicBlock> Blocks { get; }

    public DominatorTree(ControlFlowGraph cfg)
        : this(cfg.Blocks, cfg.Blocks.Count > 0 ? cfg.Blocks[0] : null)
    {
    }

    public DominatorTree(IReadOnlyList<BasicBlock> blocks, BasicBlock? entry = null)
    {
        Blocks = blocks;
        Entry = entry ?? (blocks.Count > 0 ? blocks[0] : null);

        _forwardEngine = new DominanceEngine(
            Entry,
            blocks,
            b => b.Predecessors,
            b => b.Successors);

        var exitBlocks = blocks.Where(b => _forwardEngine.IsReachable(b) && b.Successors.Count == 0).ToList();
        if (exitBlocks.Count == 0 && blocks.Count > 0)
        {
            var fallback = blocks.LastOrDefault(_forwardEngine.IsReachable) ?? blocks[^1];
            exitBlocks.Add(fallback);
        }

        if (exitBlocks.Count > 1)
        {
            _artificialExit = new BasicBlock
            {
                Id = -1,
                StartOffset = -1
            };

            var exitSet = new HashSet<BasicBlock>(exitBlocks);

            IEnumerable<BasicBlock> GetReversePredecessors(BasicBlock b)
            {
                if (ReferenceEquals(b, _artificialExit))
                {
                    return [];
                }
                if (exitSet.Contains(b))
                {
                    return [..b.Successors, _artificialExit];
                }
                return b.Successors;
            }

            IEnumerable<BasicBlock> GetReverseSuccessors(BasicBlock b)
            {
                if (ReferenceEquals(b, _artificialExit))
                {
                    return exitBlocks;
                }
                return b.Predecessors;
            }

            _reverseEngine = new DominanceEngine(
                _artificialExit,
                [..blocks, _artificialExit],
                GetReversePredecessors,
                GetReverseSuccessors);
        }
        else
        {
            _artificialExit = null;
            var exitRoot = exitBlocks.Count == 1 ? exitBlocks[0] : null;

            _reverseEngine = new DominanceEngine(
                exitRoot,
                blocks,
                b => b.Successors,
                b => b.Predecessors);
        }

        foreach (var block in blocks)
        {
            _frontiers[block] = [];
        }

        foreach (var b in blocks)
        {
            if (_forwardEngine.IsReachable(b) && b.Predecessors.Count >= 2)
            {
                var bIdom = _forwardEngine.GetImmediateDominator(b);
                foreach (var p in b.Predecessors)
                {
                    if (!_forwardEngine.IsReachable(p))
                    {
                        continue;
                    }
                    var runner = p;
                    while (runner != null && !ReferenceEquals(runner, bIdom))
                    {
                        if (_frontiers.TryGetValue(runner, out var frontier))
                        {
                            frontier.Add(b);
                        }
                        runner = _forwardEngine.GetImmediateDominator(runner);
                    }
                }
            }
        }
    }

    public bool Dominates(BasicBlock a, BasicBlock b) =>
        _forwardEngine.Dominates(a, b);

    public bool PostDominates(BasicBlock a, BasicBlock b)
    {
        if (ReferenceEquals(a, b))
        {
            return _reverseEngine.IsReachable(a);
        }
        return _reverseEngine.Dominates(a, b);
    }

    public BasicBlock? GetImmediateDominator(BasicBlock b) =>
        _forwardEngine.GetImmediateDominator(b);

    public BasicBlock? GetImmediatePostDominator(BasicBlock b)
    {
        var ipdom = _reverseEngine.GetImmediateDominator(b);
        return (_artificialExit != null && ReferenceEquals(ipdom, _artificialExit)) ? null : ipdom;
    }

    public HashSet<BasicBlock> GetDominanceFrontier(BasicBlock b) =>
        _frontiers.TryGetValue(b, out var set) ? [..set] : [];

    private sealed class DominanceEngine
    {
        private readonly Dictionary<BasicBlock, BasicBlock?> _idom = [];
        private readonly Dictionary<BasicBlock, int> _tin = [];
        private readonly Dictionary<BasicBlock, int> _tout = [];
        private readonly HashSet<BasicBlock> _reachable = [];

        public DominanceEngine(
            BasicBlock? root,
            IEnumerable<BasicBlock> allNodes,
            Func<BasicBlock, IEnumerable<BasicBlock>> getPredecessors,
            Func<BasicBlock, IEnumerable<BasicBlock>> getSuccessors)
        {
            if (root == null)
            {
                return;
            }

            var postOrder = new List<BasicBlock>();
            var postOrderIndex = new Dictionary<BasicBlock, int>();

            var visited = new HashSet<BasicBlock>();
            var stack = new Stack<(BasicBlock Node, IEnumerator<BasicBlock> Enumerator)>();

            visited.Add(root);
            stack.Push((root, getSuccessors(root).GetEnumerator()));

            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (top.Enumerator.MoveNext())
                {
                    var succ = top.Enumerator.Current;
                    if (visited.Add(succ))
                    {
                        stack.Push((succ, getSuccessors(succ).GetEnumerator()));
                    }
                }
                else
                {
                    top.Enumerator.Dispose();
                    stack.Pop();
                    postOrderIndex[top.Node] = postOrder.Count;
                    postOrder.Add(top.Node);
                }
            }

            foreach (var node in visited)
            {
                _reachable.Add(node);
            }

            var rpo = new List<BasicBlock>(postOrder);
            rpo.Reverse();

            var doms = new Dictionary<BasicBlock, BasicBlock>();
            doms[root] = root;

            BasicBlock Intersect(BasicBlock b1, BasicBlock b2)
            {
                var finger1 = b1;
                var finger2 = b2;
                while (!ReferenceEquals(finger1, finger2))
                {
                    while (postOrderIndex[finger1] < postOrderIndex[finger2])
                    {
                        finger1 = doms[finger1];
                    }
                    while (postOrderIndex[finger2] < postOrderIndex[finger1])
                    {
                        finger2 = doms[finger2];
                    }
                }
                return finger1;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 1; i < rpo.Count; i++)
                {
                    var b = rpo[i];
                    BasicBlock? newIdom = null;

                    foreach (var p in getPredecessors(b))
                    {
                        if (doms.ContainsKey(p))
                        {
                            newIdom = p;
                            break;
                        }
                    }

                    if (newIdom == null)
                    {
                        continue;
                    }

                    foreach (var p in getPredecessors(b))
                    {
                        if (!ReferenceEquals(p, newIdom) && doms.ContainsKey(p))
                        {
                            newIdom = Intersect(p, newIdom);
                        }
                    }

                    if (!doms.TryGetValue(b, out var currentIdom) || !ReferenceEquals(currentIdom, newIdom))
                    {
                        doms[b] = newIdom;
                        changed = true;
                    }
                }
            }

            _idom[root] = null;
            for (var i = 1; i < rpo.Count; i++)
            {
                var b = rpo[i];
                if (doms.TryGetValue(b, out var parent))
                {
                    _idom[b] = parent;
                }
            }

            var domChildren = new Dictionary<BasicBlock, List<BasicBlock>>();
            foreach (var b in visited)
            {
                domChildren[b] = [];
            }
            for (var i = 1; i < rpo.Count; i++)
            {
                var b = rpo[i];
                if (doms.TryGetValue(b, out var parent))
                {
                    domChildren[parent].Add(b);
                }
            }

            var timer = 0;
            var treeStack = new Stack<(BasicBlock Node, IEnumerator<BasicBlock> Enumerator)>();
            _tin[root] = ++timer;
            treeStack.Push((root, domChildren[root].GetEnumerator()));

            while (treeStack.Count > 0)
            {
                var top = treeStack.Peek();
                if (top.Enumerator.MoveNext())
                {
                    var child = top.Enumerator.Current;
                    _tin[child] = ++timer;
                    treeStack.Push((child, domChildren[child].GetEnumerator()));
                }
                else
                {
                    top.Enumerator.Dispose();
                    treeStack.Pop();
                    _tout[top.Node] = ++timer;
                }
            }
        }

        public bool IsReachable(BasicBlock block) => _reachable.Contains(block);

        public BasicBlock? GetImmediateDominator(BasicBlock block) =>
            _idom.GetValueOrDefault(block);

        public bool Dominates(BasicBlock a, BasicBlock b)
        {
            if (ReferenceEquals(a, b))
            {
                return _reachable.Contains(a);
            }
            if (!_tin.TryGetValue(a, out var inA) || !_tin.TryGetValue(b, out var inB))
            {
                return false;
            }
            return inA <= inB && _tout[a] >= _tout[b];
        }
    }
}
