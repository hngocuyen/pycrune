namespace PycRune.Decompiler;

using System.Text;
using PycRune.AST;
using PycRune.Model;

public sealed class PythonEmitter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public static string Emit(IReadOnlyList<StmtNode> statements)
    {
        var emitter = new PythonEmitter();
        emitter.EmitStatements(statements);
        return emitter._sb.ToString();
    }

    public static string EmitExpr(ExprNode expr)
    {
        var emitter = new PythonEmitter();
        emitter.WriteExpr(expr, 0);
        return emitter._sb.ToString();
    }

    private void EmitStatements(IReadOnlyList<StmtNode> statements)
    {
        if (statements.Count == 0)
        {
            WriteIndent();
            _sb.AppendLine("pass");
            return;
        }

        foreach (var stmt in statements)
        {
            EmitStatement(stmt);
        }
    }

    private void EmitStatement(StmtNode stmt)
    {
        switch (stmt)
        {
            case ExprStmt s:
                WriteIndent();
                WriteExpr(s.Expression, 1);
                _sb.AppendLine();
                break;

            case AssignStmt s:
                WriteIndent();
                for (var i = 0; i < s.Targets.Count; i++)
                {
                    WriteExpr(s.Targets[i], 0);
                    _sb.Append(" = ");
                }
                WriteExpr(s.Value, 1);
                _sb.AppendLine();
                break;

            case AugAssignStmt s:
                WriteIndent();
                WriteExpr(s.Target, 0);
                _sb.Append($" {s.Op} ");
                WriteExpr(s.Value, 1);
                _sb.AppendLine();
                break;

            case IfStmt s:
                WriteIndent();
                _sb.Append("if ");
                WriteExpr(s.Condition, 0);
                _sb.AppendLine(":");
                _indent++;
                EmitStatements(s.ThenBody);
                _indent--;
                if (s.ElseBody.Count > 0)
                {
                    if (s.ElseBody.Count == 1 && s.ElseBody[0] is IfStmt elif)
                    {
                        EmitElif(elif);
                    }
                    else
                    {
                        WriteIndent();
                        _sb.AppendLine("else:");
                        _indent++;
                        EmitStatements(s.ElseBody);
                        _indent--;
                    }
                }
                break;

            case WhileStmt s:
                WriteIndent();
                _sb.Append("while ");
                WriteExpr(s.Condition, 0);
                _sb.AppendLine(":");
                _indent++;
                EmitStatements(s.Body);
                _indent--;
                if (s.ElseBody.Count > 0)
                {
                    WriteIndent();
                    _sb.AppendLine("else:");
                    _indent++;
                    EmitStatements(s.ElseBody);
                    _indent--;
                }
                break;

            case ForStmt s:
                WriteIndent();
                if (s.IsAsync) _sb.Append("async ");
                _sb.Append("for ");
                WriteExpr(s.Target, 0);
                _sb.Append(" in ");
                WriteExpr(s.Iterator, 1);
                _sb.AppendLine(":");
                _indent++;
                EmitStatements(s.Body);
                _indent--;
                if (s.ElseBody.Count > 0)
                {
                    WriteIndent();
                    _sb.AppendLine("else:");
                    _indent++;
                    EmitStatements(s.ElseBody);
                    _indent--;
                }
                break;

            case TryStmt s:
                WriteIndent();
                _sb.AppendLine("try:");
                _indent++;
                EmitStatements(s.Body);
                _indent--;
                foreach (var h in s.Handlers)
                {
                    WriteIndent();
                    _sb.Append(h.IsStar ? "except*" : "except");
                    if (h.ExceptionType != null)
                    {
                        _sb.Append(' ');
                        WriteExpr(h.ExceptionType, 0);
                        if (!string.IsNullOrEmpty(h.VariableName))
                        {
                            GuardIdentifier(h.VariableName);
                            _sb.Append($" as {h.VariableName}");
                        }
                    }
                    _sb.AppendLine(":");
                    _indent++;
                    EmitStatements(h.Body);
                    _indent--;
                }
                if (s.ElseBody.Count > 0)
                {
                    WriteIndent();
                    _sb.AppendLine("else:");
                    _indent++;
                    EmitStatements(s.ElseBody);
                    _indent--;
                }
                if (s.FinallyBody != null)
                {
                    WriteIndent();
                    _sb.AppendLine("finally:");
                    _indent++;
                    EmitStatements(s.FinallyBody);
                    _indent--;
                }
                else if (s.Handlers.Count == 0)
                {
                    WriteIndent();
                    _sb.AppendLine("except:");
                    _indent++;
                    WriteIndent();
                    _sb.AppendLine("pass");
                    _indent--;
                }
                break;

            case RaiseStmt s:
                WriteIndent();
                _sb.Append("raise");
                if (s.Exception != null)
                {
                    _sb.Append(' ');
                    WriteExpr(s.Exception, 1);
                    if (s.From != null)
                    {
                        _sb.Append(" from ");
                        WriteExpr(s.From, 1);
                    }
                }
                _sb.AppendLine();
                break;

            case ReturnStmt s:
                WriteIndent();
                _sb.Append("return");
                if (s.Value != null)
                {
                    _sb.Append(' ');
                    WriteExpr(s.Value, 1);
                }
                _sb.AppendLine();
                break;

            case BreakStmt:
                WriteIndent();
                _sb.AppendLine("break");
                break;

            case ContinueStmt:
                WriteIndent();
                _sb.AppendLine("continue");
                break;

            case PassStmt:
                WriteIndent();
                _sb.AppendLine("pass");
                break;

            case AssertStmt s:
                WriteIndent();
                _sb.Append("assert ");
                WriteExpr(s.Test, 1);
                if (s.Message != null)
                {
                    _sb.Append(", ");
                    WriteExpr(s.Message, 1);
                }
                _sb.AppendLine();
                break;

            case NonlocalStmt s:
                WriteIndent();
                _sb.Append("nonlocal ");
                _sb.AppendLine(string.Join(", ", s.Names));
                break;

            case GlobalStmt s:
                WriteIndent();
                _sb.Append("global ");
                _sb.AppendLine(string.Join(", ", s.Names));
                break;

            case FunctionDefStmt s:
                if (s.Decorators != null)
                {
                    foreach (var dec in s.Decorators)
                    {
                        WriteIndent();
                        _sb.Append('@');
                        WriteExpr(dec, 0);
                        _sb.AppendLine();
                    }
                }
                GuardIdentifier(s.Name);
                if (s.TypeParams != null)
                {
                    foreach (var tp in s.TypeParams) GuardIdentifier(tp);
                }
                foreach (var param in s.Parameters)
                {
                    if (param.Contains("__pycrune_ph_", StringComparison.Ordinal))
                        throw new FailFastException($"internal placeholder leaked into parameter: {param}");
                }
                WriteIndent();
                if (s.IsAsync) _sb.Append("async ");
                _sb.Append($"def {s.Name}");
                if (s.TypeParams != null && s.TypeParams.Count > 0)
                {
                    _sb.Append($"[{string.Join(", ", s.TypeParams)}]");
                }
                _sb.Append($"({string.Join(", ", s.Parameters)})");
                if (!string.IsNullOrEmpty(s.ReturnType))
                {
                    _sb.Append($" -> {s.ReturnType}");
                }
                _sb.AppendLine(":");
                _indent++;
                EmitStatements(s.Body);
                _indent--;
                break;

            case ClassDefStmt s:
                if (s.Decorators != null)
                {
                    foreach (var dec in s.Decorators)
                    {
                        WriteIndent();
                        _sb.Append('@');
                        WriteExpr(dec, 0);
                        _sb.AppendLine();
                    }
                }
                GuardIdentifier(s.Name);
                if (s.TypeParams != null)
                {
                    foreach (var tp in s.TypeParams) GuardIdentifier(tp);
                }
                WriteIndent();
                _sb.Append($"class {s.Name}");
                if (s.TypeParams != null && s.TypeParams.Count > 0)
                {
                    _sb.Append($"[{string.Join(", ", s.TypeParams)}]");
                }
                if (s.Bases.Count > 0 || s.Keywords.Count > 0)
                {
                    _sb.Append('(');
                    var first = true;
                    foreach (var b in s.Bases)
                    {
                        if (!first) _sb.Append(", ");
                        first = false;
                        WriteExpr(b, 1);
                    }
                    foreach (var (kw, val) in s.Keywords)
                    {
                        if (!first) _sb.Append(", ");
                        first = false;
                        _sb.Append($"{kw}=");
                        WriteExpr(val, 1);
                    }
                    _sb.Append(')');
                }
                _sb.AppendLine(":");
                _indent++;
                EmitStatements(s.Body);
                _indent--;
                break;

            case TypeAliasStmt s:
                GuardIdentifier(s.Name);
                foreach (var tp in s.TypeParams) GuardIdentifier(tp);
                WriteIndent();
                _sb.Append($"type {s.Name}");
                if (s.TypeParams.Count > 0)
                {
                    _sb.Append($"[{string.Join(", ", s.TypeParams)}]");
                }
                _sb.Append(" = ");
                WriteExpr(s.Value, 1);
                _sb.AppendLine();
                break;

            case DeleteStmt s:
                WriteIndent();
                _sb.Append("del ");
                for (var i = 0; i < s.Targets.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    WriteExpr(s.Targets[i], 0);
                }
                _sb.AppendLine();
                break;

            case ImportStmt s:
                WriteIndent();
                _sb.Append("import ");
                for (var i = 0; i < s.Names.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(s.Names[i].Name);
                    if (!string.IsNullOrEmpty(s.Names[i].AsName))
                    {
                        _sb.Append($" as {s.Names[i].AsName}");
                    }
                }
                _sb.AppendLine();
                break;

            case ImportFromStmt s:
                WriteIndent();
                _sb.Append("from ");
                if (s.Level > 0) _sb.Append(new string('.', s.Level));
                if (!string.IsNullOrEmpty(s.Module)) _sb.Append(s.Module);
                _sb.Append(" import ");
                for (var i = 0; i < s.Names.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(s.Names[i].Name);
                    if (!string.IsNullOrEmpty(s.Names[i].AsName))
                    {
                        _sb.Append($" as {s.Names[i].AsName}");
                    }
                }
                _sb.AppendLine();
                break;

            case WithStmt s:
                WriteIndent();
                if (s.IsAsync) _sb.Append("async ");
                _sb.Append("with ");
                for (var i = 0; i < s.Items.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    WriteExpr(s.Items[i].ContextExpr, 1);
                    if (s.Items[i].OptionalVars != null)
                    {
                        _sb.Append(" as ");
                        WriteExpr(s.Items[i].OptionalVars!, 0);
                    }
                }
                _sb.AppendLine(":");
                _indent++;
                EmitStatements(s.Body);
                _indent--;
                break;

            case MatchStmt s:
                WriteIndent();
                _sb.Append("match ");
                WriteExpr(s.Subject, 0);
                _sb.AppendLine(":");
                _indent++;
                foreach (var c in s.Cases)
                {
                    WriteIndent();
                    _sb.Append("case ");
                    EmitPattern(c.Pattern);
                    if (c.Guard != null)
                    {
                        _sb.Append(" if ");
                        WriteExpr(c.Guard, 0);
                    }
                    _sb.AppendLine(":");
                    _indent++;
                    EmitStatements(c.Body);
                    _indent--;
                }
                _indent--;
                break;
        }
    }

    private void EmitPattern(PatternNode pattern)
    {
        switch (pattern)
        {
            case MatchWildcardPattern:
                _sb.Append('_');
                break;
            case MatchValuePattern v:
                WriteExpr(v.Value, 0);
                break;
            case MatchStarPattern st:
                _sb.Append('*');
                _sb.Append(st.Name);
                break;
            case MatchAsPattern a:
                if (a.Pattern != null)
                {
                    EmitPattern(a.Pattern);
                    _sb.Append(" as ");
                }
                _sb.Append(a.Name);
                break;
            case MatchOrPattern o:
                for (var i = 0; i < o.Patterns.Count; i++)
                {
                    if (i > 0) _sb.Append(" | ");
                    EmitPattern(o.Patterns[i]);
                }
                break;
            case MatchSequencePattern seq:
                _sb.Append(seq.IsList ? '[' : '(');
                for (var i = 0; i < seq.Patterns.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    EmitPattern(seq.Patterns[i]);
                }
                if (!seq.IsList && seq.Patterns.Count == 1) _sb.Append(',');
                _sb.Append(seq.IsList ? ']' : ')');
                break;
            case MatchClassPattern cls:
                WriteExpr(cls.ClassName, 0);
                _sb.Append('(');
                for (var i = 0; i < cls.Patterns.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    EmitPattern(cls.Patterns[i]);
                }
                _sb.Append(')');
                break;
            case MatchKeywordPattern kw:
                _sb.Append($"{kw.Attribute}=");
                EmitPattern(kw.Pattern);
                break;
            case MatchMappingPattern m:
                _sb.Append('{');
                var first = true;
                foreach (var (k, p) in m.Entries)
                {
                    if (!first) _sb.Append(", ");
                    first = false;
                    WriteExpr(k, 0);
                    _sb.Append(": ");
                    EmitPattern(p);
                }
                if (m.Rest != null)
                {
                    if (!first) _sb.Append(", ");
                    if (m.Rest is MatchStarPattern stRest)
                    {
                        _sb.Append("**");
                        _sb.Append(stRest.Name);
                    }
                    else
                    {
                        EmitPattern(m.Rest);
                    }
                }
                _sb.Append('}');
                break;
        }
    }

    private void EmitElif(IfStmt elif)
    {
        WriteIndent();
        _sb.Append("elif ");
        WriteExpr(elif.Condition, 0);
        _sb.AppendLine(":");
        _indent++;
        EmitStatements(elif.ThenBody);
        _indent--;
        if (elif.ElseBody.Count > 0)
        {
            if (elif.ElseBody.Count == 1 && elif.ElseBody[0] is IfStmt nestedElif)
            {
                EmitElif(nestedElif);
            }
            else
            {
                WriteIndent();
                _sb.AppendLine("else:");
                _indent++;
                EmitStatements(elif.ElseBody);
                _indent--;
            }
        }
    }

    private static void GuardIdentifier(string name)
    {


        if (name.StartsWith("__pycrune_ph_", StringComparison.Ordinal))
            throw new FailFastException($"internal placeholder leaked into output: {name}");
    }

    private void WriteExpr(ExprNode expr, int parentPrec)
    {
        var myPrec = GetPrecedence(expr);
        var needParens = myPrec < parentPrec;

        if (needParens) _sb.Append('(');

        switch (expr)
        {
            case ConstantExpr c:
                WriteConstant(c.Value);
                break;

            case NameExpr n:
                GuardIdentifier(n.Name);
                _sb.Append(n.Name);
                break;

            case AssertionErrorExpr:

                _sb.Append("AssertionError");
                break;

            case UnaryExpr u:
                _sb.Append(u.Op);
                if (u.Op == "not") _sb.Append(' ');
                WriteExpr(u.Operand, myPrec);
                break;

            case BinaryExpr b:
                WriteExpr(b.Left, myPrec);
                _sb.Append($" {b.Op} ");
                WriteExpr(b.Right, myPrec + 1);
                break;

            case CompareExpr comp:
                WriteExpr(comp.Left, comp.Left is CompareExpr ? 99 : myPrec);
                foreach (var (op, right) in comp.Comparators)
                {
                    _sb.Append($" {op} ");
                    WriteExpr(right, right is CompareExpr ? 99 : myPrec + 1);
                }
                break;

            case BoolOpExpr boolOp:
                for (var i = 0; i < boolOp.Values.Count; i++)
                {
                    if (i > 0) _sb.Append($" {boolOp.Op} ");
                    WriteExpr(boolOp.Values[i], myPrec);
                }
                break;

            case TernaryExpr t:
                WriteExpr(t.IfTrue, myPrec + 1);
                _sb.Append(" if ");
                WriteExpr(t.Condition, myPrec + 1);
                _sb.Append(" else ");
                WriteExpr(t.IfFalse, myPrec);
                break;

            case CallExpr call:
                WriteExpr(call.Function, 16);
                _sb.Append('(');
                var first = true;
                foreach (var arg in call.Arguments)
                {
                    if (!first) _sb.Append(", ");
                    first = false;
                    WriteExpr(arg, 0);
                }
                if (call.KeywordSequence != null)
                {
                    foreach (var (kw, val) in call.KeywordSequence)
                    {
                        if (!first) _sb.Append(", ");
                        first = false;
                        if (kw == null)
                        {
                            _sb.Append("**");
                            WriteExpr(val, 1);
                        }
                        else
                        {
                            _sb.Append($"{kw}=");
                            WriteExpr(val, 1);
                        }
                    }
                }
                else
                {
                    foreach (var (kw, val) in call.KeywordArguments)
                    {
                        if (!first) _sb.Append(", ");
                        first = false;
                        _sb.Append($"{kw}=");
                        WriteExpr(val, 1);
                    }
                }
                _sb.Append(')');
                break;

            case SubscriptExpr sub:
                WriteExpr(sub.Target, 16);
                _sb.Append('[');
                if (sub.Index is TupleExpr subTuple)
                {
                    for (var i = 0; i < subTuple.Elements.Count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        WriteExpr(subTuple.Elements[i], 1);
                    }
                }
                else
                {
                    WriteExpr(sub.Index, 1);
                }
                _sb.Append(']');
                break;

            case AttributeExpr att:
                GuardIdentifier(att.Attribute);
                WriteExpr(att.Target, 16);
                _sb.Append($".{att.Attribute}");
                break;

            case SliceExpr sl:
                if (sl.Lower != null) WriteExpr(sl.Lower, 2);
                _sb.Append(':');
                if (sl.Upper != null) WriteExpr(sl.Upper, 2);
                if (sl.Step != null)
                {
                    _sb.Append(':');
                    WriteExpr(sl.Step, 2);
                }
                break;

            case ListExpr l:
                _sb.Append('[');
                for (var i = 0; i < l.Elements.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    WriteExpr(l.Elements[i], 1);
                }
                _sb.Append(']');
                break;

            case TupleExpr tup:
                _sb.Append('(');
                for (var i = 0; i < tup.Elements.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    WriteExpr(tup.Elements[i], 1);
                }
                if (tup.Elements.Count == 1) _sb.Append(',');
                _sb.Append(')');
                break;

            case SetExpr set:
                if (set.Elements.Count == 0)
                {
                    _sb.Append("set()");
                }
                else
                {
                    _sb.Append('{');
                    for (var i = 0; i < set.Elements.Count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        WriteExpr(set.Elements[i], 1);
                    }
                    _sb.Append('}');
                }
                break;

            case DictExpr d:
                _sb.Append('{');
                for (var i = 0; i < d.Entries.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    if (d.Entries[i].Key == null)
                    {
                        _sb.Append("**");
                        WriteExpr(d.Entries[i].Value, 2);
                    }
                    else
                    {
                        WriteExpr(d.Entries[i].Key!, 2);
                        _sb.Append(": ");
                        WriteExpr(d.Entries[i].Value, 2);
                    }
                }
                _sb.Append('}');
                break;

            case ListCompExpr lc:
                _sb.Append('[');
                WriteExpr(lc.Element, 1);
                WriteClauses(lc.Clauses);
                _sb.Append(']');
                break;

            case SetCompExpr sc:
                _sb.Append('{');
                WriteExpr(sc.Element, 1);
                WriteClauses(sc.Clauses);
                _sb.Append('}');
                break;

            case DictCompExpr dc:
                _sb.Append('{');
                WriteExpr(dc.Key, 2);
                _sb.Append(": ");
                WriteExpr(dc.Value, 2);
                WriteClauses(dc.Clauses);
                _sb.Append('}');
                break;

            case GeneratorExpr ge:
                _sb.Append('(');
                WriteExpr(ge.Element, 1);
                WriteClauses(ge.Clauses);
                _sb.Append(')');
                break;

            case NamedExpr ne:
                WriteExpr(ne.Target, 2);
                _sb.Append(" := ");
                WriteExpr(ne.Value, 1);
                break;

            case LambdaExpr lam:
                if (lam.Parameters.Count > 0)
                {
                    _sb.Append($"lambda {string.Join(", ", lam.Parameters)}: ");
                }
                else
                {
                    _sb.Append("lambda: ");
                }


                WriteExpr(lam.Body, 2);
                break;

            case YieldExpr y:
                _sb.Append("yield");
                if (y.Value != null)
                {
                    _sb.Append(' ');
                    WriteExpr(y.Value, 1);
                }
                break;

            case YieldFromExpr yf:
                _sb.Append("yield from ");
                WriteExpr(yf.Value, myPrec);
                break;

            case AwaitExpr a:
                _sb.Append("await ");
                WriteExpr(a.Value, myPrec);
                break;

            case FormattedValueExpr fv:
                _sb.Append("f\"");
                WriteFormattedValue(fv);
                _sb.Append('"');
                break;

            case StarredExpr st:
                _sb.Append('*');
                WriteExpr(st.Value, 1);
                break;

            case DoubleStarredExpr dst:
                _sb.Append("**");
                WriteExpr(dst.Value, 1);
                break;

            case JoinedStrExpr js:
                _sb.Append("f\"");
                foreach (var part in js.Parts)
                {
                    if (part is ConstantExpr { Value: string s })
                    {
                        _sb.Append(s.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r"));
                    }
                    else if (part is FormattedValueExpr fvPart)
                    {
                        WriteFormattedValue(fvPart);
                    }
                    else
                    {
                        _sb.Append('{');
                        WriteExpr(part, 2);
                        _sb.Append('}');
                    }
                }
                _sb.Append('"');
                break;
        }

        if (needParens) _sb.Append(')');
    }

    private void WriteConstant(object? val)
    {
        switch (val)
        {
            case null:
                _sb.Append("None");
                break;
            case PyEllipsis:
                _sb.Append("...");
                break;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Object && je.TryGetProperty("__type__", out var tp) && tp.GetString() == "ellipsis":
                _sb.Append("...");
                break;
            case bool b:
                _sb.Append(b ? "True" : "False");
                break;
            case string s:
                _sb.Append(FormatStringLiteral(s));
                break;
            case byte[] bytes:
                _sb.Append(FormatBytes(bytes));
                break;
            case double d:
                _sb.Append(FormatDouble(d));
                break;
            case float f:
                _sb.Append(FormatDouble(f));
                break;
            case long or int or short or byte or System.Numerics.BigInteger:
                _sb.Append(val.ToString());
                break;
            case System.Numerics.Complex cx:
                _sb.Append(FormatComplex(cx));
                break;
            case SliceValue sl:
                _sb.Append("slice(");
                WriteConstant(sl.Start);
                _sb.Append(", ");
                WriteConstant(sl.Stop);
                _sb.Append(", ");
                WriteConstant(sl.Step);
                _sb.Append(')');
                break;
            case IList<object?> list:
                _sb.Append('(');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    WriteConstant(list[i]);
                }
                if (list.Count == 1) _sb.Append(',');
                _sb.Append(')');
                break;
            case HashSet<object?> set:


                _sb.Append('{');
                var firstSetItem = true;
                foreach (var item in set)
                {
                    if (!firstSetItem) _sb.Append(", ");
                    firstSetItem = false;
                    WriteConstant(item);
                }
                _sb.Append('}');
                break;
            case FrozenSetValue frozenSet:
                _sb.Append('{');
                var firstFrozenItem = true;
                foreach (var item in frozenSet.Items)
                {
                    if (!firstFrozenItem) _sb.Append(", ");
                    firstFrozenItem = false;
                    WriteConstant(item);
                }
                _sb.Append('}');
                break;
            default:
                _sb.Append(val.ToString());
                break;
        }
    }


    private static string FormatComplex(System.Numerics.Complex c)
    {
        if (c.Imaginary == 0.0)
        {
            return $"({FormatDouble(c.Real)}+0j)";
        }

        var real = FormatDouble(c.Real);
        var imag = FormatDouble(c.Imaginary);
        if (c.Real == 0.0 && !double.IsNegative(c.Real))
        {
            return $"{imag}j";
        }

        var sign = c.Imaginary >= 0 || double.IsNegative(c.Imaginary) == false ? "+" : "";
        return $"({real}{sign}{imag}j)";
    }


    private static string FormatDouble(double d)
    {
        if (double.IsNaN(d)) return "float(\"nan\")";
        if (double.IsPositiveInfinity(d)) return "1e999";
        if (double.IsNegativeInfinity(d)) return "-1e999";
        if (d == 0.0) return double.IsNegative(d) ? "-0.0" : "0.0";

        var s = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (s.IndexOf('.') < 0 && s.IndexOf('E') < 0 && s.IndexOf('e') < 0)
        {
            s += ".0";
        }
        return s;
    }

    private static string FormatBytes(byte[] bytes)
    {
        var sb = new StringBuilder("b'");
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\'': sb.Append("\\'"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                default:
                    if (b >= 0x20 && b < 0x7f) sb.Append((char)b);
                    else sb.Append($"\\x{b:x2}");
                    break;
            }
        }
        sb.Append('\'');
        return sb.ToString();
    }

    private static string FormatStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch == 0x7f)
                        sb.Append($"\\x{(int)ch:x2}");
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static int GetPrecedence(ExprNode expr) => expr switch
    {
        LambdaExpr => 1,
        YieldExpr or YieldFromExpr or AwaitExpr => 1,
        TernaryExpr => 2,
        BoolOpExpr b => b.Op == "or" ? 3 : 4,


        NamedExpr => 0,
        UnaryExpr u when u.Op == "not" => 5,
        CompareExpr => 6,
        BinaryExpr b when b.Op == "|" => 7,
        BinaryExpr b when b.Op == "^" => 8,
        BinaryExpr b when b.Op == "&" => 9,
        BinaryExpr b when b.Op is "<<" or ">>" => 10,
        BinaryExpr b when b.Op is "+" or "-" => 11,
        BinaryExpr b when b.Op is "*" or "/" or "//" or "%" or "@" => 12,
        UnaryExpr => 13,
        BinaryExpr b when b.Op == "**" => 14,
        CallExpr or SubscriptExpr or AttributeExpr => 16,
        ConstantExpr or NameExpr or AssertionErrorExpr or ListExpr or TupleExpr or DictExpr or SetExpr or ListCompExpr or SetCompExpr or DictCompExpr or GeneratorExpr => 17,


        SliceExpr or StarredExpr or DoubleStarredExpr or FormattedValueExpr or JoinedStrExpr => 17,
        _ => 0
    };

    private void WriteClauses(IReadOnlyList<ComprehensionClause> clauses)
    {


        foreach (var clause in clauses)
        {
            _sb.Append(clause.IsAsync ? " async for " : " for ");
            WriteExpr(clause.Target, 0);
            _sb.Append(" in ");
            WriteExpr(clause.Iterator, 1);
            for (var i = 0; i < clause.Filters.Count; i++)
            {
                var threaded = clause.FilterThreaded == null || i >= clause.FilterThreaded.Count || clause.FilterThreaded[i];
                if (!threaded)
                {
                    _sb.Append('\n');
                    _sb.Append(new string(' ', (_indent + 1) * 4));
                }
                _sb.Append(" if ");
                WriteExpr(clause.Filters[i], 1);
            }
        }
    }

    private void WriteFormattedValue(FormattedValueExpr fv)
    {
        _sb.Append('{');


        WriteExpr(fv.Value, 2);
        if (!string.IsNullOrEmpty(fv.Conversion))
        {
            _sb.Append('!');
            _sb.Append(fv.Conversion);
        }
        if (fv.FormatSpec != null)
        {
            _sb.Append(':');
            WriteFormatSpec(fv.FormatSpec);
        }
        _sb.Append('}');
    }

    private void WriteFormatSpec(ExprNode spec)
    {
        if (spec is ConstantExpr { Value: string s })
        {
            _sb.Append(s);
        }
        else if (spec is JoinedStrExpr js)
        {
            foreach (var part in js.Parts)
            {
                if (part is ConstantExpr { Value: string ps })
                {
                    _sb.Append(ps);
                }
                else if (part is FormattedValueExpr fvPart)
                {
                    WriteFormattedValue(fvPart);
                }
                else
                {
                    _sb.Append('{');
                    WriteExpr(part, 0);
                    _sb.Append('}');
                }
            }
        }
        else
        {
            WriteExpr(spec, 2);
        }
    }

    private void WriteIndent() => _sb.Append(new string(' ', _indent * 4));
}
