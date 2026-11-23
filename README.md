# PycRune

**Exact Python bytecode decompiler for CPython 3.8 – 3.15**

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4">
  <img alt="CPython 3.8-3.15" src="https://img.shields.io/badge/CPython-3.8%20%E2%80%93%203.15-3776AB">
  <img alt="Platforms" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey">
</p>

<p align="center">
  <a href="README.md"><b>English</b></a> ·
  <a href="README.zh.md">中文</a> ·
  <a href="README.vi.md">Tiếng Việt</a>
</p>

PycRune reads a `.pyc` file, reconstructs Python source, and proves the result by recompiling that source with the matching CPython interpreter and comparing the resulting code object recursively.

The acceptance rule is bytecode identity, not visual similarity.

---

## Quick Start

```bash
dotnet build pycrune.csproj -c Release
./bin/Release/net10.0/pycrune decompile input.pyc -o output.py --verify
```

Windows:

```powershell
.\bin\Release\net10.0\pycrune.exe decompile input.pyc -o output.py --verify
```

Requirements: .NET 10 SDK and the matching CPython interpreter. See [Setup on Every Platform](#10-setup-on-every-platform).

## Table of Contents

1. [Overview](#1-overview)
2. [Design Goals](#2-design-goals)
3. [Supported Versions](#3-supported-versions)
4. [Feature Matrix](#4-feature-matrix)
5. [How It Works](#5-how-it-works)
6. [Core Data Model](#6-core-data-model)
7. [Mathematical Foundations](#7-mathematical-foundations)
8. [Architecture](#8-architecture)
9. [Requirements](#9-requirements)
10. [Setup on Every Platform](#10-setup-on-every-platform)
11. [Build from Source](#11-build-from-source)
12. [CLI Reference](#12-cli-reference)
13. [Verification Guide](#13-verification-guide)
14. [Interpreter Resolution](#14-interpreter-resolution)
15. [Performance](#15-performance)
16. [Troubleshooting](#16-troubleshooting)
17. [FAQ](#17-faq)
18. [Appendix A — With / Async-With Protocols](#18-appendix-a--with--async-with-protocols)
19. [Appendix B — Exception Table](#19-appendix-b--exception-table)
20. [Appendix C — Magic Numbers](#20-appendix-c--magic-numbers)
21. [Appendix D — Glossary](#21-appendix-d--glossary)

---

## 1. Overview

PycRune is a C#/.NET 10 decompiler for Python bytecode. It supports CPython 3.8 through 3.15 and focuses on one property: the decompiled source must compile back to a code object that is identical to the original in every field that CPython exposes.

That property is stronger than "the source looks right". Two source files can look different and compile to the same bytecode; two source files can look similar and compile to different bytecode. PycRune accepts only the first case.

The project is intentionally small at the surface:

```text
.pyc  ->  PycRune  ->  .py  ->  CPython  ->  code object  ->  compare
```

The hard part is inside the arrow from `.pyc` to `.py`: CPython changes its bytecode layout in every minor release. The decompiler therefore does not rely on one fixed pattern per statement. It reads the exception table, simulates the value stack, identifies protected ranges, and reconstructs the control-flow structure that the compiler actually emitted.

### What PycRune is

- A decompiler for CPython bytecode.
- A bytecode comparator.
- A behavioral verifier.
- A CLI tool for single files, directories, and CFG inspection.

### What PycRune is not

- It is not a Python interpreter.
- It does not execute untrusted bytecode during decompilation.
- It does not promise that two different source files are textually identical.
- It does not use a machine-learning model to guess missing code.

### Why bytecode identity

Python source is not stored in a `.pyc` file. Names of local variables, exact formatting, comments, and some syntactic choices are lost. A decompiler must reconstruct a source that is behaviorally and structurally equivalent. Bytecode identity is the strongest mechanical check available without the original source.

The identity check covers:

- Instruction sequence after transparent-op normalization.
- Opcodes and operands.
- Constant order, types, and values.
- Names, varnames, freevars, cellvars.
- Exception table entries.
- Nested code objects, recursively.

---

## 2. Design Goals

### 2.1 Exactness

Every accepted output must satisfy the identity relation in [§7.1](#71-bytecode-identity). A result that is "close" is a failure.

### 2.2 Structural reasoning

The decompiler derives decisions from:

- The exception table.
- The symbolic stack.
- The control-flow graph.
- The actual instruction stream.

It does not key on variable names, constant values, source line numbers, or fixed byte offsets.

### 2.3 Cross-version stability

A change made for CPython 3.14 must not break 3.8–3.13. Shared code paths are validated across all supported versions.

### 2.4 Fail fast

When the instruction stream contains an opcode or a shape that the current dialect cannot represent, PycRune raises an explicit error. It does not silently emit a placeholder or a wrong statement.

### 2.5 No hidden test harness

The clean distribution contains the decompiler, the comparator, and the verifier. It does not contain the development corpus, agent runner, baselines, or reports.

---

## 3. Supported Versions

| CPython | Magic | Header | Status | Notes |
|---|---:|---:|---|---|
| 3.8 | 3413 | 16 | Supported | Legacy block stack |
| 3.9 | 3425 | 16 | Supported | Legacy block stack |
| 3.10 | 3439 | 16 | Supported | Legacy block stack, pattern matching |
| 3.11 | 3495 | 16 | Supported | Exception table, `BEFORE_WITH` |
| 3.12 | 3531 | 16 | Supported | Exception table refinements |
| 3.13 | 3571 | 16 | Supported | Fused locals, exception table |
| 3.14 | 3627 | 16 | Supported | `LOAD_SPECIAL` with protocol |
| 3.15 | 3666 | 16 | Supported | `LOAD_SPECIAL`, common constants |

The header size is the number of bytes before the marshal payload. All listed versions use a 16-byte header. PycRune also contains a bounded scan fallback for stripped or patched headers.

### 3.1 Python language features covered

- Expressions, operators, comparisons, boolean short-circuiting.
- Literals, f-strings, formatted values, string concatenation.
- Lists, tuples, sets, dictionaries, comprehensions.
- Slices, extended slices, unpacking, starred targets.
- Assignments, augmented assignments, chained assignments.
- `if` / `elif` / `else`, `while`, `for`, `else` clauses.
- `break`, `continue`, `return`, `yield`, `yield from`.
- Functions, lambdas, defaults, keyword-only and positional-only parameters.
- Classes, metaclasses, decorators, descriptors.
- Closures, cell variables, `nonlocal`, `global`.
- `try` / `except` / `else` / `finally`.
- `with`, `async with`, multi-item with.
- `async for`, async generators, coroutines.
- `match` / `case`, class patterns, mapping patterns, OR patterns, guards.
- Exception groups and `except*`.
- PEP 695 type parameters and type aliases.
- PEP 701 f-string nesting.

---

## 4. Feature Matrix

| Capability | CLI | Library | Notes |
|---|---|---|---|
| Decompile a `.pyc` | `decompile` | `StructuralDecompiler` | Emits Python source |
| Decompile a `.py` | `decompile` | `PyBridge` | Compiles first, then decompiles |
| Behavior verification | `--verify` | `ExecutionVerifier` | Runs both programs |
| Bytecode comparison | `compare` | `BytecodeComparer` | Recursive code-object diff |
| Batch decompilation | `batch` | loop over files | Directory input |
| CFG inspection | `inspect` | `ControlFlowGraph` | Blocks and exception scopes |
| Version detection | automatic | `PythonLocator` | Magic number |
| Multi-version support | automatic | per-version dialect | 3.8–3.15 |
| Persistent bridge | automatic | `PyBridge` | One server per worker |
| Cross-platform | partial | core is portable | Unix needs a `py` shim |

---

## 5. How It Works

### 5.1 Pipeline

```text
+-----------+      +----------------+      +-------------------+
|  .pyc     | ---> | PythonLocator  | ---> |   PyBridge        |
|  header   |      | magic -> 3.x   |      |   Python process  |
+-----------+      +----------------+      +-------------------+
                                                    |
                                                    v
                                           +-------------------+
                                           | PythonCodeObject  |
                                           | instructions      |
                                           | consts, names     |
                                           | exception table   |
                                           +-------------------+
                                                    |
                                                    v
                                           +-------------------+
                                           |StructuralDecompiler|
                                           | stack simulation  |
                                           | scope building    |
                                           | AST construction  |
                                           +-------------------+
                                                    |
                                                    v
                                           +-------------------+
                                           |   PythonEmitter   |
                                           |   Python source   |
                                           +-------------------+
                                                    |
                              +---------------------+---------------------+
                              |                                           |
                              v                                           v
                    +-------------------+                     +-------------------+
                    | ExecutionVerifier |                     | BytecodeComparer  |
                    | run both programs |                     | recursive compare |
                    +-------------------+                     +-------------------+
```

### 5.2 Loading

`PythonLocator` reads the first four bytes of the file. For a `.pyc`, bytes 0–1 are the magic number. The magic maps to a CPython version. The version determines which interpreter to launch and which instruction dialect to use.

If the magic is unknown, PycRune falls back to the host `python` interpreter. If the file has a stripped header or a patched magic, the bridge performs a bounded scan for a marshal code object.

### 5.3 Bridge

The bridge is a small Python server. It receives JSON requests over stdin and writes JSON responses over stdout. For a `.pyc` request, it:

1. Reads the file.
2. Finds the marshal payload.
3. Unmarshals the code object.
4. Serializes the code object into a JSON model.

For a source request, it:

1. Reads the source from stdin.
2. Compiles it with the matching interpreter.
3. Serializes the resulting code object.

The server stays alive between requests. This removes Python startup cost from the hot path.

### 5.4 Decompilation

`StructuralDecompiler` is the core. It walks the instruction stream with a symbolic stack and a set of structural parsers:

- Expression parser.
- Statement parser.
- Try-scope parser.
- With parser for each dialect.
- Async-with parser.
- Async-for parser.
- Match parser.
- Comprehension parser.
- Legacy block-stack parser for 3.8–3.10.

The parser returns AST nodes. `PythonEmitter` turns those nodes into source text.

### 5.5 Verification

`ExecutionVerifier` runs the original file and the decompiled source in separate processes, then compares exit code and stdout.

`BytecodeComparer` compiles the decompiled source and compares the resulting code object with the original.

Both checks are independent. A file can pass behavior and fail bytecode, or pass bytecode and fail behavior.

---

## 6. Core Data Model

### 6.1 PythonCodeObject

```csharp
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
    public required IReadOnlyList<PythonInstruction> Instructions { get; init; }
    public required IReadOnlyList<object?> Constants { get; init; }
    public required IReadOnlyList<string> Names { get; init; }
    public required IReadOnlyList<string> VarNames { get; init; }
    public required IReadOnlyList<string> FreeVars { get; init; }
    public required IReadOnlyList<string> CellVars { get; init; }
    public required IReadOnlyList<ExceptionTableEntry> ExceptionTable { get; init; }
}
```

### 6.2 PythonInstruction

```csharp
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
);
```

### 6.3 ExceptionTableEntry

```csharp
public sealed record ExceptionTableEntry(
    int Start,
    int End,
    int Target,
    int Depth,
    bool LastI
);
```

### 6.4 AST

The AST is a closed set of records under `AST/AstNodes.cs`:

- Expressions: `NameExpr`, `ConstantExpr`, `CallExpr`, `AttributeExpr`, `SubscriptExpr`, `BinaryExpr`, `UnaryExpr`, `CompareExpr`, `LambdaExpr`, `ListExpr`, `TupleExpr`, `DictExpr`, `SetExpr`, `ComprehensionExpr`, `YieldExpr`, `AwaitExpr`, `StarredExpr`, `SliceExpr`, `FStringExpr`, `NamedExpr`.
- Statements: `ExprStmt`, `AssignStmt`, `AugAssignStmt`, `AnnAssignStmt`, `ReturnStmt`, `RaiseStmt`, `AssertStmt`, `PassStmt`, `BreakStmt`, `ContinueStmt`, `IfStmt`, `WhileStmt`, `ForStmt`, `WithStmt`, `TryStmt`, `FunctionDefStmt`, `ClassDefStmt`, `ImportStmt`, `ImportFromStmt`, `GlobalStmt`, `NonlocalStmt`, `DeleteStmt`, `MatchStmt`, `TypeAliasStmt`.
- Helpers: `WithItem`, `ExceptHandler`, `MatchCase`, `Pattern`.

---

## 7. Mathematical Foundations

This section states the rules that the implementation follows. The notation is deliberately compact; each rule is followed by an explanation.

### 7.1 Bytecode identity

Let:

- `P` be a `.pyc` file.
- `S` be a source file.
- `Code(P)` be the code object stored in `P`.
- `Compile(S)` be the code object produced by compiling `S` with the same CPython version.
- `Eq(a, b)` be the recursive code-object equality relation.

Then the acceptance predicate is:

```text
Identical(P, S) ⇔ Eq(Code(P), Compile(S))
```

The recursive relation is:

```text
Eq(a,b) ⇔
  Norm(a.code) = Norm(b.code)
  ∧ a.consts ≡ b.consts
  ∧ a.names = b.names
  ∧ a.varnames = b.varnames
  ∧ a.freevars = b.freevars
  ∧ a.cellvars = b.cellvars
  ∧ a.exceptiontable ≡ b.exceptiontable
  ∧ ∀ c_a ∈ CodeConsts(a), c_b ∈ CodeConsts(b): Eq(c_a, c_b)
```

`CodeConsts` selects only the constants that are themselves code objects. The comparison is pairwise and order-sensitive.

### 7.2 Instruction normalization

CPython emits instructions that do not change semantics:

- `NOP`
- `EXTENDED_ARG`
- `NOT_TAKEN`

Define:

```text
Norm(I) = ⟨ i ∈ I | i.opcode ∉ {NOP, EXTENDED_ARG, NOT_TAKEN} ⟩
```

The comparison ignores these instructions but preserves the order of all others.

### 7.3 Magic-number mapping

Let `m` be the little-endian 16-bit value of bytes 0–1 of a `.pyc`. Define:

```text
V(m) =
  3.8 if m = 3413
  3.9 if m = 3425
  3.10 if m = 3439
  3.11 if m = 3495
  3.12 if m = 3531
  3.13 if m = 3571
  3.14 if m = 3627
  3.15 if m = 3666
  unknown    otherwise
```

For a known magic, the header size is 16 bytes. For unknown magic, the bridge scans a bounded window for a valid marshal code object.

### 7.4 Stack effect

Let `S` be the symbolic stack depth before an instruction. Let `pop(op)` and `push(op)` be the number of values the instruction consumes and produces. Then:

```text
S' = S - pop(op) + push(op)
```

A negative intermediate depth is a hard error. The decompiler never continues after an underflow.

Examples:

| Instruction | pop | push |
|---|---:|---:|
| `LOAD_CONST` | 0 | 1 |
| `LOAD_FAST` | 0 | 1 |
| `STORE_FAST` | 1 | 0 |
| `BINARY_OP` | 2 | 1 |
| `CALL n` | n + 1 | 1 |
| `RETURN_VALUE` | 1 | 0 |
| `POP_TOP` | 1 | 0 |
| `DUP_TOP` | 1 | 2 |
| `SWAP n` | n | n |

For `CALL`, the callable is counted separately from the arguments. Some versions also push a `NULL` sentinel; the bridge preserves it as a distinct stack value.

### 7.5 Exception table coverage

For an exception-table entry `e = (start, end, target, depth, lasti)` and a program counter `pc`:

```text
Covers(e, pc) ⇔ e.start ≤ pc < e.end
```

The innermost covering entry is the one with the largest `start`. Ties are resolved by the smallest `end`.

The handler target is valid only if the instruction at `target` exists. A target that points past the instruction stream is rejected.

### 7.6 With protocol — CPython 3.8–3.10

These versions use a block stack. The relevant opcodes are:

- `SETUP_WITH target`
- `POP_BLOCK`
- `WITH_CLEANUP_START`
- `WITH_CLEANUP_FINISH`
- `END_FINALLY`

The block stack is modeled as:

```text
B' =
  B ∪ {( SETUP_WITH, target) } on SETUP_WITH
  B ∖ {top(B)} on POP_BLOCK
```

The normal exit is the first `POP_BLOCK` that empties the matching block. Nested withs and try/finally blocks share the same stack, so the parser must count both `SETUP_WITH` and `SETUP_FINALLY`.

### 7.7 With protocol — CPython 3.11–3.13

These versions use `BEFORE_WITH` and an exception-table handler:

```text
<context expression>
BEFORE_WITH
[STORE_FAST target | POP_TOP]
<body>
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 2
POP_TOP
JUMP_FORWARD after_handler
handler:
PUSH_EXC_INFO
WITH_EXCEPT_START
...
```

The normal cleanup is the `CALL 2` sequence. The handler is the `PUSH_EXC_INFO` block targeted by the exception table.

### 7.8 With protocol — CPython 3.14+

These versions use an explicit special-method protocol:

```text
<context expression>
COPY 1
LOAD_SPECIAL __exit__
SWAP 2
SWAP 3
LOAD_SPECIAL __enter__
CALL 0
[STORE_FAST target | POP_TOP]
<body>
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 3
POP_TOP
JUMP_FORWARD after_handler
handler:
PUSH_EXC_INFO
WITH_EXCEPT_START
...
cleanup:
COPY 3
POP_EXCEPT
RERAISE 1
```

For `async with`, the `CALL 0` is followed by an await sequence:

```text
GET_AWAITABLE
LOAD_CONST None
SEND target
YIELD_VALUE
RESUME
JUMP_BACKWARD_NO_INTERRUPT
END_SEND
```

The normal `__aexit__` call is followed by a second await sequence.

### 7.9 With-handler selection

Let:

- `E` be the set of exception-table entries.
- `o_body` be the offset of the first body instruction.
- `o_body_start` be the offset at the start of the body range, including leading `NOP`/`NOT_TAKEN`.
- `W` be the set of with-handler targets:

```text
W = { i | op(i) = PUSH_EXC_INFO ∧ op(i+1) = WITH_EXCEPT_START }
```

Then the handler for the current with is:

```text
H_with = argmax { e.start | e ∈ E,
                   e.start ≤ o_body,
                   e.end ≥ o_body_start,
                   target(e) ∈ W }
```

The maximum `start` chooses the innermost entry, so an inner with is not confused with an outer with. The `end` condition handles fragmented protection where the first entry ends exactly at the body boundary, for example a with whose first statement is a try.

After `H_with` is known, the cleanup scan is bounded by that handler and by the parser's `endIdx`.

### 7.10 Cleanup anchor

A with body can contain several cleanup sequences: one for the normal path and one for each `break`, `continue`, or `return` path.

Let `C` be the candidate cleanup starts in the bounded range. Let `cont(H)` be the continuation of the handler:

```text
cont(H) =
  target(j)        if the instruction after POP_EXCEPT is a jump j
  offset(next(H))  otherwise
```

The anchor cleanup is:

```text
c* = max { c ∈ C | next(c) = cont(H) }
```

If no cleanup matches, the fallback is:

```text
c* = max C
```

The fallback covers bodies that only exit through `break` or `return`.

For multi-item withs, the parser takes the final chain of `items.Count` cleanups. A nested cleanup inside the body appears earlier in the linear stream, so the final chain belongs to the current with.

### 7.11 Nested cleanup predicate

A cleanup can belong to an inner with or an inner try. It must not be used as the body boundary of the outer with.

Define:

```text
nested(c) ⇔ ∃ e ∈ E:
target(e) ≤ H ∧ e.start ≤ c < e.end
```

A cleanup with `nested(c) = true` is skipped. A cleanup protected only by an ancestor handler (`target > H`) is kept.

The predicate has an important edge case. An entry that starts inside a nested handler region but ends outside that region can be the tail of an async-for loop. The implementation therefore rejects only entries that end inside the nested region:

```text
reject(e, r) ⇔ e.start ≥ r.start ∧ e.start ≤ r.end ∧ e.end ≤ r.end
```

This keeps `END_ASYNC_FOR` tails attached to the body.

### 7.12 Try body extent

The protected body of a try can be fragmented by nested withs, async-fors, and nested trys.

Let `B_0 = o_body_start`. Define the fixpoint:

```text
B[k+1] = max { e.end | target(e) = H_try, e.start ≤ B_k, e.end > B_k, ¬transition(e) }
```

Stop when `B_{k+1} = B_k`. The body end is the fixed point.

`transition(e)` marks handler-internal entries: the `COPY/POP_EXCEPT/RERAISE` blocks and the normal-exit transition of a nested handler. These entries start after the body and must not extend it.

### 7.13 Multi-item with and fusion

CPython fuses a store and a following local load in the multi-item form:

```text
with a as x, b as y:
```

compiles the first target store and the next context load into:

```text
STORE_FAST_LOAD_FAST x, b
```

When the bridge splits the fused instruction, both halves keep the same offset. The parser uses this as the discriminator:

```text
multi_item ⇔ offset(ctx_i) = offset(store_(i-1))
```

If the condition is false, the nested form is emitted. The nested form compiles to the same bytecode when no fusion occurred, so the choice is safe.

### 7.14 Inline exit cleanup grammar

An inline cleanup before `break`, `continue`, or `return` has this shape:

```text
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 3
POP_TOP^n
exit
```

where `exit` is one of:

```text
JUMP_FORWARD
JUMP_BACKWARD
JUMP_BACKWARD_NO_INTERRUPT
RETURN_VALUE
RETURN_CONST
LOAD_CONST None
RETURN_VALUE
```

The parser skips the cleanup and lets the following instruction produce the real `BreakStmt`, `ContinueStmt`, or `ReturnStmt`.

### 7.15 Dead else reconstruction

In a `try/except/else/finally` where the try body always raises, the else body is unreachable but its constants remain in `co_consts`.

Let `UsedConsts` be the set of constants referenced by the emitted AST. The dead-else candidate set is:

```text
E_dead = { c ∈ co_consts | c ∉ UsedConsts ∧ preservable(c) }
```

A constant is preservable when it is a string, number, tuple, or other literal that the compiler retained. The reconstruction only applies to 3.10 and 3.11 and only when the finally body proves that the compiler retained the clause's constants.

### 7.16 Folded constant provenance

CPython can fold a constant expression at compile time. The folded value is stored at operand slot 0 of `co_consts`, while the source expression may still appear in the bytecode as an operation.

The reconstruction rule is:

```text
fold(op, a, b) = eval(op, a, b)
```

Emit the folded literal only when:

1. The value matches `co_consts[0]`.
2. The type matches exactly.
3. The operand positions prove that the value was folded.

If the pool order shows that the value was inserted late, emit the expression and let the compiler fold it again. This preserves the constant order without hardcoding specific values.

### 7.17 Complexity

Let:

- `N` = number of instructions.
- `E` = number of exception-table entries.
- `K` = number of constants.
- `D` = maximum nesting depth.

| Phase | Time | Space |
|---|---|---|
| Load and serialize | O(N + K) | O(N + K) |
| Scope building | O(E log E) | O(E) |
| Stack simulation | O(N) amortized | O(D) |
| AST construction | O(N) amortized | O(N) |
| Emit | O(N) | O(N) |
| Recursive compare | O(N + K) | O(D) |

Worst-case nested ranges can degrade the scope scan to O(N·D), but D is small for real Python code.

---

## 8. Architecture

### 8.1 Source layout

```text
Program.cs
pycrune.csproj
AST/
  AstNodes.cs
Analysis/
  SyntheticCleanupPass.cs
CFG/
  BasicBlock.cs
  ControlFlowGraph.cs
  DominatorTree.cs
  ExceptionScope.cs
  LoopAnalysis.cs
Core/
  PyBridge.cs
  PythonLocator.cs
Decompiler/
  PythonEmitter.cs
  StructuralDecompiler.cs
  SymbolicStack.cs
Model/
  ExceptionTableEntry.cs
  FailFastException.cs
  FrozenSetValue.cs
  OpCodes.cs
  PythonCodeObject.cs
  PythonInstruction.cs
  SliceValue.cs
Verification/
  BytecodeComparer.cs
  ExecutionVerifier.cs
```

### 8.2 Responsibilities

| File | Responsibility |
|---|---|
| `Program.cs` | CLI parsing, commands, exit codes |
| `PythonLocator.cs` | Magic number to version, interpreter selection |
| `PyBridge.cs` | Python server, JSON protocol, code-object serialization |
| `StructuralDecompiler.cs` | Stack simulation, scope building, AST construction |
| `PythonEmitter.cs` | AST to Python source |
| `SymbolicStack.cs` | Abstract stack values |
| `AstNodes.cs` | AST records |
| `ControlFlowGraph.cs` | Basic blocks and edges |
| `DominatorTree.cs` | Dominance queries |
| `ExceptionScope.cs` | Exception region analysis |
| `SyntheticCleanupPass.cs` | Synthetic cleanup removal |
| `BytecodeComparer.cs` | Recursive code-object comparison |
| `ExecutionVerifier.cs` | Behavioral comparison |

### 8.3 Bridge protocol

Request:

```json
{"id": 1, "path": "input.pyc"}
```

Source request:

```json
{"id": 2, "source": "x = 1\n"}
```

Response:

```json
{"id": 1, "ok": true, "code": {"name": "<module>", "instructions": [], "consts": [], "names": [], "varnames": [], "freevars": [], "cellvars": [], "exceptiontable": []}}
```

Error:

```json
{"id": 1, "ok": false, "error": "message"}
```

The server is line-oriented. One JSON object per line. The client writes a request and reads one response.

---

## 9. Requirements

### 9.1 Minimum

- .NET 10 SDK (build) or .NET 10 runtime (run a published build).
- One CPython interpreter for each version you want to decompile.
- Windows, Linux, or macOS on x64 or ARM64.
- 200 MB free disk for the build; more for Python installations.

### 9.2 Python interpreter matrix

| Version | Windows command | Unix command | Notes |
|---|---|---|---|
| 3.8 | `py -3.8` | `python3.8` | Legacy block stack |
| 3.9 | `py -3.9` | `python3.9` | Legacy block stack |
| 3.10 | `py -3.10` | `python3.10` | Pattern matching |
| 3.11 | `py -3.11` or embedded | `python3.11` | Exception table |
| 3.12 | `python` | `python` | Host version in the default resolver |
| 3.13 | `py -3.13` | `python3.13` | Fused locals |
| 3.14 | `py -3.14` | `python3.14` | `LOAD_SPECIAL` |
| 3.15 | `py -3.15` | `python3.15` | `LOAD_SPECIAL` |

The current resolver uses the Windows `py` launcher for all versions except 3.12, and `python` for 3.12. On Linux and macOS, create the `py` shim described in [§10.2](#102-unix-py-shim).

### 9.3 Disk and memory

| Item | Approximate |
|---|---|
| Source tree | 1 MB |
| Release build | 20–40 MB |
| Each CPython installation | 50–150 MB |
| 88,000 test pairs | 900 MB |
| Peak memory, small file | < 100 MB |
| Peak memory, xlarge file | 0.5–2 GB |

---

## 10. Setup on Every Platform

This section is a complete setup guide. Choose your platform.

### 10.1 Windows 10/11 x64

#### Step 1 — Install .NET 10 SDK

Using winget:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
```

Verify:

```powershell
dotnet --version
dotnet --list-sdks
```

Expected: a `10.0.x` SDK.

If winget is unavailable, download the SDK installer from `https://dotnet.microsoft.com/download/dotnet/10.0` and run it.

#### Step 2 — Install Python versions

Option A — python.org installers:

1. Download the Windows installer for each version from `https://www.python.org/downloads/windows/`.
2. Run the installer.
3. Enable **Add python.exe to PATH**.
4. Enable **py launcher** for all users if prompted.

Option B — winget for versions that are available:

```powershell
winget install --id Python.Python.3.8 -e
winget install --id Python.Python.3.9 -e
winget install --id Python.Python.3.10 -e
winget install --id Python.Python.3.11 -e
winget install --id Python.Python.3.12 -e
winget install --id Python.Python.3.13 -e
```

For 3.14 and 3.15, use the python.org installer or pyenv-win.

Option C — pyenv-win:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/pyenv-win/pyenv-win/master/pyenv-win/install-pyenv-win.ps1" -OutFile "./install-pyenv-win.ps1"
./install-pyenv-win.ps1
pyenv install 3.8.20
pyenv install 3.9.21
pyenv install 3.10.16
pyenv install 3.11.11
pyenv install 3.12.8
pyenv install 3.13.1
pyenv install 3.14.0
pyenv install 3.15.0
```

Use the latest patch that exists for each minor version. `pyenv install --list` shows the available set.

#### Step 3 — Verify the launcher

```powershell
py -0p
```

You should see every installed version with its path.

Test one version:

```powershell
py -3.14 -c "import sys; print(sys.version)"
```

#### Step 4 — Build

```powershell
git clone <your-repository-url> pycrune
cd pycrune
dotnet build pycrune.csproj -c Release
```

#### Step 5 — Run

```powershell
.\bin\Release\net10.0\pycrune.exe decompile input.pyc -o output.py --verify
```

#### Step 6 — Optional PATH

Add the build output directory to your user PATH:

```powershell
$env:Path += ";$PWD\bin\Release\net10.0"
```

Or use the full path each time.

### 10.2 Unix `py` shim

The resolver calls `py -X.Y` for versions other than 3.12. Linux and macOS do not ship a `py` launcher by default. Create a shim once.

Create `/usr/local/bin/py`:

```bash
sudo tee /usr/local/bin/py >/dev/null <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [ "$#" -eq 0 ]; then
  exec python3 "$@"
fi
case "$1" in
  -3.*)
    ver="${1#-}"
    shift
    exec "python${ver}" "$@"
    ;;
  *)
    exec python3 "$@"
    ;;
esac
SH
sudo chmod +x /usr/local/bin/py
```

Verify:

```bash
py -3.12 -c "import sys; print(sys.version)"
```

Ensure `python` points to 3.12 because the resolver uses `python` for 3.12:

```bash
python --version
```

If it does not, create a user-local symlink or use pyenv:

```bash
pyenv global 3.12.8
```

Alternative without sudo: put the shim in `~/.local/bin/py` and add that directory to `PATH`.

### 10.3 Windows ARM64

Use the ARM64 builds:

- .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10` (the installer selects ARM64 on an ARM64 OS).
- Python: python.org provides ARM64 installers for recent versions. For older versions, use x64 emulation or pyenv-win with an ARM64 build.
- Build and run the same commands as x64.

Verify the architecture:

```powershell
[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
```

### 10.4 WSL2 (Ubuntu)

Install WSL2:

```powershell
wsl --install -d Ubuntu
```

Inside Ubuntu:

```bash
sudo apt-get update
sudo apt-get install -y curl git build-essential libssl-dev zlib1g-dev \
  libbz2-dev libreadline-dev libsqlite3-dev libffi-dev liblzma-dev \
  libncursesw5-dev tk-dev xz-utils
```

Install .NET 10:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

Install pyenv:

```bash
curl -sSL https://pyenv.run | bash
export PATH="$HOME/.pyenv/bin:$PATH"
eval "$(pyenv init -)"
```

Install Python versions:

```bash
pyenv install 3.8.20
pyenv install 3.9.21
pyenv install 3.10.16
pyenv install 3.11.11
pyenv install 3.12.8
pyenv install 3.13.1
pyenv install 3.14.0
pyenv install 3.15.0
```

Create the `py` shim from [§10.2](#102-unix-py-shim), then:

```bash
git clone <your-repository-url> pycrune
cd pycrune
dotnet build pycrune.csproj -c Release
./bin/Release/net10.0/pycrune decompile input.pyc -o output.py --verify
```

### 10.5 Ubuntu / Debian

#### .NET 10

Use the install script:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
dotnet --version
```

Or use the Microsoft package repository if it already provides a 10.0 package for your release.

#### Python

Option A — deadsnakes PPA:

```bash
sudo add-apt-repository ppa:deadsnakes/ppa
sudo apt-get update
sudo apt-get install -y python3.8 python3.9 python3.10 python3.11 python3.12 python3.13
```

3.14 and 3.15 may require a newer PPA or a source build. Use pyenv for those.

Option B — pyenv:

```bash
sudo apt-get install -y make build-essential libssl-dev zlib1g-dev \
  libbz2-dev libreadline-dev libsqlite3-dev wget curl llvm \
  libncursesw5-dev xz-utils tk-dev libxml2-dev libxmlsec1-dev \
  libffi-dev liblzma-dev git
curl -sSL https://pyenv.run | bash
export PATH="$HOME/.pyenv/bin:$PATH"
eval "$(pyenv init -)"
pyenv install 3.8.20
pyenv install 3.9.21
pyenv install 3.10.16
pyenv install 3.11.11
pyenv install 3.12.8
pyenv install 3.13.1
pyenv install 3.14.0
pyenv install 3.15.0
```

Install the shim, build, and test.

### 10.6 Fedora / RHEL

```bash
sudo dnf install -y git curl gcc make openssl-devel zlib-devel \
  bzip2-devel readline-devel sqlite-devel libffi-devel xz-devel \
  ncurses-devel tk-devel
```

Install .NET 10:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

Install pyenv and the Python versions as in §10.5. Create the `py` shim.

### 10.7 Arch Linux

```bash
sudo pacman -S --needed git base-devel openssl zlib bzip2 readline \
  sqlite libffi xz ncurses tk curl
```

Install .NET 10:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

Install pyenv from AUR or from source:

```bash
curl -sSL https://pyenv.run | bash
export PATH="$HOME/.pyenv/bin:$PATH"
eval "$(pyenv init -)"
```

Install the versions and the `py` shim.

### 10.8 macOS Intel

Install Homebrew if needed:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

Install .NET 10:

```bash
brew install --cask dotnet-sdk
dotnet --version
```

If the cask installs a newer major version, use the install script:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

Install Python versions with pyenv:

```bash
brew install pyenv
export PATH="$HOME/.pyenv/bin:$PATH"
eval "$(pyenv init -)"
pyenv install 3.8.20
pyenv install 3.9.21
pyenv install 3.10.16
pyenv install 3.11.11
pyenv install 3.12.8
pyenv install 3.13.1
pyenv install 3.14.0
pyenv install 3.15.0
```

Create the shim in `/usr/local/bin/py` or `~/.local/bin/py`.

### 10.9 macOS Apple Silicon

The same steps as Intel, with two notes:

- Use ARM64 builds. Homebrew installs ARM64 by default under `/opt/homebrew`.
- Python 3.8 may require an x86_64 build or a newer patch. If pyenv fails, try `arch -x86_64 pyenv install 3.8.20` under Rosetta, or use a container.

Verify:

```bash
uname -m
```

Expected: `arm64`.

### 10.10 Docker

#### Minimal image for one Python version

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY . .
RUN dotnet publish pycrune.csproj -c Release -r linux-x64 --self-contained true -o /out

FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates libicu72 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
COPY docker/py /usr/local/bin/py
RUN chmod +x /usr/local/bin/py
ENTRYPOINT ["/app/pycrune"]
```

`docker/py` is the shim from §10.2.

Build and run:

```bash
docker build -t pycrune:3.12 .
docker run --rm -v "$PWD:/work" pycrune:3.12 decompile /work/input.pyc -o /work/output.py --verify
```

#### Full matrix image

A full image needs all eight interpreters. Use pyenv:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY . .
RUN dotnet publish pycrune.csproj -c Release -r linux-x64 --self-contained true -o /out

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates libicu72 curl git build-essential libssl-dev zlib1g-dev \
    libbz2-dev libreadline-dev libsqlite3-dev libffi-dev liblzma-dev \
    libncursesw5-dev tk-dev xz-utils && rm -rf /var/lib/apt/lists/*
RUN git clone https://github.com/pyenv/pyenv.git /opt/pyenv
ENV PATH="/opt/pyenv/bin:$PATH"
RUN pyenv install 3.8.20 \
    && pyenv install 3.9.21 \
    && pyenv install 3.10.16 \
    && pyenv install 3.11.11 \
    && pyenv install 3.12.8 \
    && pyenv install 3.13.1 \
    && pyenv install 3.14.0 \
    && pyenv install 3.15.0
RUN ln -s /opt/pyenv/versions/3.12.8/bin/python3.12 /usr/local/bin/python
COPY --from=build /out /app
COPY docker/py /usr/local/bin/py
RUN chmod +x /usr/local/bin/py
ENTRYPOINT ["/app/pycrune"]
```

This image is large and takes a long time to build. For CI, build it once and cache it.

### 10.11 GitHub Actions

Minimal build workflow:

```yaml
name: build
on:
  push:
  pull_request:
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - run: dotnet build pycrune.csproj -c Release
```

Matrix with Python versions:

```yaml
name: matrix
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        python: ["3.8", "3.9", "3.10", "3.11", "3.12", "3.13", "3.14", "3.15"]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"
      - uses: actions/setup-python@v5
        with:
          python-version: ${{ matrix.python }}
      - run: dotnet build pycrune.csproj -c Release
      - run: echo "Build completed for Python ${{ matrix.python }}"
```

`actions/setup-python` may lag behind the newest CPython release. If a version is unavailable, use pyenv in the job.

### 10.12 Raspberry Pi / ARM64 Linux

Use a 64-bit OS. The 32-bit ARM builds are not a supported target for .NET 10.

```bash
sudo apt-get update
sudo apt-get install -y curl git build-essential libssl-dev zlib1g-dev \
  libbz2-dev libreadline-dev libsqlite3-dev libffi-dev liblzma-dev \
  libncursesw5-dev tk-dev xz-utils
```

Install .NET 10 with the install script, then pyenv and the Python versions. Build and run as on Ubuntu.

For a Pi with limited RAM, install one Python version at a time and use `MAKE_OPTS="-j2"`.

### 10.13 Android / Termux

.NET 10 is not distributed for Termux as an official target. Use one of these options:

1. Run PycRune on a PC and copy the output.
2. Use `proot-distro` with Ubuntu:

```bash
pkg install proot-distro
proot-distro install ubuntu
proot-distro login ubuntu
```

Inside the Ubuntu environment, follow §10.5. Expect slow builds and limited memory.

3. Run the Docker image on a remote machine and connect over SSH.

### 10.14 IDE setup

#### Visual Studio Code

Install the C# Dev Kit extension and the .NET 10 SDK. Open the repository folder. The project is detected automatically.

Recommended `.vscode/settings.json`:

```json
{
  "dotnet.defaultSolution": "pycrune.csproj",
  "files.encoding": "utf8",
  "editor.formatOnSave": false
}
```

#### Visual Studio 2022/2026

Open `pycrune.csproj` as a project. Select the Release configuration. Set the startup project to `pycrune`.

#### JetBrains Rider

Open the repository folder. Rider detects `pycrune.csproj`. Set the run configuration to the `pycrune` project and pass CLI arguments such as:

```text
decompile input.pyc -o output.py --verify
```

### 10.15 Post-install checklist

```bash
dotnet --version
py -0p
py -3.8 -c "import sys; print(sys.version)"
py -3.12 -c "import sys; print(sys.version)"
py -3.15 -c "import sys; print(sys.version)"
```

On Unix, also:

```bash
py -3.8 -c "import sys; print(sys.version)"
python --version
```

Build:

```bash
dotnet build pycrune.csproj -c Release
```

Smoke test:

```bash
./bin/Release/net10.0/pycrune decompile ../test/000001.pyc -o /tmp/000001.py --verify
```

If all steps pass, the environment is ready.
## 11. Build from Source

### 11.1 Release build

```bash
dotnet build pycrune.csproj -c Release
```

Output:

```text
bin/Release/net10.0/pycrune.dll
bin/Release/net10.0/pycrune.exe      (Windows)
bin/Release/net10.0/pycrune          (Unix apphost)
```

### 11.2 Publish a self-contained build

Windows x64:

```bash
dotnet publish pycrune.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Linux x64:

```bash
dotnet publish pycrune.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

Linux ARM64:

```bash
dotnet publish pycrune.csproj -c Release -r linux-arm64 --self-contained true -o publish/linux-arm64
```

macOS Intel:

```bash
dotnet publish pycrune.csproj -c Release -r osx-x64 --self-contained true -o publish/osx-x64
```

macOS Apple Silicon:

```bash
dotnet publish pycrune.csproj -c Release -r osx-arm64 --self-contained true -o publish/osx-arm64
```

A self-contained build does not require the .NET runtime on the target machine. It still requires the Python interpreters.

### 11.3 Debug build

```bash
dotnet build pycrune.csproj -c Debug
```

Use the Debug build with a debugger attached when investigating parser behavior.

### 11.4 Clean

```bash
dotnet clean pycrune.csproj
rm -rf bin obj
```

On Windows PowerShell:

```powershell
dotnet clean pycrune.csproj
Remove-Item -Recurse -Force bin, obj
```

---

## 12. CLI Reference

### 12.1 Synopsis

```text
pycrune <command> [arguments]
```

### 12.2 Commands

| Command | Purpose |
|---|---|
| `decompile` | Decompile a `.pyc` or `.py` file |
| `verify` | Verify a decompiled source against an original file |
| `compare` | Compare bytecode only |
| `batch` | Decompile every `.pyc` under a directory |
| `inspect` | Print CFG blocks and exception scopes |

### 12.3 decompile

```text
pycrune decompile <path> [-o <out.py>] [--verify]
```

Arguments:

| Argument | Required | Meaning |
|---|---|---|
| `<path>` | Yes | Input `.pyc` or `.py` |
| `-o <out.py>` | No | Write source to a file |
| `--verify` | No | Run behavior and bytecode verification |

Without `-o`, the source is printed to stdout. Long output is truncated at 2000 characters in the console preview; use `-o` for the full file.

Examples:

```bash
pycrune decompile module.pyc
pycrune decompile module.pyc -o module.py
pycrune decompile module.pyc -o module.py --verify
pycrune decompile module.py -o module.pyc.source.py
```

Exit codes:

| Code | Meaning |
|---:|---|
| 0 | Success |
| 1 | Usage error or verification mismatch |
| 2 | Critical error (parse, bridge, I/O) |

### 12.4 verify

```text
pycrune verify <original> <decompiled>
```

Runs the original and the decompiled source, compares exit code and stdout, then compares bytecode.

Example:

```bash
pycrune verify input.pyc output.py
```

Use this command when the source was produced elsewhere or edited by hand.

### 12.5 compare

```text
pycrune compare <original-pyc> <decompiled-source>
```

Compares bytecode only. Does not execute either program. Useful in CI and for fast checks.

Example:

```bash
pycrune compare input.pyc output.py
```

### 12.6 batch

```text
pycrune batch <directory>
```

Finds every `.pyc` under the directory recursively and decompiles each one. The output is a per-file status line. The command does not write source files; it is a coverage and smoke-test utility.

Example:

```bash
pycrune batch ../test
```

For production batch conversion, write a small script that calls `decompile -o` per file. Example in Bash:

```bash
#!/usr/bin/env bash
set -euo pipefail
input_dir="$1"
output_dir="$2"
mkdir -p "$output_dir"
find "$input_dir" -name '*.pyc' -print0 | while IFS= read -r -d '' f; do
  rel="${f#"$input_dir"/}"
  out="$output_dir/${rel%.pyc}.py"
  mkdir -p "$(dirname "$out")"
  pycrune decompile "$f" -o "$out"
done
```

PowerShell:

```powershell
param([string]$InputDir, [string]$OutputDir)
Get-ChildItem -Path $InputDir -Filter *.pyc -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring((Resolve-Path $InputDir).Path.Length + 1)
    $out = Join-Path $OutputDir ($rel -replace '\.pyc$', '.py')
    New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
    & pycrune decompile $_.FullName -o $out
}
```

### 12.7 inspect

```text
pycrune inspect <path>
```

Prints:

- Number of CFG blocks.
- Number of exception scopes.
- Each scope: start offset, end offset, handler count, finally flag, exit block.

Example:

```bash
pycrune inspect input.pyc
```

This command is useful when a parse fails and you need to see how the exception table was interpreted.

---

## 13. Verification Guide

### 13.1 The two checks

| Check | What it runs | What it proves |
|---|---|---|
| Behavior | Original program and decompiled program | Same exit code and stdout |
| Bytecode | Recompile decompiled source | Same code object |

A full `--verify` runs both.

### 13.2 Reading the output

Success:

```text
[PASS] 100% BEHAVIORAL FIDELITY CONFIRMED (ExitCode and Output matched identically)
[PASS] Bytecode identical: 268 instructions matched with 0 differences!
```

Behavior failure:

```text
[FAIL] Execution divergence:
[EXIT CODE MISMATCH] Original=0, Decompiled=1
```

Bytecode failure:

```text
[!] Bytecode Differences: 12 discrepancies found between 336 orig and 324 decompiled instructions.
<module>/run#2: constant count 9 vs 8
<module>/run#2: instr #18 opcode LOAD_GLOBAL vs JUMP_BACKWARD
```

### 13.3 First discrepancy wins

The comparator reports the first differences in document order. Fix the earliest discrepancy first. Later differences are often consequences of the first one.

### 13.4 Verifying a directory

Bash:

```bash
pass=0
fail=0
for f in ../test/*.pyc; do
  if pycrune compare "$f" "${f%.pyc}.py" >/dev/null 2>&1; then
    pass=$((pass+1))
  else
    fail=$((fail+1))
    echo "FAIL $f"
  fi
done
echo "pass=$pass fail=$fail"
```

PowerShell:

```powershell
$pass = 0
$fail = 0
Get-ChildItem ../test/*.pyc | ForEach-Object {
    $src = [System.IO.Path]::ChangeExtension($_.FullName, ".py")
    & pycrune compare $_.FullName $src *> $null
    if ($LASTEXITCODE -eq 0) { $pass++ } else { $fail++; Write-Host "FAIL $($_.Name)" }
}
Write-Host "pass=$pass fail=$fail"
```

### 13.5 CI gate

```yaml
- name: Bytecode gate
  run: |
    dotnet build pycrune.csproj -c Release
    ./bin/Release/net10.0/pycrune compare input.pyc output.py
```

Use `compare` when execution is not safe or not deterministic. Use `verify` when behavior matters.

---

## 14. Interpreter Resolution

### 14.1 Default rules

`PythonLocator.Resolve` applies these rules:

1. If the input is not a `.pyc`, use `ResolveVersion(versionHint)`.
2. Read bytes 0–1 as a little-endian magic.
3. Look up the magic in the version table.
4. For 3.11, use `multiver/python311-embed/python.exe` if it exists.
5. For 3.12, use `python`.
6. For every other version, use `py -X.Y`.
7. If the magic is unknown, use `python`.

### 14.2 Why 3.12 uses `python`

The default resolver assumes the host Python is 3.12. This is the version used by the development environment. If your host Python is different, create a `python` symlink or use pyenv to select 3.12 for the session.

### 14.3 Why 3.11 has an embedded path

Some environments ship an embedded 3.11 interpreter. If the directory exists, PycRune uses it directly. If not, it falls back to `py -3.11`.

### 14.4 Custom interpreter paths

The clean distribution does not expose an environment-variable override. To use a custom path, edit `Core/PythonLocator.cs` and change the returned command. Keep the version mapping intact.

Example change for a custom 3.14 path:

```csharp
if (version == "3.14")
{
    return (@"C:\Python\3.14\python.exe", "", version);
}
```

Rebuild after the change.

### 14.5 Verifying the mapping

```bash
pycrune inspect input.pyc
```

The output does not print the interpreter, but a successful load proves that the selected interpreter can unmarshal the file. For a direct check:

```bash
py -3.14 -c "import importlib.util; print(importlib.util.MAGIC_NUMBER.hex())"
```

Compare the magic with the file header:

```bash
python -c "print(open('input.pyc','rb').read(4).hex())"
```

---

## 15. Performance

### 15.1 Persistent bridge

The bridge server stays alive per worker. Python startup is paid once, not once per file.

Typical numbers on a modern desktop:

| Workload | Time |
|---|---|
| Small file, warm bridge | 20–50 ms |
| Medium file | 50–200 ms |
| Large file | 0.2–2 s |
| Xlarge file | 2–20 s |
| 10,000 small files, 8 workers | 1–3 minutes |

### 15.2 Memory

| Workload | Peak |
|---|---|
| Small file | < 100 MB |
| Large file | 200–500 MB |
| Xlarge file | 0.5–2 GB |

The bridge serializes the code object into JSON. For very large files, JSON overhead is the dominant cost.

### 15.3 Parallelism

The CLI processes one file per invocation. To parallelize, run multiple processes or use a script with a worker pool. Each process starts its own bridge.

### 15.4 Profiling

Use `dotnet-trace` or `dotnet-counters` for the C# side. Use `cProfile` inside the bridge only for development; the bridge script is intentionally small.

---

## 16. Troubleshooting

### 16.1 `py` is not recognized

Windows:

- Install the Python Launcher.
- Run `py -0p`.
- Reinstall Python with the launcher option enabled.

Unix:

- Create the shim from §10.2.
- Ensure `python3.X` is on `PATH`.

### 16.2 `py -3.14` is not recognized

The version is not registered with the launcher. Install it from python.org or pyenv, then run:

```powershell
py -0p
```

### 16.3 Magic number is unknown

Symptoms:

```text
Unsupported opcode
```

or a marshal error.

Causes:

- The file was compiled by an unsupported CPython version.
- The magic was patched.
- The file is not a `.pyc`.

Check the header:

```bash
python -c "print(open('input.pyc','rb').read(4).hex())"
```

Known values are in [§20](#20-appendix-c--magic-numbers).

### 16.4 `no marshal code object found`

The bridge could not find a marshal payload. Causes:

- The file is truncated.
- The header size is not 16.
- The payload is compressed or encrypted.
- The file is a raw marshal dump with an unusual prefix.

The bridge scans a bounded window. If the scan fails, the file is not supported.

### 16.5 Stack underflow

```text
Stack underflow
```

This means the symbolic stack ran out of values for an instruction. It is a decompiler bug, not a user error. Capture the file and the instruction index, then reproduce with `inspect`.

### 16.6 Behavior mismatch but bytecode identical

Possible causes:

- The program depends on its file path, `__file__`, or the current directory.
- The program reads stdin.
- The program uses randomness, time, or network access.
- The program depends on environment variables.

Run both programs under the same conditions. For deterministic checks, use `compare` instead of `verify`.

### 16.7 Bytecode mismatch but behavior identical

The source is behaviorally equivalent but not bytecode-identical. This is still a failure under the project's acceptance rule. Inspect the first discrepancy, then fix the emitter or parser.

### 16.8 Build fails with missing SDK

```text
The current .NET SDK does not support targeting .NET 10.0
```

Install the .NET 10 SDK and select it:

```bash
dotnet --list-sdks
dotnet --version
```

If multiple SDKs are installed, add a `global.json`:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  }
}
```

### 16.9 Build fails on Linux with missing native libraries

Install the build dependencies listed in §10.5. For Python builds, the usual missing packages are `libssl-dev`, `zlib1g-dev`, `libffi-dev`, and `liblzma-dev`.

### 16.10 Permission denied on Unix

```bash
chmod +x ./bin/Release/net10.0/pycrune
```

If the binary is on a mounted filesystem with `noexec`, copy it to a local disk.

### 16.11 Paths with spaces

Quote every path:

```bash
pycrune decompile "C:\My Files\input.pyc" -o "C:\My Files\output.py"
```

### 16.12 Antivirus interference

Some antivirus products delay or block newly built executables. Add the build output directory to the exclusion list, or run from a user directory.

### 16.13 Docker cannot find Python

The image must contain the interpreters. Use the full matrix image or install the required version in the container. The `py` shim must be executable.

### 16.14 CI cannot install Python 3.15

`actions/setup-python` may not have the newest version. Use pyenv in the job:

```yaml
- name: Install Python
  run: |
    curl -sSL https://pyenv.run | bash
    export PATH="$HOME/.pyenv/bin:$PATH"
    eval "$(pyenv init -)"
    pyenv install 3.15.0
    pyenv global 3.15.0
```

---

## 17. FAQ

**Q: Does PycRune recover comments?**  
No. Comments are not stored in `.pyc` files.

**Q: Does it recover original variable names?**  
Local variable names are stored in `co_varnames`. They are recovered when present. Names optimized away by the compiler cannot be recovered.

**Q: Does it recover formatting?**  
No. The emitter produces a consistent style. Bytecode identity does not depend on formatting.

**Q: Can it decompile a `.py` file?**  
Yes. The file is compiled first, then decompiled.

**Q: Can it decompile Python 2?**  
No. The supported range is CPython 3.8–3.15.

**Q: Does it execute untrusted code?**  
Decompilation does not execute the input. `verify` executes both programs, so use it only with trusted files or in a sandbox.

**Q: Why does `verify` fail on a program that reads stdin?**  
Both programs receive no stdin in the default verifier. Provide deterministic input or use `compare`.

**Q: Why is the output not textually identical to the original?**  
The original source is not stored in the `.pyc`. The output is a reconstruction that compiles to the same code object.

**Q: Can I use the library without the CLI?**  
Yes. Reference the project and call `PyBridge.Load`, `StructuralDecompiler`, and `PythonEmitter` directly.

**Q: Is there a NuGet package?**  
Not in this distribution. Build from source or publish your own package.

**Q: Does it support PyPy?**  
No. The bytecode format is CPython-specific.

**Q: Does it support free-threaded builds?**  
The bytecode format is the same for the standard and free-threaded builds in the supported versions. The interpreter must be able to unmarshal the file.

---

## 18. Appendix A — With / Async-With Protocols

### 18.1 CPython 3.8–3.10

```text
<context expression>
SETUP_WITH target
[STORE_FAST target | POP_TOP]
<body>
POP_BLOCK
LOAD_CONST None
BEGIN_FINALLY
WITH_CLEANUP_START
WITH_CLEANUP_FINISH
END_FINALLY
```

The block stack must count both `SETUP_WITH` and `SETUP_FINALLY`. A nested try/finally can otherwise steal the `POP_BLOCK` of the with.

### 18.2 CPython 3.11–3.13

```text
<context expression>
BEFORE_WITH
[STORE_FAST target | POP_TOP]
<body>
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 2
POP_TOP
JUMP_FORWARD after_handler
handler:
PUSH_EXC_INFO
WITH_EXCEPT_START
...
cleanup:
COPY 3
POP_EXCEPT
RERAISE 1
```

### 18.3 CPython 3.14–3.15

```text
<context expression>
COPY 1
LOAD_SPECIAL __exit__
SWAP 2
SWAP 3
LOAD_SPECIAL __enter__
CALL 0
[STORE_FAST target | POP_TOP]
<body>
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 3
POP_TOP
JUMP_FORWARD after_handler
handler:
PUSH_EXC_INFO
WITH_EXCEPT_START
...
cleanup:
COPY 3
POP_EXCEPT
RERAISE 1
```

### 18.4 Async with

```text
<context expression>
COPY 1
LOAD_SPECIAL __aexit__
SWAP 2
SWAP 3
LOAD_SPECIAL __aenter__
CALL 0
GET_AWAITABLE
LOAD_CONST None
SEND target
YIELD_VALUE
RESUME
JUMP_BACKWARD_NO_INTERRUPT
END_SEND
[STORE_FAST target | POP_TOP]
<body>
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 3
GET_AWAITABLE
LOAD_CONST None
SEND target
YIELD_VALUE
RESUME
JUMP_BACKWARD_NO_INTERRUPT
END_SEND
POP_TOP
```

### 18.5 Handler layout

```text
PUSH_EXC_INFO
WITH_EXCEPT_START
TO_BOOL
POP_JUMP_IF_TRUE suppress
NOT_TAKEN
RERAISE 2
suppress:
POP_TOP
POP_EXCEPT
POP_TOP
POP_TOP
POP_TOP
JUMP_BACKWARD_NO_INTERRUPT after
cleanup:
COPY 3
POP_EXCEPT
RERAISE 1
```

For async with, the handler inserts an await sequence after `WITH_EXCEPT_START`.

---

## 19. Appendix B — Exception Table

### 19.1 Entry fields

| Field | Meaning |
|---|---|
| `start` | First protected offset |
| `end` | One past the last protected offset |
| `target` | Handler offset |
| `depth` | Stack depth at the handler |
| `lasti` | Whether the last instruction offset is pushed |

### 19.2 Coverage

```text
start <= pc < end
```

The innermost entry has the largest `start` among covering entries.

### 19.3 Handler kinds

| Pattern | Kind |
|---|---|
| `PUSH_EXC_INFO; WITH_EXCEPT_START` | with handler |
| `PUSH_EXC_INFO; LOAD_GLOBAL Exception; CHECK_EXC_MATCH` | except handler |
| `PUSH_EXC_INFO; ...; RERAISE` | finally handler |
| `COPY 3; POP_EXCEPT; RERAISE 1` | cleanup-only entry |
| `END_ASYNC_FOR` | async-for handler |
| `CLEANUP_THROW` | generator/await cleanup |

### 19.4 Fragmentation

A single try body can be split into several entries. The parser grows the body through contiguous entries for the same handler and stops at handler-internal transitions.

### 19.5 Example

```text
_ExceptionTableEntry(start=22, end=44, target=182, depth=2, lasti=True)
_ExceptionTableEntry(start=44, end=68, target=144, depth=4, lasti=True)
_ExceptionTableEntry(start=68, end=104, target=182, depth=2, lasti=True)
_ExceptionTableEntry(start=144, end=166, target=176, depth=6, lasti=True)
_ExceptionTableEntry(start=166, end=182, target=182, depth=2, lasti=True)
_ExceptionTableEntry(start=182, end=204, target=214, depth=4, lasti=True)
```

The entries targeting 182 form the protected body of the outer with. Entries targeting 144 belong to the inner with.

---

## 20. Appendix C — Magic Numbers

| Magic | Version |
|---:|---|
| 3413 | 3.8 |
| 3425 | 3.9 |
| 3439 | 3.10 |
| 3495 | 3.11 |
| 3531 | 3.12 |
| 3571 | 3.13 |
| 3627 | 3.14 |
| 3666 | 3.15 |

Header size: 16 bytes for all listed versions.

Read the magic:

```bash
python -c "print(int.from_bytes(open('input.pyc','rb').read(2),'little'))"
```

---

## 21. Appendix D — Glossary

| Term | Meaning |
|---|---|
| Code object | CPython's compiled representation of a module, function, class, or comprehension |
| Bytecode | The instruction sequence inside a code object |
| Marshal | CPython's binary serialization format for code objects |
| Magic number | Two-byte version identifier at the start of a `.pyc` |
| Co_consts | Constant pool of a code object |
| Co_names | Global and attribute names used by the code object |
| Co_varnames | Local variable names |
| Free variable | Variable captured from an enclosing scope |
| Cell variable | Variable stored in a cell for closure access |
| Exception table | Protected ranges and handler targets in 3.11+ |
| Block stack | Legacy protected-block stack in 3.8–3.10 |
| Symbolic stack | Abstract value stack used by the decompiler |
| Handler | Code that processes an exception |
| Cleanup | Code that calls `__exit__` or `__aexit__` on the normal path |
| Continuation | The point control returns to after a handler |
| Fusion | A combined instruction such as `STORE_FAST_LOAD_FAST` |
| Anchor cleanup | The cleanup chosen as the body boundary |
| Nested cleanup | A cleanup that belongs to an inner construct |
| Transition entry | An exception-table entry that covers handler-internal code |
| Dead else | An unreachable else body whose constants remain in the pool |
| Folded constant | A constant expression evaluated at compile time |

---
