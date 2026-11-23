namespace PycRune.Verification;

using System.Collections;
using System.Text;
using PycRune.AST;
using PycRune.Core;
using PycRune.Model;

public sealed class BytecodeComparer
{
    public sealed record DiffResult(
        bool IsIdentical,
        int TotalOriginal,
        int TotalDecompiled,
        IReadOnlyList<string> Discrepancies
    );

    public static DiffResult Compare(
        string originalPath, string decompiledSource, PythonCodeObject? originalCode = null)
    {
        var origCode = originalCode ?? PyBridge.Load(originalPath);
        var decompCode = PyBridge.LoadSource(decompiledSource, origCode.PythonVersion);

        var diffs = new List<string>();
        var origTotal = 0;
        var decompTotal = 0;
        CompareCodeObject(origCode, decompCode, "<module>", diffs, ref origTotal, ref decompTotal, 0);

        return new DiffResult(diffs.Count == 0, origTotal, decompTotal, diffs);
    }

    private static void CompareCodeObject(
        PythonCodeObject orig,
        PythonCodeObject decomp,
        string path,
        List<string> diffs,
        ref int origTotal,
        ref int decompTotal,
        int depth)
    {
        if (depth > 64)
        {
            diffs.Add($"{path}: code-object nesting exceeds 64 levels");
            return;
        }

        if (orig.Name != decomp.Name)
        {
            diffs.Add($"{path}: code name '{orig.Name}' vs '{decomp.Name}'");
        }
        if (orig.QualName != decomp.QualName)
        {
            diffs.Add($"{path}: qualname '{orig.QualName}' vs '{decomp.QualName}'");
        }

        CompareScalar(path, "argcount", orig.ArgCount, decomp.ArgCount, diffs);
        CompareScalar(path, "posonlyargcount", orig.PosOnlyArgCount, decomp.PosOnlyArgCount, diffs);
        CompareScalar(path, "kwonlyargcount", orig.KwOnlyArgCount, decomp.KwOnlyArgCount, diffs);
        CompareScalar(path, "flags", orig.Flags, decomp.Flags, diffs);
        CompareStringList(path, "names", orig.Names, decomp.Names, diffs);
        CompareStringList(path, "varnames", orig.VarNames, decomp.VarNames, diffs);
        CompareStringList(path, "freevars", orig.FreeVars, decomp.FreeVars, diffs);
        CompareStringList(path, "cellvars", orig.CellVars, decomp.CellVars, diffs);
        var origSkip = LineMetadataSkips(orig);
        var decompSkip = LineMetadataSkips(decomp);
        CompareConstants(path, FilterConstants(orig.Constants, origSkip), FilterConstants(decomp.Constants, decompSkip), diffs);


        var origInstrs = orig.Instructions.Where(i => !IsTransparent(i))
            .Select(i => RemapConstIndex(i, origSkip)).ToList();
        var decompInstrs = decomp.Instructions.Where(i => !IsTransparent(i))
            .Select(i => RemapConstIndex(i, decompSkip)).ToList();
        origTotal += origInstrs.Count;
        decompTotal += decompInstrs.Count;
        CompareInstructions(path, origInstrs, decompInstrs, diffs);
        CompareExceptionTable(path, orig, decomp, origInstrs, decompInstrs, diffs);

        var origNested = orig.NestedCodes;
        var decompNested = decomp.NestedCodes;
        if (origNested.Count != decompNested.Count)
        {
            diffs.Add($"{path}: nested code-object count {origNested.Count} vs {decompNested.Count}");
        }

        for (var i = 0; i < Math.Min(origNested.Count, decompNested.Count); i++)
        {
            CompareCodeObject(
                origNested[i],
                decompNested[i],
                $"{path}/{origNested[i].Name}#{i}",
                diffs,
                ref origTotal,
                ref decompTotal,
                depth + 1);
        }
    }

    private static bool IsTransparent(PythonInstruction instruction) =>
        instruction.OpCode is OpCodes.NOP or OpCodes.EXTENDED_ARG or OpCodes.NOT_TAKEN;

    private static bool IsFirstLineMetadataLoad(IReadOnlyList<PythonInstruction> instrs, int index) =>
        index + 1 < instrs.Count &&
        instrs[index].OpCode == OpCodes.LOAD_CONST &&
        instrs[index + 1].OpCode == OpCodes.STORE_NAME &&
        (instrs[index + 1].ArgVal?.ToString() ?? instrs[index + 1].ArgRepr) == "__firstlineno__";

