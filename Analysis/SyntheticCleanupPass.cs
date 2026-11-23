namespace PycRune.Analysis;

using System.Diagnostics.CodeAnalysis;
using PycRune.AST;

public static class SyntheticCleanupPass
{
    public static IReadOnlyList<StmtNode> CleanStatements(IReadOnlyList<StmtNode> statements)
    {
        var cleaned = CleanExceptionVariable(statements);
        var result = new List<StmtNode>(cleaned.Count);

        foreach (var stmt in cleaned)
        {
            result.Add(CleanStatement(stmt));
        }

        return result;
    }

    public static StmtNode CleanStatement(StmtNode statement) => statement switch
    {
        TryStmt tryStmt => CleanTry(tryStmt),
        WithStmt withStmt => CleanWith(withStmt),
        IfStmt ifStmt => new IfStmt(
            ifStmt.Condition,
            CleanStatements(ifStmt.ThenBody),
            CleanStatements(ifStmt.ElseBody)),
        WhileStmt whileStmt => new WhileStmt(
            whileStmt.Condition,
            CleanStatements(whileStmt.Body),
            CleanStatements(whileStmt.ElseBody)),
        ForStmt forStmt => new ForStmt(
            forStmt.Target,
            forStmt.Iterator,
            CleanStatements(forStmt.Body),
            CleanStatements(forStmt.ElseBody),
            forStmt.IsAsync),
        FunctionDefStmt fn => new FunctionDefStmt(
            fn.Name,
            fn.Parameters,
            CleanStatements(fn.Body),
            fn.Decorators,
            fn.IsAsync,
            fn.TypeParams,
            fn.ReturnType),
        ClassDefStmt cls => new ClassDefStmt(
            cls.Name,
            cls.Bases,
            cls.Keywords,
            CleanClassBody(cls.Body),
            cls.Decorators,
            cls.TypeParams),
        _ => statement
    };

    public static IReadOnlyList<StmtNode> CleanExceptionVariable(IReadOnlyList<StmtNode> statements, string? variableName = null)
    {
        var result = new List<StmtNode>();
        var count = statements.Count;

        for (var i = 0; i < count; i++)
        {
            var stmt = statements[i];

            if (!string.IsNullOrEmpty(variableName))
            {
                if (IsExceptionVariableCleanup(stmt, variableName))
                {
                    continue;
                }
            }
            else
            {
                if (i + 1 < count && IsExceptionVariableAssign(stmt, out var assignName) && IsExceptionVariableDelete(statements[i + 1], assignName))
                {
                    i++;
                    continue;
                }
            }

            result.Add(stmt);
        }

        return result;
    }

    public static IReadOnlyList<StmtNode> CleanContextManagerExit(IReadOnlyList<StmtNode> statements)
    {
        var list = new List<StmtNode>(statements);
        while (list.Count > 0 && IsSyntheticContextManagerExit(list[^1]))
        {
            list.RemoveAt(list.Count - 1);
        }
        return list;
    }

    public static bool IsSyntheticExceptionCleanup(StmtNode statement, string? variableName = null)
    {
        if (statement is AssignStmt assign)
        {
            if (assign.Value is ConstantExpr { Value: null } && assign.Targets.Count > 0)
            {
                if (string.IsNullOrEmpty(variableName))
                {
                    return assign.Targets.All(t => t is NameExpr);
                }
                return assign.Targets.Any(t => t is NameExpr n && n.Name == variableName);
            }
        }
        else if (statement is DeleteStmt del)
        {
            if (del.Targets.Count > 0)
            {
                if (string.IsNullOrEmpty(variableName))
                {
                    return del.Targets.All(t => t is NameExpr);
                }
                return del.Targets.Any(t => t is NameExpr n && n.Name == variableName);
            }
        }
        return false;
    }

    public static bool IsSyntheticContextManagerExit(StmtNode statement)
    {
        if (statement is ExprStmt exprStmt && exprStmt.Expression is CallExpr call)
        {
            if (call.Function is AttributeExpr attr && attr.Attribute is "__exit__" or "__aexit__")
            {
                return call.Arguments.Count == 0 || call.Arguments.All(a => a is ConstantExpr { Value: null });
            }
            if (call.Function is NameExpr name && name.Name is "__exit__" or "__aexit__")
            {
                return call.Arguments.Count == 0 || call.Arguments.All(a => a is ConstantExpr { Value: null });
            }
        }
        return false;
    }

    private static TryStmt CleanTry(TryStmt tryStmt)
    {
        var cleanBody = CleanStatements(tryStmt.Body);
        var cleanHandlers = new List<ExceptClause>();

        foreach (var handler in tryStmt.Handlers)
        {
            var cleanedHandlerBody = CleanStatements(handler.Body);
            if (!string.IsNullOrEmpty(handler.VariableName))
            {
                cleanedHandlerBody = CleanExceptionVariable(cleanedHandlerBody, handler.VariableName);
            }
            cleanHandlers.Add(new ExceptClause(
                handler.ExceptionType,
                handler.VariableName,
                cleanedHandlerBody,
                handler.IsStar));
        }

        var cleanElse = CleanStatements(tryStmt.ElseBody);
        var cleanFinally = tryStmt.FinallyBody != null ? CleanStatements(tryStmt.FinallyBody) : null;

        return new TryStmt(cleanBody, cleanHandlers, cleanElse, cleanFinally);
    }

    private static WithStmt CleanWith(WithStmt withStmt)
    {
        var cleanBody = CleanStatements(withStmt.Body);
        cleanBody = CleanContextManagerExit(cleanBody);

        return new WithStmt(withStmt.Items, cleanBody, withStmt.IsAsync);
    }

    public static IReadOnlyList<StmtNode> CleanClassBody(IReadOnlyList<StmtNode> statements)
    {
        var cleaned = CleanStatements(statements);
        var result = new List<StmtNode>(cleaned.Count);
        foreach (var stmt in cleaned)
        {
            if (IsSyntheticClassStatement(stmt)) continue;
            result.Add(stmt);
        }
        return result;
    }

    private static bool IsSyntheticClassStatement(StmtNode stmt)
    {
        if (stmt is AssignStmt assign)
        {
            if (assign.Targets.Count == 1 && assign.Targets[0] is NameExpr targetName)
            {
                if (targetName.Name is "__classdict__" or "__classdictcell__" or "__classcell__" or "__type_params__")
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsExceptionVariableCleanup(StmtNode stmt, string variableName)
    {
        if (stmt is AssignStmt assign)
        {
            return assign.Value is ConstantExpr { Value: null } &&
                   assign.Targets.Any(t => t is NameExpr n && n.Name == variableName);
        }
        if (stmt is DeleteStmt del)
        {
            return del.Targets.Any(t => t is NameExpr n && n.Name == variableName);
        }
        return false;
    }

    private static bool IsExceptionVariableAssign(StmtNode stmt, [NotNullWhen(true)] out string? varName)
    {
        if (stmt is AssignStmt assign && assign.Value is ConstantExpr { Value: null } && assign.Targets.Count == 1 && assign.Targets[0] is NameExpr n)
        {
            varName = n.Name;
            return true;
        }
        varName = null;
        return false;
    }

    private static bool IsExceptionVariableDelete(StmtNode stmt, string varName)
    {
        if (stmt is DeleteStmt del && del.Targets.Count == 1 && del.Targets[0] is NameExpr n && n.Name == varName)
        {
            return true;
        }
        return false;
    }
}
