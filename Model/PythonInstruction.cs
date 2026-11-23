namespace PycRune.Model;

public sealed record PythonInstruction(
    int Offset,
    int OpCode,
    string OpName,
    int? OpArg,
    object? ArgVal,
    string? ArgRepr,
    int? TargetOffset,
    bool IsJumpTarget,
    int? StartsLine
)
{
    public override string ToString()
    {
        var targetStr = TargetOffset.HasValue ? $" -> {TargetOffset.Value}" : "";
        var argStr = OpArg.HasValue ? $" {OpArg.Value} ({ArgRepr ?? ArgVal?.ToString() ?? ""})" : "";
        return $"{Offset,6}: {OpName,-26}{argStr}{targetStr}";
    }
}
