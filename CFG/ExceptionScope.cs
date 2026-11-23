namespace PycRune.CFG;

using PycRune.Model;

public enum ExceptionHandlerKind
{
    Except,
    ExceptStar,
    Finally
}

public sealed class ExceptionHandler
{
    public required ExceptionHandlerKind Kind { get; init; }
    public required BasicBlock EntryBlock { get; init; }
    public required int TargetOffset { get; init; }
    public required HashSet<BasicBlock> Blocks { get; init; }
    public ExceptionTableEntry? TableEntry { get; init; }

    public bool IsFinally => Kind == ExceptionHandlerKind.Finally;
    public bool IsStar => Kind == ExceptionHandlerKind.ExceptStar;
    public int StartOffset => EntryBlock.StartOffset;
    public int EndOffset => Blocks.Count > 0 ? Blocks.Max(b => b.EndOffset) : EntryBlock.EndOffset;

    public IReadOnlyList<PythonInstruction> GetInstructions() =>
        Blocks.OrderBy(b => b.StartOffset).SelectMany(b => b.Instructions).ToList();
}

public sealed class ExceptionScope
{
    public required int Id { get; init; }
    public required int StartOffset { get; init; }
    public required int EndOffset { get; init; }
    public required HashSet<BasicBlock> TryBlocks { get; init; }
    public required IReadOnlyList<ExceptionHandler> Handlers { get; init; }
    public ExceptionHandler? FinallyHandler { get; init; }
    public required HashSet<BasicBlock> ElseBlocks { get; init; }
    public BasicBlock? ExitBlock { get; init; }
    public ExceptionScope? Parent { get; set; }
    public List<ExceptionScope> Children { get; } = [];

    public bool HasHandlers => Handlers.Count > 0;
    public bool HasFinally => FinallyHandler != null;
    public bool HasElse => ElseBlocks.Count > 0;

    public bool ContainsOffset(int offset) => offset >= StartOffset && offset < EndOffset;

    public bool ContainsBlock(BasicBlock block) =>
        TryBlocks.Contains(block) ||
        Handlers.Any(h => h.Blocks.Contains(block)) ||
        (FinallyHandler != null && FinallyHandler.Blocks.Contains(block)) ||
        ElseBlocks.Contains(block);

    public IReadOnlyList<PythonInstruction> GetTryInstructions() =>
        TryBlocks.OrderBy(b => b.StartOffset).SelectMany(b => b.Instructions).ToList();

    public IReadOnlyList<PythonInstruction> GetElseInstructions() =>
        ElseBlocks.OrderBy(b => b.StartOffset).SelectMany(b => b.Instructions).ToList();

    public IReadOnlyList<PythonInstruction> GetFinallyInstructions() =>
        FinallyHandler != null ? FinallyHandler.GetInstructions() : [];

    public static IReadOnlyList<ExceptionScope> Analyze(ControlFlowGraph cfg, DominatorTree domTree)
    {
        var entries = cfg.Code.ExceptionTable;
        if (entries.Count == 0 || cfg.Blocks.Count == 0)
        {
            return [];
        }

        var offsetToBlock = cfg.OffsetToBlock;
        var blocks = cfg.Blocks;

        var userEntries = entries
            .Where(e => offsetToBlock.ContainsKey(e.Target))
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.End)
            .ToList();

        if (userEntries.Count == 0)
        {
            return [];
        }

        var scopes = new List<ExceptionScope>();
        var groupedByRange = userEntries
            .GroupBy(e => (e.Start, e.End))
            .OrderBy(g => g.Key.Start)
            .ThenByDescending(g => g.Key.End);

        var scopeId = 0;

