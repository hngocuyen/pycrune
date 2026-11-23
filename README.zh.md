# PycRune

**PycRune：面向 CPython 3.8 – 3.15 的精确字节码反编译器**

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4">
  <img alt="CPython 3.8-3.15" src="https://img.shields.io/badge/CPython-3.8%20%E2%80%93%203.15-3776AB">
  <img alt="Platforms" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey">
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.zh.md"><b>中文</b></a> ·
  <a href="README.vi.md">Tiếng Việt</a>
</p>

PycRune 读取 `.pyc` 文件，重建 Python 源码，并用对应的 CPython 解释器重新编译该源码，递归比较生成的代码对象。验收标准是字节码恒等，而不是“看起来像”。

## 快速开始

```bash
dotnet build pycrune.csproj -c Release
./bin/Release/net10.0/pycrune decompile input.pyc -o output.py --verify
```

Windows：

```powershell
.\bin\Release\net10.0\pycrune.exe decompile input.pyc -o output.py --verify
```

需要 .NET 10 SDK 和对应的 CPython 解释器。参见 [全平台安装](#9-全平台安装)。

## 目录

1. [项目简介](#1-项目简介)
2. [设计目标](#2-设计目标)
3. [支持版本](#3-支持版本)
4. [工作流程](#4-工作流程)
5. [核心数据模型](#5-核心数据模型)
6. [数学公式](#6-数学公式)
7. [架构](#7-架构)
8. [环境要求](#8-环境要求)
9. [全平台安装](#9-全平台安装)
10. [构建](#10-构建)
11. [CLI 参考](#11-cli-参考)
12. [验证](#12-验证)
13. [解释器解析](#13-解释器解析)
14. [故障排查](#14-故障排查)
15. [附录](#15-附录)

---

## 1. 项目简介

PycRune 是一个用 C#/.NET 10 编写的 Python 字节码反编译器。它支持 CPython 3.8 到 3.15，核心目标是：反编译得到的源码重新编译后，生成的代码对象在 CPython 暴露的所有字段上与原始代码对象完全一致。

比较范围包括：

- 归一化后的指令序列。
- 操作码和操作数。
- 常量池的顺序、类型和值。
- 名称、局部变量、自由变量、单元格变量。
- 异常表。
- 递归比较所有嵌套代码对象。

### 能做什么

- 反编译 `.pyc` 和 `.py`。
- 比较两个代码对象的字节码。
- 运行行为验证。
- 批量处理目录。
- 输出 CFG 和异常作用域信息。

### 不能做什么

- 不恢复注释。
- 不保证源码文本与原始文件完全一致。
- 不执行反编译阶段的输入字节码。
- 不支持 PyPy、Python 2 或非 CPython 字节码。

---

## 2. 设计目标

### 2.1 精确性

每个被接受的输出都必须满足 [§6.1](#61-字节码恒等) 的恒等关系。接近正确仍然是失败。

### 2.2 结构化推理

反编译器只依赖：

- 异常表。
- 符号栈。
- 控制流图。
- 实际指令流。

不依赖变量名、常量值、源码行号或固定偏移。

### 2.3 跨版本稳定性

为 3.14 做的修改不能破坏 3.8–3.13。共享代码路径必须在所有版本上重新验证。

### 2.4 快速失败

遇到当前方言无法表示的指令或形状时，直接抛出明确错误，不输出占位符或错误语句。

---

## 3. 支持版本

| CPython | 魔数 | 头部 | 说明 |
|---|---:|---:|---|
| 3.8 | 3413 | 16 | 旧式块栈 |
| 3.9 | 3425 | 16 | 旧式块栈 |
| 3.10 | 3439 | 16 | 旧式块栈、模式匹配 |
| 3.11 | 3495 | 16 | 异常表、`BEFORE_WITH` |
| 3.12 | 3531 | 16 | 异常表细化 |
| 3.13 | 3571 | 16 | 融合局部变量、异常表 |
| 3.14 | 3627 | 16 | `LOAD_SPECIAL` with 协议 |
| 3.15 | 3666 | 16 | `LOAD_SPECIAL`、公共常量 |

所有版本的 `.pyc` 头部均为 16 字节。对于被截断或魔数被修改的文件，桥接器会在有限窗口内扫描 marshal 代码对象。

### 3.1 覆盖的语法

- 表达式、运算符、比较、布尔短路。
- 字面量、f-string、格式化、字符串拼接。
- 列表、元组、集合、字典、推导式。
- 切片、扩展切片、解包、星号目标。
- 赋值、增量赋值、链式赋值。
- `if`/`elif`/`else`、`while`、`for`、`else`。
- `break`、`continue`、`return`、`yield`、`yield from`。
- 函数、lambda、默认参数、关键字参数、位置参数。
- 类、元类、装饰器、描述符。
- 闭包、cell 变量、`nonlocal`、`global`。
- `try`/`except`/`else`/`finally`。
- `with`、`async with`、多项目 with。
- `async for`、异步生成器、协程。
- `match`/`case`、类模式、映射模式、OR 模式、守卫。
- 异常组和 `except*`。
- PEP 695 类型参数和类型别名。
- PEP 701 f-string 嵌套。

---

## 4. 工作流程

```text
+-----------+      +----------------+      +-------------------+
|  .pyc     | ---> | PythonLocator  | ---> |   PyBridge        |
|  头部     |      | 魔数 -> 3.x    |      |   Python 进程     |
+-----------+      +----------------+      +-------------------+
                                                    |
                                                    v
                                           +-------------------+
                                           | PythonCodeObject  |
                                           | 指令、常量、名称  |
                                           | 异常表            |
                                           +-------------------+
                                                    |
                                                    v
                                           +-------------------+
                                           |StructuralDecompiler|
                                           | 栈模拟、作用域    |
                                           | AST 构建          |
                                           +-------------------+
                                                    |
                                                    v
                                           +-------------------+
                                           |   PythonEmitter   |
                                           |   Python 源码     |
                                           +-------------------+
                                                    |
                              +---------------------+---------------------+
                              |                                           |
                              v                                           v
                    +-------------------+                     +-------------------+
                    | ExecutionVerifier |                     | BytecodeComparer  |
                    | 运行两个程序      |                     | 递归比较          |
                    +-------------------+                     +-------------------+
```

### 4.1 加载

`PythonLocator` 读取文件前 4 个字节。对于 `.pyc`，前 2 个字节是小端魔数。魔数映射到 CPython 版本，版本决定启动哪个解释器以及使用哪套指令方言。

### 4.2 桥接

桥接器是一个小型 Python 服务器，通过 stdin/stdout 交换 JSON。对于 `.pyc`：

1. 读取文件。
2. 找到 marshal 负载。
3. 反序列化代码对象。
4. 序列化为 JSON 模型。

对于源码：

1. 从 stdin 读取源码。
2. 用对应解释器编译。
3. 序列化生成的代码对象。

服务器在请求之间保持存活，避免每次调用都支付 Python 启动成本。

### 4.3 反编译

`StructuralDecompiler` 是核心。它使用符号栈和一组结构化解析器：

- 表达式解析器。
- 语句解析器。
- try 作用域解析器。
- 各版本的 with 解析器。
- async-with 解析器。
- async-for 解析器。
- match 解析器。
- 推导式解析器。
- 3.8–3.10 旧式块栈解析器。

解析器返回 AST 节点，`PythonEmitter` 将节点输出为源码。

### 4.4 验证

`ExecutionVerifier` 在独立进程中运行原始文件和反编译源码，比较退出码和 stdout。

`BytecodeComparer` 重新编译反编译源码，并递归比较代码对象。

两项检查相互独立：可能行为通过但字节码失败，也可能字节码通过但行为失败。

---

## 5. 核心数据模型

### 5.1 PythonCodeObject

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

### 5.2 PythonInstruction

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

### 5.3 ExceptionTableEntry

```csharp
public sealed record ExceptionTableEntry(
    int Start,
    int End,
    int Target,
    int Depth,
    bool LastI
);
```

### 5.4 AST

AST 定义在 `AST/AstNodes.cs`，包括表达式、语句、with 项、except 处理器、match 分支和模式。

---

## 6. 数学公式

### 6.1 字节码恒等

设：

- `P` 是 `.pyc` 文件。
- `S` 是源码文件。
- `Code(P)` 是 `P` 中的代码对象。
- `Compile(S)` 是用相同 CPython 版本编译 `S` 得到的代码对象。
- `Eq(a, b)` 是递归代码对象相等关系。

验收谓词：

```text
Identical(P, S) ⇔ Eq(Code(P), Compile(S))
```

递归关系：

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

### 6.2 指令归一化

忽略 `NOP`、`EXTENDED_ARG`、`NOT_TAKEN`：

```text
Norm(I) = ⟨ i ∈ I | i.opcode ∉ {NOP, EXTENDED_ARG, NOT_TAKEN} ⟩
```

### 6.3 魔数映射

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

### 6.4 栈效应

设指令前栈深度为 `S`，弹出 `pop(op)`，压入 `push(op)`：

```text
S' = S - pop(op) + push(op)
```

负深度是硬错误，解析器不会继续。

### 6.5 异常表覆盖

对于条目 `e = (start, end, target, depth, lasti)` 和程序计数器 `pc`：

```text
Covers(e, pc) ⇔ e.start ≤ pc < e.end
```

最内层条目是 `start` 最大的条目；相同 `start` 时取 `end` 最小者。

### 6.6 with 协议：3.8–3.10

使用块栈：

- `SETUP_WITH target`
- `POP_BLOCK`
- `WITH_CLEANUP_START`
- `WITH_CLEANUP_FINISH`
- `END_FINALLY`

块栈更新：

```text
B' =
  B ∪ {(SETUP_WITH, target)} on SETUP_WITH
  B ∖ {top(B)} on POP_BLOCK
```

解析器必须同时统计 `SETUP_WITH` 和 `SETUP_FINALLY`，否则嵌套 try/finally 会抢走 with 的 `POP_BLOCK`。

### 6.7 with 协议：3.11–3.13

```text
<上下文表达式>
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

正常清理是 `CALL 2` 序列，处理器是异常表指向的 `PUSH_EXC_INFO` 块。

### 6.8 with 协议：3.14+

```text
<上下文表达式>
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

`async with` 在 `CALL 0` 后插入：

```text
GET_AWAITABLE
LOAD_CONST None
SEND target
YIELD_VALUE
RESUME
JUMP_BACKWARD_NO_INTERRUPT
END_SEND
```

`__aexit__` 调用后还有第二段 await 序列。

### 6.9 with 处理器选择

设 `E` 为异常表条目集合，`o_body` 为第一条 body 指令偏移，`o_body_start` 为 body 范围起始偏移，`W` 为 with 处理器目标集合：

```text
W = { i | op(i) = PUSH_EXC_INFO ∧ op(i+1) = WITH_EXCEPT_START }
```

处理器：

```text
H_with = argmax { e.start | e ∈ E,
                   e.start ≤ o_body,
                   e.end ≥ o_body_start,
                   target(e) ∈ W }
```

取最大 `start` 是为了选择最内层条目，避免把内层 with 和外层 with 混淆。`end` 条件处理保护范围被碎片化的情况。

确定 `H_with` 后，cleanup 扫描被该处理器和解析器的 `endIdx` 限制。

### 6.10 cleanup 锚点

with body 中可能有多个 cleanup：正常路径一个，每个 `break`、`continue`、`return` 路径各一个。

设 `C` 为候选 cleanup 起始集合，`cont(H)` 为处理器 continuation：

```text
cont(H) =
  target(j)       若 POP_EXCEPT 后是跳转 j
  offset(next(H)) 否则
```

锚点 cleanup：

```text
c* = max { c ∈ C | next(c) = cont(H) }
```

如果没有匹配，则：

```text
c* = max C
```

多项目 with 取最后 `items.Count` 个 cleanup 组成的链。

### 6.11 嵌套 cleanup 谓词

cleanup 可能属于内层 with 或内层 try，不能作为外层 with 的 body 边界。

```text
nested(c) ⇔ ∃ e ∈ E:
target(e) ≤ H ∧ e.start ≤ c < e.end
```

`nested(c) = true` 的 cleanup 被跳过；只被祖先处理器（`target > H`）保护的 cleanup 保留。

边界情况：条目从嵌套处理器区域内部开始、但在区域外部结束，可能是 async-for 循环的尾部。因此只拒绝在嵌套区域内部结束的条目：

```text
reject(e, r) ⇔ e.start ≥ r.start ∧ e.start ≤ r.end ∧ e.end ≤ r.end
```

这样 `END_ASYNC_FOR` 尾部会保留在 body 中。

### 6.12 try body 范围

try 的保护范围可能被嵌套 with、async-for、嵌套 try 打碎。

设 `B_0 = o_body_start`，迭代：

```text
B[k+1] = max { e.end | target(e) = H_try, e.start ≤ B_k, e.end > B_k, ¬transition(e) }
```

当 `B_{k+1} = B_k` 时停止，固定点就是 body 结束位置。

`transition(e)` 标记处理器内部条目：`COPY/POP_EXCEPT/RERAISE` 块和嵌套处理器的正常退出转移。这些条目从 body 之后开始，不能扩展 body。

### 6.13 多项目 with 与融合

CPython 在多项目形式中融合存储和下一条局部加载：

```text
with a as x, b as y:
```

会编译成：

```text
STORE_FAST_LOAD_FAST x, b
```

桥接器拆分融合指令时，两半保留相同偏移。判别式：

```text
multi_item ⇔ offset(ctx_i) = offset(store_(i-1))
```

条件不成立时输出嵌套形式；没有融合时两种形式编译结果相同，因此选择是安全的。

### 6.14 内联退出 cleanup 语法

`break`、`continue`、`return` 前的内联 cleanup：

```text
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 3
POP_TOP^n
exit
```

`exit` 为：

```text
JUMP_FORWARD
JUMP_BACKWARD
JUMP_BACKWARD_NO_INTERRUPT
RETURN_VALUE
RETURN_CONST
LOAD_CONST None
RETURN_VALUE
```

解析器跳过 cleanup，让后续指令生成真正的 `BreakStmt`、`ContinueStmt` 或 `ReturnStmt`。

### 6.15 死 else 重建

在 `try/except/else/finally` 中，如果 try body 总是抛出异常，else body 不可达，但常量仍保留在 `co_consts`。

设 `UsedConsts` 为已输出 AST 引用的常量集合：

```text
E_dead = { c ∈ co_consts | c ∉ UsedConsts ∧ preservable(c) }
```

重建只适用于 3.10 和 3.11，并且只在 finally body 证明编译器保留了该子句常量时启用。

### 6.16 折叠常量来源

CPython 可能在编译期折叠常量表达式。折叠值存放在 `co_consts[0]`，而源码表达式可能仍以操作形式出现在字节码中。

折叠规则：

```text
fold(op, a, b) = eval(op, a, b)
```

只有在以下条件同时满足时输出折叠字面量：

1. 值与 `co_consts[0]` 匹配。
2. 类型完全匹配。
3. 操作数位置证明该值确实被折叠。

如果常量池顺序表明该值是后插入的，则输出表达式，让编译器再次折叠。这样不需要硬编码具体值。

### 6.17 复杂度

| 阶段 | 时间 | 空间 |
|---|---|---|
| 加载与序列化 | O(N + K) | O(N + K) |
| 作用域构建 | O(E log E) | O(E) |
| 栈模拟 | O(N) 摊还 | O(D) |
| AST 构建 | O(N) 摊还 | O(N) |
| 输出 | O(N) | O(N) |
| 递归比较 | O(N + K) | O(D) |

其中 `N` 为指令数，`E` 为异常表条目数，`K` 为常量数，`D` 为最大嵌套深度。

---

## 7. 架构

```text
Program.cs
pycrune.csproj
AST/AstNodes.cs
Analysis/SyntheticCleanupPass.cs
CFG/BasicBlock.cs
CFG/ControlFlowGraph.cs
CFG/DominatorTree.cs
CFG/ExceptionScope.cs
CFG/LoopAnalysis.cs
Core/PyBridge.cs
Core/PythonLocator.cs
Decompiler/PythonEmitter.cs
Decompiler/StructuralDecompiler.cs
Decompiler/SymbolicStack.cs
Model/ExceptionTableEntry.cs
Model/FailFastException.cs
Model/FrozenSetValue.cs
Model/OpCodes.cs
Model/PythonCodeObject.cs
Model/PythonInstruction.cs
Model/SliceValue.cs
Verification/BytecodeComparer.cs
Verification/ExecutionVerifier.cs
```

| 文件 | 职责 |
|---|---|
| `Program.cs` | CLI、命令、退出码 |
| `PythonLocator.cs` | 魔数到版本、解释器选择 |
| `PyBridge.cs` | Python 服务器、JSON 协议、序列化 |
| `StructuralDecompiler.cs` | 栈模拟、作用域、AST |
| `PythonEmitter.cs` | AST 到源码 |
| `SymbolicStack.cs` | 抽象栈值 |
| `AstNodes.cs` | AST 记录 |
| `ControlFlowGraph.cs` | 基本块和边 |
| `DominatorTree.cs` | 支配关系 |
| `ExceptionScope.cs` | 异常区域分析 |
| `SyntheticCleanupPass.cs` | 合成清理移除 |
| `BytecodeComparer.cs` | 递归代码对象比较 |
| `ExecutionVerifier.cs` | 行为比较 |

---

## 8. 环境要求

- .NET 10 SDK。
- 每个目标版本对应的 CPython 解释器。
- Windows、Linux 或 macOS，x64 或 ARM64。
- 构建至少 200 MB 磁盘空间。

| 版本 | Windows | Unix |
|---|---|---|
| 3.8 | `py -3.8` | `python3.8` |
| 3.9 | `py -3.9` | `python3.9` |
| 3.10 | `py -3.10` | `python3.10` |
| 3.11 | `py -3.11` 或内嵌 | `python3.11` |
| 3.12 | `python` | `python` |
| 3.13 | `py -3.13` | `python3.13` |
| 3.14 | `py -3.14` | `python3.14` |
| 3.15 | `py -3.15` | `python3.15` |

当前解析器在 Windows 上使用 `py` 启动器，3.12 使用 `python`。Linux/macOS 需要创建 `py` 包装脚本，见 §9.2。

---

## 9. 全平台安装

### 9.1 Windows 10/11 x64

安装 .NET 10：

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
dotnet --version
```

安装 Python：

```powershell
winget install --id Python.Python.3.8 -e
winget install --id Python.Python.3.9 -e
winget install --id Python.Python.3.10 -e
winget install --id Python.Python.3.11 -e
winget install --id Python.Python.3.12 -e
winget install --id Python.Python.3.13 -e
```

3.14 和 3.15 使用 python.org 安装包或 pyenv-win。

pyenv-win：

```powershell
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/pyenv-win/pyenv-win/master/pyenv-win/install-pyenv-win.ps1" -OutFile "./install-pyenv-win.ps1"
./install-pyenv-win.ps1
pyenv install 3.8.20
pyenv install 3.12.8
pyenv install 3.14.0
pyenv install 3.15.0
```

验证：

```powershell
py -0p
py -3.14 -c "import sys; print(sys.version)"
```

构建：

```powershell
dotnet build pycrune.csproj -c Release
.\bin\Release\net10.0\pycrune.exe decompile input.pyc -o output.py --verify
```

### 9.2 Unix `py` 包装脚本

创建 `/usr/local/bin/py`：

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

验证：

```bash
py -3.12 -c "import sys; print(sys.version)"
python --version
```

`python` 应指向 3.12，因为解析器对 3.12 使用 `python`。

### 9.3 Ubuntu / Debian

```bash
sudo apt-get update
sudo apt-get install -y curl git build-essential libssl-dev zlib1g-dev \
  libbz2-dev libreadline-dev libsqlite3-dev libffi-dev liblzma-dev \
  libncursesw5-dev tk-dev xz-utils
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
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

创建 `py` 包装脚本后构建。

### 9.4 Fedora / RHEL

```bash
sudo dnf install -y git curl gcc make openssl-devel zlib-devel \
  bzip2-devel readline-devel sqlite-devel libffi-devel xz-devel \
  ncurses-devel tk-devel
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

Python 版本使用 pyenv，步骤同 Ubuntu。

### 9.5 Arch Linux

```bash
sudo pacman -S --needed git base-devel openssl zlib bzip2 readline \
  sqlite libffi xz ncurses tk curl
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
curl -sSL https://pyenv.run | bash
export PATH="$HOME/.pyenv/bin:$PATH"
eval "$(pyenv init -)"
```

### 9.6 macOS Intel / Apple Silicon

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
brew install --cask dotnet-sdk
brew install pyenv
export PATH="$HOME/.pyenv/bin:$PATH"
eval "$(pyenv init -)"
pyenv install 3.8.20
pyenv install 3.12.8
pyenv install 3.14.0
pyenv install 3.15.0
```

Apple Silicon 使用 ARM64 构建。如果 3.8 构建失败，可使用 Rosetta 或容器。

### 9.7 Docker

单版本镜像：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY . .
RUN dotnet publish pycrune.csproj -c Release -r linux-x64 --self-contained true -o /out

FROM python:3.12-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates libicu72 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
COPY docker/py /usr/local/bin/py
RUN chmod +x /usr/local/bin/py
ENTRYPOINT ["/app/pycrune"]
```

构建和运行：

```bash
docker build -t pycrune:3.12 .
docker run --rm -v "$PWD:/work" pycrune:3.12 decompile /work/input.pyc -o /work/output.py --verify
```

完整矩阵镜像需要 pyenv 安装 8 个解释器，体积大、构建慢，建议在 CI 中缓存。

### 9.8 GitHub Actions

```yaml
name: build
on: [push, pull_request]
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

多版本矩阵：

```yaml
strategy:
  fail-fast: false
  matrix:
    python: ["3.8", "3.9", "3.10", "3.11", "3.12", "3.13", "3.14", "3.15"]
```

如果 `setup-python` 没有最新版本，使用 pyenv。

### 9.9 ARM64 / 树莓派

使用 64 位系统。安装 .NET 10 和 pyenv 的步骤与 Ubuntu 相同。内存有限的设备逐个安装 Python，并使用 `MAKE_OPTS="-j2"`。

### 9.10 Android / Termux

.NET 10 没有官方 Termux 目标。可使用：

1. 在 PC 上运行，复制输出。
2. `proot-distro` 安装 Ubuntu，再按 Ubuntu 步骤操作。
3. 远程 Docker + SSH。

### 9.11 IDE

- VS Code：安装 C# Dev Kit，打开仓库。
- Visual Studio：打开 `pycrune.csproj`。
- Rider：打开仓库，选择 `pycrune` 项目。

---

## 10. 构建

```bash
dotnet build pycrune.csproj -c Release
```

输出：

```text
bin/Release/net10.0/pycrune
bin/Release/net10.0/pycrune.exe
```

自包含发布：

```bash
dotnet publish pycrune.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

可用 RID：`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。

---

## 11. CLI 参考

```text
pycrune <command> [arguments]
```

| 命令 | 用途 |
|---|---|
| `decompile` | 反编译 `.pyc` 或 `.py` |
| `verify` | 验证反编译源码 |
| `compare` | 只比较字节码 |
| `batch` | 批量反编译目录 |
| `inspect` | 输出 CFG 和异常作用域 |

### decompile

```text
pycrune decompile <path> [-o <out.py>] [--verify]
```

示例：

```bash
pycrune decompile module.pyc
pycrune decompile module.pyc -o module.py
pycrune decompile module.pyc -o module.py --verify
```

退出码：

| 码 | 含义 |
|---:|---|
| 0 | 成功 |
| 1 | 用法错误或验证失败 |
| 2 | 严重错误 |

### verify

```bash
pycrune verify input.pyc output.py
```

运行两个程序并比较退出码、stdout，然后比较字节码。

### compare

```bash
pycrune compare input.pyc output.py
```

只比较字节码，不执行程序。

### batch

```bash
pycrune batch ../test
```

递归处理目录中的 `.pyc`。

### inspect

```bash
pycrune inspect input.pyc
```

输出基本块数量、异常作用域数量，以及每个作用域的起止偏移、处理器数量、finally 标志和退出块。

---

## 12. 验证

| 检查 | 运行内容 | 证明 |
|---|---|---|
| 行为 | 原始程序和反编译程序 | 退出码和 stdout 相同 |
| 字节码 | 重新编译反编译源码 | 代码对象相同 |

成功输出：

```text
[PASS] 100% BEHAVIORAL FIDELITY CONFIRMED (ExitCode and Output matched identically)
[PASS] Bytecode identical: 268 instructions matched with 0 differences!
```

字节码失败：

```text
[!] Bytecode Differences: 12 discrepancies found between 336 orig and 324 decompiled instructions.
```

先修复最早的差异，后面的差异通常是连锁反应。

---

## 13. 解释器解析

默认规则：

1. 非 `.pyc` 使用 `ResolveVersion`。
2. 读取前两字节小端魔数。
3. 查表得到版本。
4. 3.11 优先使用 `multiver/python311-embed/python.exe`。
5. 3.12 使用 `python`。
6. 其他版本使用 `py -X.Y`。
7. 未知魔数使用 `python`。

自定义路径需要修改 `Core/PythonLocator.cs`，然后重新构建。

---

## 14. 故障排查

### `py` 找不到

Windows 安装 Python Launcher；Unix 创建 §9.2 的包装脚本。

### `py -3.14` 找不到

版本没有注册到启动器。安装后运行 `py -0p` 检查。

### 未知魔数

检查头部：

```bash
python -c "print(open('input.pyc','rb').read(4).hex())"
```

对照 §15.3 的魔数表。

### 找不到 marshal 代码对象

文件可能被截断、头部大小不是 16、负载被压缩或加密。桥接器会做有限扫描，失败则不支持。

### 栈下溢

这是反编译器缺陷。记录文件和指令索引，用 `inspect` 复现。

### 行为不同但字节码相同

程序可能依赖路径、`__file__`、stdin、随机数、时间或网络。让两个程序在相同条件下运行；确定性检查使用 `compare`。

### 字节码不同但行为相同

行为等价但字节码不恒等，仍然失败。检查最早的差异并修复解析器或输出器。

### 构建缺少 SDK

安装 .NET 10 SDK，检查 `dotnet --list-sdks`。多个 SDK 时添加 `global.json`。

### Linux 缺少原生库

安装 `libssl-dev`、`zlib1g-dev`、`libffi-dev`、`liblzma-dev`。

### 路径包含空格

给所有路径加引号。

---

## 15. 附录

### 15.1 with 协议摘要

| 版本 | 协议 |
|---|---|
| 3.8–3.10 | `SETUP_WITH`、`POP_BLOCK`、`WITH_CLEANUP_*` |
| 3.11–3.13 | `BEFORE_WITH`、`CALL 2` |
| 3.14–3.15 | `COPY 1; LOAD_SPECIAL __exit__; SWAP 2; SWAP 3; LOAD_SPECIAL __enter__; CALL 0` |

### 15.2 异常表字段

| 字段 | 含义 |
|---|---|
| `start` | 保护范围起始 |
| `end` | 保护范围结束（不含） |
| `target` | 处理器偏移 |
| `depth` | 处理器栈深度 |
| `lasti` | 是否压入最后指令偏移 |

### 15.3 魔数

| 魔数 | 版本 |
|---:|---|
| 3413 | 3.8 |
| 3425 | 3.9 |
| 3439 | 3.10 |
| 3495 | 3.11 |
| 3531 | 3.12 |
| 3571 | 3.13 |
| 3627 | 3.14 |
| 3666 | 3.15 |

### 15.4 术语

| 术语 | 含义 |
|---|---|
| 代码对象 | CPython 编译后的模块、函数、类或推导式表示 |
| 字节码 | 代码对象中的指令序列 |
| Marshal | CPython 代码对象二进制序列化格式 |
| 魔数 | `.pyc` 开头的两字节版本标识 |
| 常量池 | `co_consts` |
| 名称表 | `co_names` |
| 局部变量 | `co_varnames` |
| 自由变量 | 从外层作用域捕获的变量 |
| 单元格变量 | 用于闭包访问的 cell 变量 |
| 异常表 | 3.11+ 的保护范围和处理器目标 |
| 块栈 | 3.8–3.10 的旧式保护块栈 |
| 符号栈 | 反编译器使用的抽象值栈 |
| 处理器 | 处理异常的代码 |
| 清理 | 正常路径调用 `__exit__` 或 `__aexit__` 的代码 |
| 续点 | 处理器结束后控制返回的位置 |
| 融合 | 如 `STORE_FAST_LOAD_FAST` 的组合指令 |
| 锚点清理 | 被选为 body 边界的清理 |
| 嵌套清理 | 属于内层结构的清理 |
| 转移条目 | 覆盖处理器内部代码的异常表条目 |
| 死 else | 不可达但常量仍保留的 else 体 |
| 折叠常量 | 编译期求值的常量表达式 |

---
