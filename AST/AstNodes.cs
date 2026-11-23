namespace PycRune.AST;

public sealed class PyEllipsis
{
    public static readonly PyEllipsis Instance = new();
    private PyEllipsis() { }
    public override string ToString() => "...";
}

public abstract record AstNode;

public abstract record ExprNode : AstNode;

public sealed record ConstantExpr(object? Value) : ExprNode;

public sealed record NameExpr(string Name) : ExprNode;

public sealed record AssertionErrorExpr : ExprNode;

public sealed record UnaryExpr(string Op, ExprNode Operand) : ExprNode;

public sealed record BinaryExpr(ExprNode Left, string Op, ExprNode Right) : ExprNode;

public sealed record CompareExpr(ExprNode Left, IReadOnlyList<(string Op, ExprNode Right)> Comparators) : ExprNode;

public sealed record BoolOpExpr(string Op, IReadOnlyList<ExprNode> Values) : ExprNode;

public sealed record TernaryExpr(ExprNode Condition, ExprNode IfTrue, ExprNode IfFalse) : ExprNode;

public sealed record CallExpr(
    ExprNode Function,
    IReadOnlyList<ExprNode> Arguments,
    IReadOnlyList<(string Name, ExprNode Value)> KeywordArguments,


    IReadOnlyList<(string? Name, ExprNode Value)>? KeywordSequence = null
) : ExprNode;

public sealed record SubscriptExpr(ExprNode Target, ExprNode Index) : ExprNode;

public sealed record AttributeExpr(ExprNode Target, string Attribute) : ExprNode;

public sealed record SliceExpr(ExprNode? Lower, ExprNode? Upper, ExprNode? Step) : ExprNode;

public sealed record ListExpr(IReadOnlyList<ExprNode> Elements) : ExprNode;

public sealed record TupleExpr(IReadOnlyList<ExprNode> Elements) : ExprNode;

public sealed record SetExpr(IReadOnlyList<ExprNode> Elements) : ExprNode;

public sealed record DictExpr(IReadOnlyList<(ExprNode? Key, ExprNode Value)> Entries) : ExprNode;

public sealed record ComprehensionClause(ExprNode Target, ExprNode Iterator, IReadOnlyList<ExprNode> Filters, bool IsAsync = false, IReadOnlyList<bool>? FilterThreaded = null);

public sealed record ListCompExpr(ExprNode Element, IReadOnlyList<ComprehensionClause> Clauses) : ExprNode;

public sealed record SetCompExpr(ExprNode Element, IReadOnlyList<ComprehensionClause> Clauses) : ExprNode;

public sealed record DictCompExpr(ExprNode Key, ExprNode Value, IReadOnlyList<ComprehensionClause> Clauses) : ExprNode;

public sealed record GeneratorExpr(ExprNode Element, IReadOnlyList<ComprehensionClause> Clauses) : ExprNode;

public sealed record NamedExpr(ExprNode Target, ExprNode Value) : ExprNode;

public sealed record LambdaExpr(IReadOnlyList<string> Parameters, ExprNode Body) : ExprNode;

public sealed record YieldExpr(ExprNode? Value) : ExprNode;

public sealed record YieldFromExpr(ExprNode Value) : ExprNode;

public sealed record AwaitExpr(ExprNode Value) : ExprNode;

public sealed record FormattedValueExpr(ExprNode Value, string? Conversion = null, ExprNode? FormatSpec = null) : ExprNode;

public sealed record JoinedStrExpr(IReadOnlyList<ExprNode> Parts) : ExprNode;

public sealed record StarredExpr(ExprNode Value) : ExprNode;

public sealed record DoubleStarredExpr(ExprNode Value) : ExprNode;

public sealed record FunctionExpr(
    string Name,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<StmtNode> Body,
    List<ExprNode>? Decorators = null,
    bool IsAsync = false,
    IReadOnlyList<string>? TypeParams = null,
    string? ReturnType = null
) : ExprNode;

public sealed record ClassExpr(
    string Name,
    IReadOnlyList<ExprNode> Bases,
    IReadOnlyList<(string Name, ExprNode Value)> Keywords,
    IReadOnlyList<StmtNode> Body,
    List<ExprNode>? Decorators = null,
    IReadOnlyList<string>? TypeParams = null
) : ExprNode;

public sealed record TypeAliasExpr(
    string Name,
    IReadOnlyList<string> TypeParams,
    ExprNode Value
) : ExprNode;

public sealed record GenericBaseExpr(IReadOnlyList<string> TypeParams) : ExprNode;

public abstract record StmtNode : AstNode;

public sealed record ExprStmt(ExprNode Expression) : StmtNode;

