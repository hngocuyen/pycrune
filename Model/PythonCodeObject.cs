namespace PycRune.Model;

public sealed class PythonCodeObject
{
    public required string Name { get; init; }
    public required string QualName { get; init; }
    public string PythonVersion { get; init; } = "";
    public required string FileName { get; init; }
    public required int FirstLineNo { get; init; }
    public required int ArgCount { get; init; }
    public required int PosOnlyArgCount { get; init; }
    public required int KwOnlyArgCount { get; init; }
    public required int StackSize { get; init; }
    public required int Flags { get; init; }
    public required IReadOnlyList<object?> Constants { get; init; }
    public required IReadOnlyList<string> Names { get; init; }
    public required IReadOnlyList<string> VarNames { get; init; }
    public required IReadOnlyList<string> FreeVars { get; init; }
    public required IReadOnlyList<string> CellVars { get; init; }
    public required IReadOnlyList<PythonInstruction> Instructions { get; init; }
    public required IReadOnlyList<ExceptionTableEntry> ExceptionTable { get; init; }
    public required IReadOnlyList<PythonCodeObject> NestedCodes { get; init; }
}