        foreach (var group in groupedByRange)
        {
            var (start, end) = group.Key;
            var tryBlocks = blocks
                .Where(b => b.StartOffset >= start && b.StartOffset < end)
                .ToHashSet();

            if (tryBlocks.Count == 0)
            {
                continue;
            }

            var primaryEntries = group.ToList();
            var exceptEntries = primaryEntries.Where(e => !IsFinallyEntry(e, offsetToBlock[e.Target])).ToList();
            var finallyEntries = primaryEntries.Where(e => IsFinallyEntry(e, offsetToBlock[e.Target])).ToList();

            var candidateChainHeads = exceptEntries.Select(e => offsetToBlock[e.Target]).Distinct().ToList();
            var allHandlerEntries = new HashSet<BasicBlock>();
            var rawHandlers = new List<(BasicBlock Entry, ExceptionHandlerKind Kind)>();

            foreach (var head in candidateChainHeads)
            {
                var cur = head;
                while (cur != null)
                {
                    if (!allHandlerEntries.Add(cur))
                    {
                        break;
                    }

                    var checkMatch = cur.Instructions.FirstOrDefault(i => i.OpCode is OpCodes.CHECK_EXC_MATCH or OpCodes.CHECK_EG_MATCH);
                    var kind = checkMatch != null && checkMatch.OpCode == OpCodes.CHECK_EG_MATCH
                        ? ExceptionHandlerKind.ExceptStar
                        : ExceptionHandlerKind.Except;

                    rawHandlers.Add((cur, kind));

                    if (checkMatch == null)
                    {
                        break;
                    }

                    var jumpIns = cur.Instructions.FirstOrDefault(i => i.OpCode is OpCodes.POP_JUMP_IF_FALSE or OpCodes.POP_JUMP_IF_TRUE);
                    if (jumpIns?.TargetOffset != null && offsetToBlock.TryGetValue(jumpIns.TargetOffset.Value, out var nextBlock))
                    {
                        if (nextBlock.Instructions.Any(i => i.OpCode == OpCodes.RERAISE && (i.OpArg ?? 0) == 0))
                        {
                            break;
                        }
                        cur = nextBlock;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            BasicBlock? finallyEntryBlock = null;
            ExceptionTableEntry? finallyTableEntry = null;

            if (finallyEntries.Count > 0)
            {
                finallyTableEntry = finallyEntries[0];
                finallyEntryBlock = offsetToBlock[finallyTableEntry.Target];
                allHandlerEntries.Add(finallyEntryBlock);
            }

            var allClauseExits = new HashSet<BasicBlock>();

            foreach (var tb in tryBlocks)
            {
                foreach (var succ in tb.Successors)
                {
                    if (!tryBlocks.Contains(succ) && !allHandlerEntries.Contains(succ))
                    {
                        allClauseExits.Add(succ);
                    }
                }
            }

            foreach (var (hEntry, _) in rawHandlers)
            {
                foreach (var succ in hEntry.Successors)
                {
                    if (!tryBlocks.Contains(succ) && !allHandlerEntries.Contains(succ))
                    {
                        allClauseExits.Add(succ);
                    }
                }
            }

            if (finallyEntryBlock != null)
            {
                foreach (var succ in finallyEntryBlock.Successors)
                {
                    if (!tryBlocks.Contains(succ) && !allHandlerEntries.Contains(succ))
                    {
                        allClauseExits.Add(succ);
                    }
                }
            }

            BasicBlock? exitBlock = null;
            if (allClauseExits.Count > 0)
            {
                exitBlock = FindConvergenceBlock(allClauseExits, domTree);
            }
            else if (finallyEntryBlock != null)
            {
                exitBlock = finallyEntryBlock;
            }

            var handlers = new List<ExceptionHandler>();
            foreach (var (hEntry, kind) in rawHandlers)
            {
                var hBlocks = CollectHandlerBlocks(hEntry, exitBlock, tryBlocks, allHandlerEntries);
                var matchingEntry = exceptEntries.FirstOrDefault(e => e.Target == hEntry.StartOffset);

                handlers.Add(new ExceptionHandler
                {
                    Kind = kind,
                    EntryBlock = hEntry,
                    TargetOffset = hEntry.StartOffset,
                    Blocks = hBlocks,
                    TableEntry = matchingEntry
                });
            }

            ExceptionHandler? finallyHandler = null;
            if (finallyEntryBlock != null)
            {
                var fBlocks = CollectHandlerBlocks(finallyEntryBlock, exitBlock, tryBlocks, allHandlerEntries);
                finallyHandler = new ExceptionHandler
                {
                    Kind = ExceptionHandlerKind.Finally,
                    EntryBlock = finallyEntryBlock,
                    TargetOffset = finallyEntryBlock.StartOffset,
                    Blocks = fBlocks,
                    TableEntry = finallyTableEntry
                };
            }

            var elseBlocks = new HashSet<BasicBlock>();
            if (exitBlock != null)
            {
                foreach (var tb in tryBlocks)
                {
                    foreach (var succ in tb.Successors)
                    {
                        if (!tryBlocks.Contains(succ) && succ != exitBlock && !allHandlerEntries.Contains(succ))
                        {
                            var collected = CollectElseBlocks(succ, exitBlock, tryBlocks, allHandlerEntries);
                            foreach (var b in collected)
                            {
                                elseBlocks.Add(b);
                            }
                        }
                    }
                }
            }

            var scope = new ExceptionScope
            {
                Id = scopeId++,
                StartOffset = start,
                EndOffset = end,
                TryBlocks = tryBlocks,
                Handlers = handlers,
                FinallyHandler = finallyHandler,
                ElseBlocks = elseBlocks,
                ExitBlock = exitBlock
            };

            scopes.Add(scope);
        }

        BuildScopeHierarchy(scopes);

        return scopes;
    }

    private static bool IsFinallyEntry(ExceptionTableEntry entry, BasicBlock targetBlock)
    {
        if (entry.LastI)
        {
            return true;
        }

        var instrs = targetBlock.Instructions;
        if (instrs.Count == 0)
        {
            return false;
        }

        var hasCheckMatch = instrs.Any(i => i.OpCode is OpCodes.CHECK_EXC_MATCH or OpCodes.CHECK_EG_MATCH);
        if (hasCheckMatch)
        {
            return false;
        }

        return instrs.Any(i => i.OpCode == OpCodes.RERAISE && (i.OpArg ?? 0) == 0);
    }

    private static BasicBlock? FindConvergenceBlock(HashSet<BasicBlock> candidateExits, DominatorTree domTree)
    {
        var candidates = candidateExits.ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var lca = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var target = candidates[i];
            while (lca != null && !domTree.PostDominates(lca, target))
            {
                lca = domTree.GetImmediatePostDominator(lca);
            }
            if (lca == null)
            {
                break;
            }
        }

        return lca;
    }

    private static HashSet<BasicBlock> CollectHandlerBlocks(
        BasicBlock entryBlock,
        BasicBlock? exitBlock,
        HashSet<BasicBlock> tryBlocks,
        HashSet<BasicBlock> allHandlerEntries)
    {
        var result = new HashSet<BasicBlock>();
        var visited = new HashSet<BasicBlock>();
        var queue = new Queue<BasicBlock>();

        queue.Enqueue(entryBlock);
        visited.Add(entryBlock);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            result.Add(cur);

            foreach (var succ in cur.Successors)
            {
                if (succ == exitBlock || tryBlocks.Contains(succ))
                {
                    continue;
                }

                if (allHandlerEntries.Contains(succ) && succ != entryBlock)
                {
                    continue;
                }

                if (visited.Add(succ))
                {
                    queue.Enqueue(succ);
                }
            }
        }

        return result;
    }

