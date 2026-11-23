# PycRune
Hngocuyennn


**PycRune — Decompiler bytecode Python cho CPython 3.8 – 3.15**

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4">
  <img alt="CPython 3.8-3.15" src="https://img.shields.io/badge/CPython-3.8%20%E2%80%93%203.15-3776AB">
  <img alt="Platforms" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey">
</p>

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README.zh.md">中文</a> ·
  <a href="README.vi.md"><b>Tiếng Việt</b></a>
</p>

PycRune đọc tệp `.pyc`, dựng lại mã nguồn Python và chứng minh kết quả bằng cách biên dịch lại mã nguồn đó với đúng phiên bản CPython, sau đó so sánh code object đó tương tự với cách verify của Pylingual. Với tiêu chí là bytecode identity, không phải “giống” trên nghĩa của source.

## Bắt đầu nhanh

```bash
dotnet build pycrune.csproj -c Release
./bin/Release/net10.0/pycrune decompile input.pyc -o output.py --verify
```

Windows:

```powershell
.\bin\Release\net10.0\pycrune.exe decompile input.pyc -o output.py --verify
```

Yêu cầu: .NET 10 SDK và interpreter CPython tương ứng. Xem [Cài đặt trên mọi nền tảng](#9-cài-đặt-trên-mọi-nền-tảng).

## Mục lục

1. [Giới thiệu](#1-giới-thiệu)
2. [Mục tiêu thiết kế](#2-mục-tiêu-thiết-kế)
3. [Phiên bản hỗ trợ](#3-phiên-bản-hỗ-trợ)
4. [Quy trình hoạt động](#4-quy-trình-hoạt-động)
5. [Mô hình dữ liệu](#5-mô-hình-dữ-liệu)
6. [Công thức toán học](#6-công-thức-toán-học)
7. [Kiến trúc](#7-kiến-trúc)
8. [Yêu cầu môi trường](#8-yêu-cầu-môi-trường)
9. [Cài đặt trên mọi nền tảng](#9-cài-đặt-trên-mọi-nền-tảng)
10. [Build](#10-build)
11. [CLI](#11-cli)
12. [Kiểm chứng](#12-kiểm-chứng)
13. [Chọn interpreter](#13-chọn-interpreter)
14. [Xử lý sự cố](#14-xử-lý-sự-cố)
15. [Phụ lục](#15-phụ-lục)

---

## 1. Giới thiệu

PycRune là decompiler Python viết bằng C#/.NET 10, hỗ trợ CPython 3.8 đến 3.15. Mục tiêu duy nhất là: mã nguồn dựng lại, khi biên dịch bằng đúng phiên bản CPython, phải tạo ra code object tương đương với bản gốc ở mọi trường mà CPython công bố.

Phạm vi so sánh:

- Chuỗi instruction sau khi chuẩn hóa.
- Opcode và toán hạng.
- Thứ tự, kiểu và giá trị của hằng số.
- Tên, varnames, freevars, cellvars.
- Bảng exception.
- Toàn bộ code object lồng nhau, đệ quy.

### Làm được gì

- Decompile `.pyc` và `.py`.
- So sánh bytecode của hai code object.
- Kiểm tra hành vi thực thi.
- Xử lý hàng loạt theo thư mục.
- In CFG và exception scope.

### Không làm gì

- Không khôi phục comment.
- Không đảm bảo mã nguồn giống hệt từng ký tự.
- Không thực thi bytecode trong giai đoạn decompile.
- Không hỗ trợ PyPy, Python 2 hay bytecode không phải CPython.

---

## 2. Mục tiêu thiết kế

### 2.1 Chính xác tuyệt đối

Mọi kết quả được chấp nhận phải thỏa mãn quan hệ identity ở [§6.1](#61-bytecode-identity). “Gần đúng” vẫn là thất bại.

### 2.2 Suy luận theo cấu trúc

Decompiler chỉ dựa vào:

- Bảng exception.
- Ngăn xếp tượng trưng.
- Đồ thị luồng điều khiển.
- Chuỗi instruction thực tế.

Không dựa vào tên biến, giá trị hằng số, số dòng hay offset cố định.

### 2.3 Ổn định chéo phiên bản

Thay đổi cho 3.14 không được làm hỏng 3.8–3.13. Mọi đường code dùng chung phải được kiểm tra lại trên toàn bộ phiên bản.

### 2.4 Fail fast

Khi gặp opcode hoặc hình dạng mà dialect hiện tại không biểu diễn được, chương trình báo lỗi rõ ràng. Không tự động phát ra placeholder hay câu lệnh sai.

---

## 3. Phiên bản hỗ trợ

| CPython | Magic | Header | Ghi chú |
|---|---:|---:|---|
| 3.8 | 3413 | 16 | Block stack cũ |
| 3.9 | 3425 | 16 | Block stack cũ |
| 3.10 | 3439 | 16 | Block stack cũ, pattern matching |
| 3.11 | 3495 | 16 | Exception table, `BEFORE_WITH` |
| 3.12 | 3531 | 16 | Tinh chỉnh exception table |
| 3.13 | 3571 | 16 | Fused locals, exception table |
| 3.14 | 3627 | 16 | `LOAD_SPECIAL` with protocol |
| 3.15 | 3666 | 16 | `LOAD_SPECIAL`, common constants |

Tất cả phiên bản dùng header 16 byte. Với tệp bị cắt header hoặc patch magic, bridge sẽ quét có giới hạn để tìm marshal code object.

### 3.1 Cú pháp được hỗ trợ

- Biểu thức, toán tử, so sánh, boolean short-circuit.
- Literal, f-string, định dạng, nối chuỗi.
- List, tuple, set, dict, comprehension.
- Slice, extended slice, unpacking, starred target.
- Gán, gán tăng, gán chuỗi.
- `if`/`elif`/`else`, `while`, `for`, `else`.
- `break`, `continue`, `return`, `yield`, `yield from`.
- Function, lambda, default, keyword-only, positional-only.
- Class, metaclass, decorator, descriptor.
- Closure, cell variable, `nonlocal`, `global`.
- `try`/`except`/`else`/`finally`.
- `with`, `async with`, multi-item with.
- `async for`, async generator, coroutine.
- `match`/`case`, class pattern, mapping pattern, OR pattern, guard.
- Exception group và `except*`.
- PEP 695 type parameter, type alias.
- PEP 701 f-string lồng nhau.

---

## 4. Quy trình hoạt động

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

### 4.1 Nạp tệp

`PythonLocator` đọc 4 byte đầu. Với `.pyc`, 2 byte đầu là magic little-endian. Magic ánh xạ sang phiên bản CPython; phiên bản quyết định interpreter và dialect instruction.

### 4.2 Bridge

Bridge là một Python server nhỏ, trao đổi JSON qua stdin/stdout. Với `.pyc`:

1. Đọc tệp.
2. Tìm marshal payload.
3. Unmarshal code object.
4. Serialize thành JSON.

Với source:

1. Đọc source từ stdin.
2. Compile bằng interpreter tương ứng.
3. Serialize code object.

Server sống giữa các request, loại bỏ chi phí khởi động Python trong hot path.

### 4.3 Decompile

`StructuralDecompiler` là lõi. Nó dùng ngăn xếp tượng trưng và các parser cấu trúc:

- Parser biểu thức.
- Parser câu lệnh.
- Parser try scope.
- Parser with cho từng dialect.
- Parser async-with.
- Parser async-for.
- Parser match.
- Parser comprehension.
- Parser legacy block stack cho 3.8–3.10.

Parser trả về AST; `PythonEmitter` phát ra source.

### 4.4 Kiểm chứng

`ExecutionVerifier` chạy bản gốc và bản decompile trong tiến trình riêng, so exit code và stdout.

`BytecodeComparer` biên dịch lại source và so code object đệ quy.

Hai kiểm tra độc lập: có thể pass behavior nhưng fail bytecode, hoặc ngược lại.

---

## 5. Mô hình dữ liệu

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

AST nằm trong `AST/AstNodes.cs`, gồm expression, statement, with item, except handler, match case và pattern.

---

## 6. Công thức toán học

### 6.1 Bytecode identity

Cho:

- `P` là tệp `.pyc`.
- `S` là tệp source.
- `Code(P)` là code object trong `P`.
- `Compile(S)` là code object khi biên dịch `S` bằng cùng phiên bản CPython.
- `Eq(a, b)` là quan hệ bằng nhau đệ quy của code object.

Điều kiện nghiệm thu:

```text
Identical(P, S) ⇔ Eq(Code(P), Compile(S))
```

Quan hệ đệ quy:

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

### 6.2 Chuẩn hóa instruction

Bỏ qua `NOP`, `EXTENDED_ARG`, `NOT_TAKEN`:

```text
Norm(I) = ⟨ i ∈ I | i.opcode ∉ {NOP, EXTENDED_ARG, NOT_TAKEN} ⟩
```

### 6.3 Ánh xạ magic

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

### 6.4 Hiệu ứng ngăn xếp

Với độ sâu `S` trước instruction, `pop(op)` giá trị bị lấy ra, `push(op)` giá trị được đẩy vào:

```text
S' = S - pop(op) + push(op)
```

Độ sâu âm là lỗi cứng; parser không tiếp tục.

### 6.5 Độ phủ exception table

Với entry `e = (start, end, target, depth, lasti)` và program counter `pc`:

```text
Covers(e, pc) ⇔ e.start ≤ pc < e.end
```

Entry trong cùng là entry có `start` lớn nhất; nếu bằng nhau, chọn `end` nhỏ nhất.

### 6.6 With protocol 3.8–3.10

Dùng block stack:

- `SETUP_WITH target`
- `POP_BLOCK`
- `WITH_CLEANUP_START`
- `WITH_CLEANUP_FINISH`
- `END_FINALLY`

Cập nhật block stack:

```text
B' =
  B ∪ {(SETUP_WITH, target)} on SETUP_WITH
  B ∖ {top(B)} on POP_BLOCK
```

Parser phải đếm cả `SETUP_WITH` và `SETUP_FINALLY`, nếu không try/finally lồng sẽ lấy mất `POP_BLOCK` của with.

### 6.7 With protocol 3.11–3.13

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

Cleanup bình thường là chuỗi `CALL 2`; handler là block `PUSH_EXC_INFO` do exception table trỏ tới.

### 6.8 With protocol 3.14+

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

`async with` chèn chuỗi await sau `CALL 0`:

```text
GET_AWAITABLE
LOAD_CONST None
SEND target
YIELD_VALUE
RESUME
JUMP_BACKWARD_NO_INTERRUPT
END_SEND
```

Sau `__aexit__` có chuỗi await thứ hai.

### 6.9 Chọn handler của with

Cho `E` là tập entry exception table, `o_body` là offset instruction đầu tiên của body, `o_body_start` là offset bắt đầu vùng body, `W` là tập handler with:

```text
W = { i | op(i) = PUSH_EXC_INFO ∧ op(i+1) = WITH_EXCEPT_START }
```

Handler:

```text
H_with = argmax { e.start | e ∈ E,
                   e.start ≤ o_body,
                   e.end ≥ o_body_start,
                   target(e) ∈ W }
```

Lấy `start` lớn nhất để chọn entry trong cùng, tránh nhầm with trong với with ngoài. Điều kiện `end` xử lý vùng bảo vệ bị phân mảnh.

Sau khi có `H_with`, phạm vi quét cleanup bị chặn bởi handler và `endIdx` của parser.

### 6.10 Neo cleanup

Body with có thể có nhiều cleanup: một cho nhánh bình thường, một cho mỗi nhánh `break`, `continue`, `return`.

Cho `C` là tập cleanup ứng viên, `cont(H)` là continuation của handler:

```text
cont(H) =
  target(j)       nếu sau POP_EXCEPT là jump j
  offset(next(H)) ngược lại
```

Cleanup neo:

```text
c* = max { c ∈ C | next(c) = cont(H) }
```

Nếu không có, fallback:

```text
c* = max C
```

Với multi-item with, lấy chuỗi `items.Count` cleanup cuối cùng.

### 6.11 Vị từ cleanup lồng nhau

Cleanup có thể thuộc with trong hoặc try trong, không được dùng làm biên body của with ngoài.

```text
nested(c) ⇔ ∃ e ∈ E:
target(e) ≤ H ∧ e.start ≤ c < e.end
```

Cleanup có `nested(c) = true` bị bỏ qua; cleanup chỉ được bảo vệ bởi handler tổ tiên (`target > H`) được giữ.

Trường hợp biên: entry bắt đầu trong vùng handler lồng nhưng kết thúc ngoài vùng đó có thể là đuôi async-for. Vì vậy chỉ loại entry kết thúc bên trong vùng lồng:

```text
reject(e, r) ⇔ e.start ≥ r.start ∧ e.start ≤ r.end ∧ e.end ≤ r.end
```

Nhờ vậy đuôi `END_ASYNC_FOR` vẫn thuộc body.

### 6.12 Phạm vi try body

Vùng bảo vệ của try có thể bị with, async-for, try lồng làm phân mảnh.

Đặt `B_0 = o_body_start`, lặp:

```text
B[k+1] = max { e.end | target(e) = H_try, e.start ≤ B_k, e.end > B_k, ¬transition(e) }
```

Dừng khi `B_{k+1} = B_k`; điểm cố định là cuối body.

`transition(e)` đánh dấu entry nội bộ handler: block `COPY/POP_EXCEPT/RERAISE` và transition thoát bình thường của handler lồng. Các entry này bắt đầu sau body và không được mở rộng body.

### 6.13 Multi-item with và fusion

CPython fuse store và load local kế tiếp trong dạng multi-item:

```text
with a as x, b as y:
```

biên dịch thành:

```text
STORE_FAST_LOAD_FAST x, b
```

Khi bridge tách instruction fuse, hai nửa giữ cùng offset. Điều kiện nhận diện:

```text
multi_item ⇔ offset(ctx_i) = offset(store_(i-1))
```

Nếu sai, phát ra dạng nested. Khi không có fusion, hai dạng biên dịch giống nhau nên lựa chọn an toàn.

### 6.14 Ngữ pháp cleanup inline

Cleanup inline trước `break`, `continue`, `return`:

```text
LOAD_CONST None
LOAD_CONST None
LOAD_CONST None
CALL 3
POP_TOP^n
exit
```

`exit` là một trong:

```text
JUMP_FORWARD
JUMP_BACKWARD
JUMP_BACKWARD_NO_INTERRUPT
RETURN_VALUE
RETURN_CONST
LOAD_CONST None
RETURN_VALUE
```

Parser bỏ qua cleanup, để instruction sau sinh ra `BreakStmt`, `ContinueStmt` hoặc `ReturnStmt` thật.

### 6.15 Tái tạo dead else

Trong `try/except/else/finally` mà body try luôn raise, else không chạy được nhưng hằng số vẫn nằm trong `co_consts`.

Cho `UsedConsts` là tập hằng số được AST tham chiếu:

```text
E_dead = { c ∈ co_consts | c ∉ UsedConsts ∧ preservable(c) }
```

Chỉ áp dụng cho 3.10 và 3.11, và chỉ khi finally body chứng minh compiler giữ hằng số của nhánh.

### 6.16 Nguồn gốc hằng số đã gấp

CPython có thể gấp biểu thức hằng lúc compile. Giá trị gấp nằm ở `co_consts[0]`, trong khi biểu thức nguồn có thể vẫn xuất hiện dưới dạng phép toán trong bytecode.

Quy tắc:

```text
fold(op, a, b) = eval(op, a, b)
```

Chỉ phát literal gấp khi:

1. Giá trị khớp `co_consts[0]`.
2. Kiểu khớp chính xác.
3. Vị trí toán hạng chứng minh giá trị được gấp.

Nếu thứ tự constant pool cho thấy giá trị được chèn muộn, phát biểu thức để compiler gấp lại. Cách này giữ thứ tự hằng số mà không hardcode giá trị.

### 6.17 Độ phức tạp

| Pha | Thời gian | Bộ nhớ |
|---|---|---|
| Load và serialize | O(N + K) | O(N + K) |
| Xây scope | O(E log E) | O(E) |
| Mô phỏng ngăn xếp | O(N) amortized | O(D) |
| Xây AST | O(N) amortized | O(N) |
| Emit | O(N) | O(N) |
| So sánh đệ quy | O(N + K) | O(D) |

Trong đó `N` là số instruction, `E` là số entry exception table, `K` là số hằng số, `D` là độ sâu lồng tối đa.

---

## 7. Kiến trúc

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

| Tệp | Trách nhiệm |
|---|---|
| `Program.cs` | CLI, command, exit code |
| `PythonLocator.cs` | Magic → version, chọn interpreter |
| `PyBridge.cs` | Python server, JSON, serialize |
| `StructuralDecompiler.cs` | Stack, scope, AST |
| `PythonEmitter.cs` | AST → source |
| `SymbolicStack.cs` | Giá trị ngăn xếp trừu tượng |
| `AstNodes.cs` | Record AST |
| `ControlFlowGraph.cs` | Basic block và cạnh |
| `DominatorTree.cs` | Quan hệ dominance |
| `ExceptionScope.cs` | Phân tích vùng exception |
| `SyntheticCleanupPass.cs` | Loại cleanup tổng hợp |
| `BytecodeComparer.cs` | So code object đệ quy |
| `ExecutionVerifier.cs` | So hành vi |

---

## 8. Yêu cầu môi trường

- .NET 10 SDK.
- Interpreter CPython tương ứng cho từng phiên bản.
- Windows, Linux hoặc macOS, x64 hoặc ARM64.
- Tối thiểu 200 MB đĩa cho build.

| Phiên bản | Windows | Unix |
|---|---|---|
| 3.8 | `py -3.8` | `python3.8` |
| 3.9 | `py -3.9` | `python3.9` |
| 3.10 | `py -3.10` | `python3.10` |
| 3.11 | `py -3.11` hoặc embedded | `python3.11` |
| 3.12 | `python` | `python` |
| 3.13 | `py -3.13` | `python3.13` |
| 3.14 | `py -3.14` | `python3.14` |
| 3.15 | `py -3.15` | `python3.15` |

Resolver hiện tại dùng `py` trên Windows và `python` cho 3.12. Linux/macOS cần shim `py`, xem §9.2.

---

## 9. Cài đặt trên mọi nền tảng

### 9.1 Windows 10/11 x64

Cài .NET 10:

```powershell
winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements
dotnet --version
```

Cài Python:

```powershell
winget install --id Python.Python.3.8 -e
winget install --id Python.Python.3.9 -e
winget install --id Python.Python.3.10 -e
winget install --id Python.Python.3.11 -e
winget install --id Python.Python.3.12 -e
winget install --id Python.Python.3.13 -e
```

3.14 và 3.15 dùng installer python.org hoặc pyenv-win.

pyenv-win:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/pyenv-win/pyenv-win/master/pyenv-win/install-pyenv-win.ps1" -OutFile "./install-pyenv-win.ps1"
./install-pyenv-win.ps1
pyenv install 3.8.20
pyenv install 3.12.8
pyenv install 3.14.0
pyenv install 3.15.0
```

Kiểm tra:

```powershell
py -0p
py -3.14 -c "import sys; print(sys.version)"
```

Build:

```powershell
dotnet build pycrune.csproj -c Release
.\bin\Release\net10.0\pycrune.exe decompile input.pyc -o output.py --verify
```

### 9.2 Shim `py` cho Unix

Tạo `/usr/local/bin/py`:

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

Kiểm tra:

```bash
py -3.12 -c "import sys; print(sys.version)"
python --version
```

`python` phải trỏ tới 3.12 vì resolver dùng `python` cho 3.12.

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

Tạo shim `py` rồi build.

### 9.4 Fedora / RHEL

```bash
sudo dnf install -y git curl gcc make openssl-devel zlib-devel \
  bzip2-devel readline-devel sqlite-devel libffi-devel xz-devel \
  ncurses-devel tk-devel
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

Python dùng pyenv, các bước như Ubuntu.

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

Apple Silicon dùng build ARM64. Nếu 3.8 build lỗi, dùng Rosetta hoặc container.

### 9.7 Docker

Image một phiên bản:

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

Build và chạy:

```bash
docker build -t pycrune:3.12 .
docker run --rm -v "$PWD:/work" pycrune:3.12 decompile /work/input.pyc -o /work/output.py --verify
```

Image full matrix cần pyenv cài 8 interpreter, dung lượng lớn và build lâu; nên cache trong CI.

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

Matrix nhiều phiên bản:

```yaml
strategy:
  fail-fast: false
  matrix:
    python: ["3.8", "3.9", "3.10", "3.11", "3.12", "3.13", "3.14", "3.15"]
```

Nếu `setup-python` chưa có bản mới nhất, dùng pyenv.

### 9.9 ARM64 / Raspberry Pi

Dùng hệ điều hành 64-bit. Cài .NET 10 và pyenv như Ubuntu. Với máy ít RAM, cài từng phiên bản Python và dùng `MAKE_OPTS="-j2"`.

### 9.10 Android / Termux

.NET 10 không có target Termux chính thức. Có thể:

1. Chạy trên PC rồi copy output.
2. Dùng `proot-distro` cài Ubuntu, làm theo hướng dẫn Ubuntu.
3. Chạy Docker từ xa và SSH.

### 9.11 IDE

- VS Code: cài C# Dev Kit, mở repo.
- Visual Studio: mở `pycrune.csproj`.
- Rider: mở repo, chọn project `pycrune`.

---

## 10. Build

```bash
dotnet build pycrune.csproj -c Release
```

Kết quả:

```text
bin/Release/net10.0/pycrune
bin/Release/net10.0/pycrune.exe
```

Self-contained:

```bash
dotnet publish pycrune.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

RID hỗ trợ: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

---

## 11. CLI

```text
pycrune <command> [arguments]
```

| Lệnh | Mục đích |
|---|---|
| `decompile` | Decompile `.pyc` hoặc `.py` |
| `verify` | Kiểm chứng source đã decompile |
| `compare` | Chỉ so bytecode |
| `batch` | Decompile cả thư mục |
| `inspect` | In CFG và exception scope |

### decompile

```text
pycrune decompile <path> [-o <out.py>] [--verify]
```

Ví dụ:

```bash
pycrune decompile module.pyc
pycrune decompile module.pyc -o module.py
pycrune decompile module.pyc -o module.py --verify
```

Exit code:

| Mã | Ý nghĩa |
|---:|---|
| 0 | Thành công |
| 1 | Lỗi usage hoặc verification mismatch |
| 2 | Lỗi nghiêm trọng |

### verify

```bash
pycrune verify input.pyc output.py
```

Chạy hai chương trình, so exit code và stdout, sau đó so bytecode.

### compare

```bash
pycrune compare input.pyc output.py
```

Chỉ so bytecode, không chạy chương trình.

### batch

```bash
pycrune batch ../test
```

Xử lý đệ quy mọi `.pyc` trong thư mục.

### inspect

```bash
pycrune inspect input.pyc
```

In số basic block, số exception scope, offset start/end, số handler, cờ finally và exit block.

---

## 12. Kiểm chứng

| Kiểm tra | Chạy gì | Chứng minh |
|---|---|---|
| Hành vi | Bản gốc và bản decompile | Exit code và stdout giống nhau |
| Bytecode | Biên dịch lại source | Code object giống nhau |

Thành công:

```text
[PASS] 100% BEHAVIORAL FIDELITY CONFIRMED (ExitCode and Output matched identically)
[PASS] Bytecode identical: 268 instructions matched with 0 differences!
```

Bytecode lỗi:

```text
[!] Bytecode Differences: 12 discrepancies found between 336 orig and 324 decompiled instructions.
```

Sửa discrepancy sớm nhất trước; các discrepancy sau thường là hệ quả.

---

## 13. Chọn interpreter

Quy tắc mặc định:

1. Input không phải `.pyc` → `ResolveVersion`.
2. Đọc 2 byte đầu dạng little-endian.
3. Tra bảng magic.
4. 3.11 ưu tiên `multiver/python311-embed/python.exe`.
5. 3.12 dùng `python`.
6. Các phiên bản khác dùng `py -X.Y`.
7. Magic không xác định dùng `python`.

Muốn dùng đường dẫn tùy chỉnh, sửa `Core/PythonLocator.cs` rồi build lại.

---

## 14. Xử lý sự cố

### Không tìm thấy `py`

Windows: cài Python Launcher. Unix: tạo shim ở §9.2.

### Không tìm thấy `py -3.14`

Phiên bản chưa đăng ký với launcher. Cài lại và kiểm tra `py -0p`.

### Magic không xác định

Kiểm tra header:

```bash
python -c "print(open('input.pyc','rb').read(4).hex())"
```

Đối chiếu bảng magic ở §15.3.

### Không tìm thấy marshal code object

Tệp có thể bị cắt, header không phải 16 byte, payload bị nén/mã hóa. Bridge quét có giới hạn; nếu thất bại thì không hỗ trợ.

### Stack underflow

Đây là lỗi decompiler. Ghi lại tệp và index instruction, dùng `inspect` để tái hiện.

### Hành vi khác nhưng bytecode giống

Chương trình có thể phụ thuộc path, `__file__`, stdin, random, thời gian hoặc mạng. Chạy hai chương trình trong cùng điều kiện; kiểm tra tất định dùng `compare`.

### Bytecode khác nhưng hành vi giống

Tương đương hành vi nhưng không tương đương bytecode vẫn là thất bại. Sửa parser hoặc emitter.

### Build thiếu SDK

Cài .NET 10 SDK, kiểm tra `dotnet --list-sdks`. Nếu có nhiều SDK, thêm `global.json`.

### Linux thiếu thư viện native

Cài `libssl-dev`, `zlib1g-dev`, `libffi-dev`, `liblzma-dev`.

### Đường dẫn có dấu cách

Đặt mọi đường dẫn trong dấu ngoặc kép.

---

## 15. Phụ lục

### 15.1 Tóm tắt with protocol

| Phiên bản | Protocol |
|---|---|
| 3.8–3.10 | `SETUP_WITH`, `POP_BLOCK`, `WITH_CLEANUP_*` |
| 3.11–3.13 | `BEFORE_WITH`, `CALL 2` |
| 3.14–3.15 | `COPY 1; LOAD_SPECIAL __exit__; SWAP 2; SWAP 3; LOAD_SPECIAL __enter__; CALL 0` |

### 15.2 Trường exception table

| Trường | Ý nghĩa |
|---|---|
| `start` | Offset bắt đầu vùng bảo vệ |
| `end` | Offset kết thúc (không tính) |
| `target` | Offset handler |
| `depth` | Độ sâu ngăn xếp tại handler |
| `lasti` | Có đẩy offset instruction cuối hay không |

### 15.3 Magic number

| Magic | Phiên bản |
|---:|---|
| 3413 | 3.8 |
| 3425 | 3.9 |
| 3439 | 3.10 |
| 3495 | 3.11 |
| 3531 | 3.12 |
| 3571 | 3.13 |
| 3627 | 3.14 |
| 3666 | 3.15 |

### 15.4 Thuật ngữ

| Thuật ngữ | Ý nghĩa |
|---|---|
| Code object | Biểu diễn đã biên dịch của module, function, class hoặc comprehension |
| Bytecode | Chuỗi instruction trong code object |
| Marshal | Định dạng serialize nhị phân của CPython |
| Magic number | Hai byte định danh phiên bản ở đầu `.pyc` |
| Constant pool | `co_consts` |
| Names | `co_names` |
| Varnames | `co_varnames` |
| Free variable | Biến được bắt từ scope ngoài |
| Cell variable | Biến lưu trong cell cho closure |
| Exception table | Vùng bảo vệ và handler target từ 3.11+ |
| Block stack | Stack block bảo vệ kiểu cũ 3.8–3.10 |
| Symbolic stack | Ngăn xếp giá trị trừu tượng của decompiler |
| Handler | Code xử lý exception |
| Cleanup | Code gọi `__exit__` hoặc `__aexit__` ở nhánh bình thường |
| Continuation | Điểm điều khiển quay về sau handler |
| Fusion | Instruction gộp như `STORE_FAST_LOAD_FAST` |
| Anchor cleanup | Cleanup được chọn làm biên body |
| Nested cleanup | Cleanup thuộc cấu trúc bên trong |
| Transition entry | Entry exception table phủ code nội bộ handler |
| Dead else | Else không chạy được nhưng hằng số vẫn còn |
| Folded constant | Hằng số được gấp lúc compile |

---
