namespace PycRune.CFG;

using PycRune.Model;

public sealed class BasicBlock
{
    public required int Id { get; init; }
    public required int StartOffset { get; init; }
    public int EndOffset => Instructions.Count > 0 ? Instructions[^1].Offset : StartOffset;
    public List<PythonInstruction> Instructions { get; } = [];
    public List<BasicBlock> Predecessors { get; } = [];
    public List<BasicBlock> Successors { get; } = [];
    public bool IsExceptionTarget { get; set; }
    public int ExceptionDepth { get; set; }

    public override string ToString() =>
        $"Block #{Id} [{StartOffset}..{EndOffset}] (Preds: {Predecessors.Count}, Succs: {Successors.Count})";
}