    private static HashSet<BasicBlock> CollectElseBlocks(
        BasicBlock startBlock,
        BasicBlock exitBlock,
        HashSet<BasicBlock> tryBlocks,
        HashSet<BasicBlock> allHandlerEntries)
    {
        var result = new HashSet<BasicBlock>();
        var visited = new HashSet<BasicBlock>();
        var queue = new Queue<BasicBlock>();

        queue.Enqueue(startBlock);
        visited.Add(startBlock);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            result.Add(cur);

            foreach (var succ in cur.Successors)
            {
                if (succ == exitBlock || tryBlocks.Contains(succ) || allHandlerEntries.Contains(succ))
                {
                    continue;
                }

                if (visited.Add(succ))
                {
                    queue.Enqueue(succ);
                }
            }
        }

        return result;
    }

    private static void BuildScopeHierarchy(List<ExceptionScope> scopes)
    {
        for (var i = 0; i < scopes.Count; i++)
        {
            for (var j = 0; j < scopes.Count; j++)
            {
                if (i == j) continue;
                var a = scopes[i];
                var b = scopes[j];

                if (a.StartOffset <= b.StartOffset && a.EndOffset >= b.EndOffset)
                {
                    if (b.Parent == null || (b.Parent.StartOffset <= a.StartOffset && b.Parent.EndOffset >= a.EndOffset))
                    {
                        b.Parent?.Children.Remove(b);
                        b.Parent = a;
                        a.Children.Add(b);
                    }
                }
            }
        }
    }
}
