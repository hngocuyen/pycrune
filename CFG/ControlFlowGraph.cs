namespace PycRune.CFG;

using PycRune.Model;

public sealed class ControlFlowGraph
{
    public required PythonCodeObject Code { get; init; }
    public required IReadOnlyList<BasicBlock> Blocks { get; init; }
    public required Dictionary<int, BasicBlock> OffsetToBlock { get; init; }

    public static ControlFlowGraph Build(PythonCodeObject code)
    {
        var instrs = code.Instructions;
        if (instrs.Count == 0)
        {
            return new ControlFlowGraph
            {
                Code = code,
                Blocks = [],
                OffsetToBlock = []
            };
        }

        var leaders = new SortedSet<int> { 0 };

        foreach (var entry in code.ExceptionTable)
        {
            leaders.Add(entry.Start);
            leaders.Add(entry.End);
            leaders.Add(entry.Target);
        }

        for (var i = 0; i < instrs.Count; i++)
        {
            var ins = instrs[i];
            if (ins.TargetOffset.HasValue)
            {
                leaders.Add(ins.TargetOffset.Value);
            }

            if (OpCodes.IsJump(ins.OpCode) || OpCodes.IsTerminator(ins.OpCode))
            {
                if (i + 1 < instrs.Count)
                {
                    leaders.Add(instrs[i + 1].Offset);
                }
            }
        }

        var blocks = new List<BasicBlock>();
        var offsetToBlock = new Dictionary<int, BasicBlock>();
        BasicBlock? currentBlock = null;
        var blockId = 0;

        foreach (var ins in instrs)
        {
            if (leaders.Contains(ins.Offset))
            {
                currentBlock = new BasicBlock
                {
                    Id = blockId++,
                    StartOffset = ins.Offset
                };
                blocks.Add(currentBlock);
            }

            if (currentBlock != null)
            {
                currentBlock.Instructions.Add(ins);
                offsetToBlock[ins.Offset] = currentBlock;
            }
        }

        var excTargets = code.ExceptionTable.Select(e => e.Target).ToHashSet();
        foreach (var b in blocks)
        {
            if (excTargets.Contains(b.StartOffset))
            {
                b.IsExceptionTarget = true;
            }
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Instructions.Count == 0) continue;

            var last = b.Instructions[^1];
            var nextBlock = i + 1 < blocks.Count ? blocks[i + 1] : null;

            if (last.OpCode is OpCodes.JUMP_FORWARD or OpCodes.JUMP_BACKWARD or OpCodes.JUMP_BACKWARD_NO_INTERRUPT)
            {
                if (last.TargetOffset.HasValue && offsetToBlock.TryGetValue(last.TargetOffset.Value, out var target))
                {
                    AddEdge(b, target);
                }
            }
            else if (OpCodes.IsRelativeForwardJump(last.OpCode) || last.OpCode == OpCodes.FOR_ITER)
            {
                if (last.TargetOffset.HasValue && offsetToBlock.TryGetValue(last.TargetOffset.Value, out var target))
                {
                    AddEdge(b, target);
                }
                if (nextBlock != null)
                {
                    AddEdge(b, nextBlock);
                }
            }
            else if (!OpCodes.IsTerminator(last.OpCode))
            {
                if (nextBlock != null)
                {
                    AddEdge(b, nextBlock);
                }
            }
        }

        return new ControlFlowGraph
        {
            Code = code,
            Blocks = blocks,
            OffsetToBlock = offsetToBlock
        };
    }

    private static void AddEdge(BasicBlock from, BasicBlock to)
    {
        if (!from.Successors.Contains(to))
        {
            from.Successors.Add(to);
        }
        if (!to.Predecessors.Contains(from))
        {
            to.Predecessors.Add(from);
        }
    }
}