public sealed record AssignStmt(IReadOnlyList<ExprNode> Targets, ExprNode Value) : StmtNode;

public sealed record AugAssignStmt(ExprNode Target, string Op, ExprNode Value) : StmtNode;

public sealed record DeleteStmt(IReadOnlyList<ExprNode> Targets) : StmtNode;

public sealed record IfStmt(
    ExprNode Condition,
    IReadOnlyList<StmtNode> ThenBody,
    IReadOnlyList<StmtNode> ElseBody
) : StmtNode;

public sealed record WhileStmt(
    ExprNode Condition,
    IReadOnlyList<StmtNode> Body,
    IReadOnlyList<StmtNode> ElseBody
) : StmtNode;

public sealed record ForStmt(
    ExprNode Target,
    ExprNode Iterator,
    IReadOnlyList<StmtNode> Body,
    IReadOnlyList<StmtNode> ElseBody,
    bool IsAsync = false
) : StmtNode;

public sealed record ExceptClause(
    ExprNode? ExceptionType,
    string? VariableName,
    IReadOnlyList<StmtNode> Body,
    bool IsStar = false
);

public sealed record TryStmt(
    IReadOnlyList<StmtNode> Body,
    IReadOnlyList<ExceptClause> Handlers,
    IReadOnlyList<StmtNode> ElseBody,
    IReadOnlyList<StmtNode>? FinallyBody = null
) : StmtNode;

public sealed record RaiseStmt(ExprNode? Exception, ExprNode? From) : StmtNode;

public sealed record ReturnStmt(ExprNode? Value) : StmtNode
{
        public bool FromReturnConst { get; init; }
}

public sealed record BreakStmt : StmtNode;

public sealed record ContinueStmt : StmtNode;

public sealed record PassStmt : StmtNode;

public sealed record AssertStmt(ExprNode Test, ExprNode? Message) : StmtNode;

public sealed record NonlocalStmt(IReadOnlyList<string> Names) : StmtNode;

public sealed record GlobalStmt(IReadOnlyList<string> Names) : StmtNode;

public sealed record FunctionDefStmt(
    string Name,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<StmtNode> Body,
    IReadOnlyList<ExprNode>? Decorators = null,
    bool IsAsync = false,
    IReadOnlyList<string>? TypeParams = null,
    string? ReturnType = null
) : StmtNode;

public sealed record ClassDefStmt(
    string Name,
    IReadOnlyList<ExprNode> Bases,
    IReadOnlyList<(string Name, ExprNode Value)> Keywords,
    IReadOnlyList<StmtNode> Body,
    IReadOnlyList<ExprNode>? Decorators = null,
    IReadOnlyList<string>? TypeParams = null
) : StmtNode;

public sealed record TypeAliasStmt(
    string Name,
    IReadOnlyList<string> TypeParams,
    ExprNode Value
) : StmtNode;

public sealed record ImportAlias(string Name, string? AsName);

public sealed record ImportStmt(IReadOnlyList<ImportAlias> Names) : StmtNode;

public sealed record ImportFromStmt(string? Module, IReadOnlyList<ImportAlias> Names, int Level) : StmtNode;

public sealed record WithItem(ExprNode ContextExpr, ExprNode? OptionalVars);

public sealed record WithStmt(
    IReadOnlyList<WithItem> Items,
    IReadOnlyList<StmtNode> Body,
    bool IsAsync = false
) : StmtNode;

public abstract record PatternNode : AstNode;

public sealed record MatchValuePattern(ExprNode Value) : PatternNode;

public sealed record MatchOrPattern(IReadOnlyList<PatternNode> Patterns) : PatternNode;

public sealed record MatchSequencePattern(IReadOnlyList<PatternNode> Patterns, bool IsList) : PatternNode;

public sealed record MatchClassPattern(ExprNode ClassName, IReadOnlyList<PatternNode> Patterns) : PatternNode;

public sealed record MatchKeywordPattern(string Attribute, PatternNode Pattern) : PatternNode;

public sealed record MatchMappingPattern(IReadOnlyList<(ExprNode Key, PatternNode Pattern)> Entries, PatternNode? Rest) : PatternNode;

public sealed record MatchStarPattern(string Name) : PatternNode;

public sealed record MatchAsPattern(PatternNode? Pattern, string Name) : PatternNode;

public sealed record MatchWildcardPattern : PatternNode;

public sealed record MatchCase(
    PatternNode Pattern,
    ExprNode? Guard,
    IReadOnlyList<StmtNode> Body
);

public sealed record MatchStmt(
    ExprNode Subject,
    IReadOnlyList<MatchCase> Cases
) : StmtNode;