    private static bool IsLineNumberConstant(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static HashSet<int> LineMetadataSkips(PythonCodeObject code)
    {
        var skip = new HashSet<int>();
        for (var i = 0; i + 1 < code.Instructions.Count; i++)
        {
            if (IsFirstLineMetadataLoad(code.Instructions, i) && code.Instructions[i].OpArg is int constIndex &&
                constIndex >= 0 && constIndex < code.Constants.Count &&
                IsLineNumberConstant(code.Constants[constIndex]))
            {


                skip.Add(constIndex);
            }
        }
        return skip;
    }

    private static IReadOnlyList<object?> FilterConstants(IReadOnlyList<object?> constants, HashSet<int> skip)
    {
        if (skip.Count == 0) return constants;
        return constants.Where((_, i) => !skip.Contains(i)).ToList();
    }

    private static PythonInstruction RemapConstIndex(PythonInstruction ins, HashSet<int> skip)
    {
        if (skip.Count == 0 || ins.OpArg is not int idx) return ins;
        if (ins.OpCode is not (OpCodes.LOAD_CONST or OpCodes.KW_NAMES)) return ins;
        var mapped = idx;
        for (var k = 0; k < idx; k++)
        {
            if (skip.Contains(k)) mapped--;
        }
        return ins with { OpArg = mapped };
    }

    private static void CompareInstructions(
        string path,
        IReadOnlyList<PythonInstruction> origInstrs,
        IReadOnlyList<PythonInstruction> decompInstrs,
        List<string> diffs)
    {
        var origOffsets = origInstrs.Select(instruction => instruction.Offset).ToArray();
        var decompOffsets = decompInstrs.Select(instruction => instruction.Offset).ToArray();
        var maxLen = Math.Max(origInstrs.Count, decompInstrs.Count);

        for (var i = 0; i < maxLen; i++)
        {
            if (i >= origInstrs.Count)
            {
                diffs.Add($"{path}: extra instruction #{i} in decompiled: {decompInstrs[i]}");
                continue;
            }
            if (i >= decompInstrs.Count)
            {
                diffs.Add($"{path}: missing instruction #{i} in decompiled: {origInstrs[i]}");
                continue;
            }

            var orig = origInstrs[i];
            var decomp = decompInstrs[i];

            if (orig.OpCode != decomp.OpCode)
            {
                diffs.Add($"{path}: instr #{i} opcode {orig.OpName} vs {decomp.OpName}");
                continue;
            }


            if (IsFirstLineMetadataLoad(origInstrs, i) && IsFirstLineMetadataLoad(decompInstrs, i))
            {
                continue;
            }

            if (orig.TargetOffset.HasValue || decomp.TargetOffset.HasValue)
            {


                var origTarget = orig.TargetOffset.HasValue ? MapBoundary(origOffsets, orig.TargetOffset.Value) : -1;
                var decompTarget = decomp.TargetOffset.HasValue ? MapBoundary(decompOffsets, decomp.TargetOffset.Value) : -1;
                if (origTarget != decompTarget)
                {
                    diffs.Add($"{path}: instr #{i} {orig.OpName} jump target #{origTarget} vs #{decompTarget} " +
                              $"(offset {orig.TargetOffset} vs {decomp.TargetOffset})");
                }
                continue;
            }


            if (orig.OpCode == OpCodes.LOAD_CONST && ValuesEqual(orig.ArgVal, decomp.ArgVal))
            {
                continue;
            }

            if (orig.OpArg != decomp.OpArg)
            {
                diffs.Add($"{path}: instr #{i} {orig.OpName} arg {orig.OpArg} vs {decomp.OpArg} " +
                          $"(value {FormatValue(orig.ArgVal)} vs {FormatValue(decomp.ArgVal)})");
                continue;
            }

            if (!ValuesEqual(orig.ArgVal, decomp.ArgVal))
            {
                diffs.Add($"{path}: instr #{i} {orig.OpName} value {FormatValue(orig.ArgVal)} vs {FormatValue(decomp.ArgVal)}");
            }
        }
    }

    private static void CompareExceptionTable(
        string path,
        PythonCodeObject orig,
        PythonCodeObject decomp,
        IReadOnlyList<PythonInstruction> origInstrs,
        IReadOnlyList<PythonInstruction> decompInstrs,
        List<string> diffs)
    {
        if (orig.ExceptionTable.Count != decomp.ExceptionTable.Count)
        {
            diffs.Add($"{path}: exception table entries {orig.ExceptionTable.Count} vs {decomp.ExceptionTable.Count}");
        }

        var origIdx = BuildOffsetIndex(origInstrs);
        var decompIdx = BuildOffsetIndex(decompInstrs);
        var origOffsets = origInstrs.Select(i => i.Offset).ToArray();
        var decompOffsets = decompInstrs.Select(i => i.Offset).ToArray();

        for (var i = 0; i < Math.Min(orig.ExceptionTable.Count, decomp.ExceptionTable.Count); i++)
        {
            var a = orig.ExceptionTable[i];
            var b = decomp.ExceptionTable[i];
            var normalizedA = (Start: MapBoundary(origOffsets, a.Start), End: MapBoundary(origOffsets, a.End), Target: Map(origIdx, a.Target), a.Depth, a.LastI);
            var normalizedB = (Start: MapBoundary(decompOffsets, b.Start), End: MapBoundary(decompOffsets, b.End), Target: Map(decompIdx, b.Target), b.Depth, b.LastI);

            if (!normalizedA.Equals(normalizedB))
            {
                diffs.Add($"{path}: exception entry #{i} " +
                          $"[{normalizedA.Start}..{normalizedA.End}) -> #{normalizedA.Target} depth {normalizedA.Depth} lasti {normalizedA.LastI} " +
                          $"vs [{normalizedB.Start}..{normalizedB.End}) -> #{normalizedB.Target} depth {normalizedB.Depth} lasti {normalizedB.LastI}");
            }
        }
    }

    private static Dictionary<int, int> BuildOffsetIndex(IReadOnlyList<PythonInstruction> instructions)
    {
        var map = new Dictionary<int, int>();
        for (var i = 0; i < instructions.Count; i++)
        {
            map[instructions[i].Offset] = i;
        }
        return map;
    }

    private static int Map(Dictionary<int, int> index, int offset) =>
        index.TryGetValue(offset, out var value) ? value : -1;

        private static int MapBoundary(IReadOnlyList<int> offsets, int boundary)
    {
        for (var i = 0; i < offsets.Count; i++)
        {
            if (offsets[i] >= boundary)
            {
                return i;
            }
        }
        return offsets.Count;
    }

    private static void CompareScalar(string path, string label, int orig, int decomp, List<string> diffs)
    {
        if (orig != decomp)
        {
            diffs.Add($"{path}: {label} {orig} vs {decomp}");
        }
    }

    private static void CompareStringList(string path, string label, IReadOnlyList<string> orig, IReadOnlyList<string> decomp, List<string> diffs)
    {
        if (orig.Count != decomp.Count || !orig.SequenceEqual(decomp, StringComparer.Ordinal))
        {
            diffs.Add($"{path}: {label} [{string.Join(", ", orig)}] vs [{string.Join(", ", decomp)}]");
        }
    }

    private static void CompareConstants(string path, IReadOnlyList<object?> orig, IReadOnlyList<object?> decomp, List<string> diffs)
    {
        if (orig.Count != decomp.Count)
        {
            diffs.Add($"{path}: constant count {orig.Count} vs {decomp.Count}");
        }

        for (var i = 0; i < Math.Min(orig.Count, decomp.Count); i++)
        {
            if (!ValuesEqual(orig[i], decomp[i]))
            {
                diffs.Add($"{path}: constant #{i} {FormatValue(orig[i])} vs {FormatValue(decomp[i])}");
            }
        }
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        if (a is PyEllipsis && b is PyEllipsis) return true;

        if (a.GetType() != b.GetType())
        {
            return false;
        }

        switch (a)
        {
            case bool ba when b is bool bb:
                return ba == bb;
            case long la when b is long lb:
                return la == lb;
            case double da when b is double db:
                return da.Equals(db);
            case System.Numerics.Complex ca when b is System.Numerics.Complex cb:
                return ca.Equals(cb);
            case byte[] ba when b is byte[] bb:
                return ba.AsSpan().SequenceEqual(bb);
            case string sa when b is string sb:
                return string.Equals(sa, sb, StringComparison.Ordinal);
            case SliceValue slA when b is SliceValue slB:
                return ValuesEqual(slA.Start, slB.Start) &&
                       ValuesEqual(slA.Stop, slB.Stop) &&
                       ValuesEqual(slA.Step, slB.Step);
        }

        if (a is IList listA && b is IList listB)
        {
            if (listA.Count != listB.Count) return false;
            for (var i = 0; i < listA.Count; i++)
            {
                if (!ValuesEqual(listA[i], listB[i])) return false;
            }
            return true;
        }

        if (a is HashSet<object?> setA && b is HashSet<object?> setB)
        {
            if (setA.Count != setB.Count) return false;
            foreach (var item in setA)
            {
                if (!setB.Any(other => ValuesEqual(item, other))) return false;
            }
            return true;
        }

        if (a is FrozenSetValue frozenA && b is FrozenSetValue frozenB)
        {
            if (frozenA.Items.Count != frozenB.Items.Count) return false;
            foreach (var item in frozenA.Items)
            {
                if (!frozenB.Items.Any(other => ValuesEqual(item, other))) return false;
            }
            return true;
        }

        return a.Equals(b);
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "None";
        if (value is PyEllipsis) return "Ellipsis";
        if (value is string text) return $"'{text}'";
        if (value is bool flag) return flag ? "True" : "False";
        if (value is double d) return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (value is IList list) return "[" + string.Join(", ", list.Cast<object?>().Select(FormatValue)) + "]";
        if (value is HashSet<object?> set) return "{" + string.Join(", ", set.Select(FormatValue)) + "}";
        if (value is FrozenSetValue frozen) return "frozenset({" + string.Join(", ", frozen.Items.Select(FormatValue)) + "})";
        if (value is SliceValue slice) return $"slice({FormatValue(slice.Start)}, {FormatValue(slice.Stop)}, {FormatValue(slice.Step)})";
        return $"{value} ({value.GetType().Name})";
    }
}
