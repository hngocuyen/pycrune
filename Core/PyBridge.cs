namespace PycRune.Core;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using PycRune.AST;
using PycRune.Model;

public static class PyBridge
{
    private const string BridgeScript = """
import sys, marshal, dis, json, os

def serialize_const(c):
    if hasattr(c, 'co_code'):
        return f"<code:{c.co_name}>"
    if c is Ellipsis:
        return {"__type__": "ellipsis"}
    if isinstance(c, float):
        if c != c:
            return {"__type__": "float", "value": "nan"}
        if c == float('inf'):
            return {"__type__": "float", "value": "inf"}
        if c == float('-inf'):
            return {"__type__": "float", "value": "-inf"}
        return c
    if isinstance(c, (int, str, bool, type(None))):
        return c
    if isinstance(c, (list, tuple)):
        return [serialize_const(x) for x in c]
    if isinstance(c, frozenset):
        return {"__type__": "frozenset", "items": [serialize_const(x) for x in sorted(list(c), key=lambda v: (str(type(v)), str(v)))]}
    if isinstance(c, set):
        return {"__type__": "set", "items": [serialize_const(x) for x in sorted(list(c), key=lambda v: (str(type(v)), str(v)))]}
    if isinstance(c, complex):
        return {"__type__": "complex", "real": c.real, "imag": c.imag}
    if isinstance(c, slice):
        return {"__type__": "slice", "start": serialize_const(c.start), "stop": serialize_const(c.stop), "step": serialize_const(c.step)}
    if isinstance(c, bytes):
        return {"__type__": "bytes", "hex": c.hex()}
    return str(c)

def serialize_co(co):
    nested = []
    nested_map = {}
    consts = []
    for c in co.co_consts:
        if hasattr(c, 'co_code'):
            nested_map[id(c)] = len(nested)
            nested.append(serialize_co(c))
            consts.append(f"<code:{c.co_name}:{len(nested)-1}>")
        else:
            consts.append(serialize_const(c))

    instrs = []
    for ins in dis.get_instructions(co):
        target = None
        if ins.opcode in dis.hasjrel or ins.opcode in dis.hasjabs:
            if isinstance(ins.argval, int):
                target = ins.argval
        val = ins.argval


        if ins.opname == 'KW_NAMES' and ins.arg is not None:
            try:
                candidate = co.co_consts[ins.arg]
                if isinstance(candidate, tuple):
                    val = candidate
            except Exception:
                pass
        if hasattr(val, 'co_code'):
            val = f"<code:{val.co_name}:{nested_map.get(id(val), 0)}>"
        else:
            val = serialize_const(val)
        instrs.append({
            "offset": ins.offset,
            "opcode": ins.opcode,
            "opname": ins.opname,
            "arg": ins.arg,
            "argval": val,
            "argrepr": ins.argrepr,
            "target": target,
            "is_jump_target": ins.is_jump_target,
            "starts_line": ins.starts_line if type(ins.starts_line) is int else None
        })
    excs = []
    if hasattr(dis, '_parse_exception_table'):
        for e in dis._parse_exception_table(co):
            excs.append({
                "start": e.start,
                "end": e.end,
                "target": e.target,
                "depth": e.depth,
                "lasti": e.lasti
            })

    return {
        "name": co.co_name,
        "qualname": getattr(co, 'co_qualname', co.co_name),
        "python_version": sys.version.split()[0],
        "filename": co.co_filename,
        "firstlineno": co.co_firstlineno,
        "argcount": co.co_argcount,
        "posonlyargcount": getattr(co, 'co_posonlyargcount', 0),
        "kwonlyargcount": getattr(co, 'co_kwonlyargcount', 0),
        "stacksize": co.co_stacksize,
        "flags": co.co_flags,
        "constants": consts,
        "names": list(co.co_names),
        "varnames": list(co.co_varnames),
        "freevars": list(co.co_freevars),
        "cellvars": list(co.co_cellvars),
        "instructions": instrs,
        "exception_table": excs,
        "nested_codes": nested
    }


MAGIC_HEADER = {
    3350: 8,
    3379: 12,
    3394: 16,
    3413: 16,
    3425: 16,
    3439: 16,
    3495: 16,
    3531: 16,
    3571: 16,
    3627: 16,
    3666: 16,
}

def _try_code(data, off):
    if off < 0 or off >= len(data):
        return None
    try:
        candidate = marshal.loads(data[off:])
    except Exception:
        return None
    return candidate if hasattr(candidate, 'co_code') else None

def find_code(data):

    if len(data) >= 4:
        magic = int.from_bytes(data[:2], 'little')
        if magic in MAGIC_HEADER and data[2:4] == b'\r\n':
            off = MAGIC_HEADER[magic]
            co = _try_code(data, off)
            if co is not None:
                return co, off


    candidates = [0, 8, 12, 16]
    seen = set(candidates)
    for i in range(min(len(data), 4096)):
        if len(candidates) >= 256:
            break
        if (data[i] & 0x7f) == 0x63 and i not in seen:
            seen.add(i)
            candidates.append(i)
    for off in candidates:
        co = _try_code(data, off)
        if co is not None:
            return co, off
    return None, None

target_path = sys.argv[1]
if target_path == '-':
    data = sys.stdin.buffer.read()
    co = compile(data.decode('utf-8', errors='replace'), '<decompiled>', 'exec')
    print(json.dumps(serialize_co(co)))
    sys.exit(0)

with open(target_path, 'rb') as f:
    data = f.read()

if target_path.endswith('.pyc'):
    co, payload_offset = find_code(data)
    if co is None:
        sys.stderr.write(f"pycrune: no marshal code object found in {target_path} ({len(data)} bytes)\n")
        sys.exit(3)
else:
    co = compile(data.decode('utf-8', errors='replace'), target_path, 'exec')

print(json.dumps(serialize_co(co)))
""";

    public static PythonCodeObject Load(string path, string? versionHint = null)
    {
        var (interpreter, versionArg, version) = PythonLocator.Resolve(path, versionHint);
        var key = $"{interpreter}|{versionArg}";
        try
        {
            return LoadViaServer(interpreter, versionArg, key, new { path });
        }
        catch (BridgeProcessException) when (version != null)
        {


            return LoadViaServer("python", "", "python|", new { path });
        }
    }

        public static PythonCodeObject LoadSource(string source, string? versionHint = null)
    {
        var (interpreter, versionArg, version) = PythonLocator.ResolveVersion(versionHint);
        var key = $"{interpreter}|{versionArg}";
        try
        {
            return LoadViaServer(interpreter, versionArg, key, new { source });
        }
        catch (BridgeProcessException) when (version != null)
        {
            return LoadViaServer("python", "", "python|", new { source });
        }
    }

    private static readonly ThreadLocal<Dictionary<string, BridgeServer>> BridgeServers =
        new(() => new Dictionary<string, BridgeServer>(StringComparer.Ordinal));

    private static PythonCodeObject LoadViaServer(
        string interpreter, string versionArg, string key, object request)
    {
        var server = GetBridgeServer(key, interpreter, versionArg);
        try
        {
            var requestJson = JsonSerializer.Serialize(request);
            var response = server.Request(requestJson);
            var root = JsonNode.Parse(response) ?? throw new BridgeProcessException("Null bridge server output");
            if (root["__error__"] is JsonValue errorValue)
            {
                throw new BridgeProcessException(errorValue.GetValue<string>());
            }
            return NormalizeOpCodes(ParseCodeObject(root));
        }
        catch (Exception ex) when (ex is BridgeProcessException or IOException or JsonException)
        {
            DropBridgeServer(key, server);
            if (ex is BridgeProcessException) throw;
            throw new BridgeProcessException($"Bridge server protocol failure: {ex.Message}");
        }
    }

    private static BridgeServer GetBridgeServer(string key, string interpreter, string versionArg)
    {
        var cache = BridgeServers.Value!;
        if (!cache.TryGetValue(key, out var server))
        {
            server = new BridgeServer(interpreter, versionArg, BridgeServerScriptPath());
            cache[key] = server;
        }
        return server;
    }

    private static void DropBridgeServer(string key, BridgeServer server)
    {
        var cache = BridgeServers.Value!;
        if (cache.TryGetValue(key, out var cached) && ReferenceEquals(cached, server))
        {
            cache.Remove(key);
        }
        server.Dispose();
    }

    private static readonly object BridgeServerScriptLock = new();
    private static string? _bridgeServerScriptPath;

    private static string BridgeServerScriptPath()
    {
        if (_bridgeServerScriptPath != null) return _bridgeServerScriptPath;
        lock (BridgeServerScriptLock)
        {
            if (_bridgeServerScriptPath != null) return _bridgeServerScriptPath;
            var path = Path.Combine(Path.GetTempPath(), "pycrune_bridge_server_stable_v1.py");
            var script = BuildServerScript();
            if (!File.Exists(path) || File.ReadAllText(path) != script)
            {
                File.WriteAllText(path, script);
            }
            _bridgeServerScriptPath = path;
            return path;
        }
    }

    private static string BuildServerScript()
    {
        const string marker = "target_path = sys.argv[1]";
        var index = BridgeScript.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("bridge server script marker missing");
        }
        return BridgeScript[..index] + ServerLoop;
    }

    private const string ServerLoop = """
for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        request = json.loads(line)
        if request.get('op') == 'exit':
            break
        if 'source' in request:
            co = compile(request['source'], '<decompiled>', 'exec')
        else:
            target_path = request['path']
            with open(target_path, 'rb') as f:
                data = f.read()
            if target_path.endswith('.pyc'):
                co, payload_offset = find_code(data)
                if co is None:
                    raise RuntimeError('no marshal code object found')
            else:
                co = compile(data.decode('utf-8', errors='replace'), target_path, 'exec')
        sys.stdout.write(json.dumps(serialize_co(co)) + '\n')
        sys.stdout.flush()
    except Exception as exc:
        sys.stdout.write(json.dumps({'__error__': f'{type(exc).__name__}: {exc}'}) + '\n')
        sys.stdout.flush()
""";

    private sealed class BridgeServer : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;

        public BridgeServer(string interpreter, string versionArg, string scriptPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = interpreter,
                Arguments = string.IsNullOrEmpty(versionArg)
                    ? $"\"{scriptPath}\""
                    : $"{versionArg} \"{scriptPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                _process = Process.Start(psi) ?? throw new BridgeProcessException("Failed to launch bridge server");
            }
            catch (BridgeProcessException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BridgeProcessException($"Failed to launch bridge server: {ex.Message}");
            }

            _input = _process.StandardInput;
            _output = _process.StandardOutput;
        }

        public string Request(string requestJson)
        {
            _input.WriteLine(requestJson);
            _input.Flush();
            var response = _output.ReadLine();
            if (response == null)
            {
                var code = _process.HasExited ? _process.ExitCode : -1;
                throw new BridgeProcessException($"Bridge server closed unexpectedly (exit {code})");
            }
            return response;
        }

        public void Dispose()
        {
            try
            {
                _input.WriteLine("{\"op\":\"exit\"}");
                _input.Flush();
            }
            catch
            {
            }

            try
            {
                if (!_process.WaitForExit(200))
                {
                    _process.Kill(true);
                }
            }
            catch
            {
            }

            _process.Dispose();
        }
    }

    private static readonly object BridgeScriptLock = new();
    private static string? _bridgeScriptPath;

    private static string BridgeScriptPath()
    {
        if (_bridgeScriptPath != null) return _bridgeScriptPath;
        lock (BridgeScriptLock)
        {
            if (_bridgeScriptPath != null) return _bridgeScriptPath;
            var path = Path.Combine(Path.GetTempPath(), "pycrune_bridge_stable_v1.py");
            if (!File.Exists(path) || File.ReadAllText(path) != BridgeScript)
            {
                File.WriteAllText(path, BridgeScript);
            }
            _bridgeScriptPath = path;
            return path;
        }
    }

    private static string RunBridge(
        string interpreter, string versionArg, string scriptPath, string path, string? stdinSource = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = interpreter,
            Arguments = string.IsNullOrEmpty(versionArg)
                ? $"\"{scriptPath}\" \"{path}\""
                : $"{versionArg} \"{scriptPath}\" \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinSource != null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new BridgeProcessException("Failed to launch python");
        if (stdinSource != null)
        {
            try
            {
                proc.StandardInput.Write(stdinSource);
                proc.StandardInput.Close();
            }
            catch (IOException)
            {


            }
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            throw new BridgeProcessException($"Python bridge failed with exit code {proc.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private sealed class BridgeProcessException : InvalidOperationException
    {
        public BridgeProcessException(string message) : base(message) { }
    }

    private static readonly Dictionary<string, int> OpCodeByName = BuildOpCodeMap();

    private static Dictionary<string, int> BuildOpCodeMap()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in typeof(OpCodes).GetFields(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.IsLiteral && field.FieldType == typeof(int) && field.GetRawConstantValue() is int value)
            {
                map[field.Name] = value;
            }
        }
        return map;
    }


    private static readonly Dictionary<string, (int Op, string Repr)> LegacyBinaryOps = new(StringComparer.Ordinal)
    {
        ["BINARY_ADD"] = (0, "+"),
        ["BINARY_AND"] = (1, "&"),
        ["BINARY_FLOOR_DIVIDE"] = (2, "//"),
        ["BINARY_LSHIFT"] = (3, "<<"),
        ["BINARY_MATRIX_MULTIPLY"] = (4, "@"),
        ["BINARY_MULTIPLY"] = (5, "*"),
        ["BINARY_MODULO"] = (6, "%"),
        ["BINARY_OR"] = (7, "|"),
        ["BINARY_POWER"] = (8, "**"),
        ["BINARY_RSHIFT"] = (9, ">>"),
        ["BINARY_SUBTRACT"] = (10, "-"),
        ["BINARY_TRUE_DIVIDE"] = (11, "/"),
        ["BINARY_XOR"] = (12, "^"),
        ["INPLACE_ADD"] = (13, "+="),
        ["INPLACE_AND"] = (14, "&="),
        ["INPLACE_FLOOR_DIVIDE"] = (15, "//="),
        ["INPLACE_LSHIFT"] = (16, "<<="),
        ["INPLACE_MATRIX_MULTIPLY"] = (17, "@="),
        ["INPLACE_MULTIPLY"] = (18, "*="),
        ["INPLACE_MODULO"] = (19, "%="),
        ["INPLACE_OR"] = (20, "|="),
        ["INPLACE_POWER"] = (21, "**="),
        ["INPLACE_RSHIFT"] = (22, ">>="),
        ["INPLACE_SUBTRACT"] = (23, "-="),
        ["INPLACE_TRUE_DIVIDE"] = (24, "/="),
        ["INPLACE_XOR"] = (25, "^="),
    };

    private static PythonCodeObject NormalizeOpCodes(PythonCodeObject code)
    {
        var instructions = new List<PythonInstruction>();
        for (var i = 0; i < code.Instructions.Count; i++)
        {
            var ins = code.Instructions[i];


            if (ins.OpName == "END_FOR" &&
                i + 1 < code.Instructions.Count &&
                code.Instructions[i + 1].OpName is "POP_TOP" or "POP_ITER")
            {
                instructions.AddRange(NormalizeInstruction(ins, code));
                i++;
                continue;
            }

            instructions.AddRange(NormalizeInstruction(ins, code));
        }

        var nested = code.NestedCodes.Select(NormalizeOpCodes).ToList();
        return new PythonCodeObject
        {
            Name = code.Name,
            QualName = code.QualName,
            PythonVersion = code.PythonVersion,
            FileName = code.FileName,
            FirstLineNo = code.FirstLineNo,
            ArgCount = code.ArgCount,
            PosOnlyArgCount = code.PosOnlyArgCount,
            KwOnlyArgCount = code.KwOnlyArgCount,
            StackSize = code.StackSize,
            Flags = code.Flags,
            Constants = code.Constants,
            Names = code.Names,
            VarNames = code.VarNames,
            FreeVars = code.FreeVars,
            CellVars = code.CellVars,
            Instructions = instructions,
            ExceptionTable = code.ExceptionTable,
            NestedCodes = nested
        };
    }

    private static IEnumerable<PythonInstruction> NormalizeInstruction(PythonInstruction ins, PythonCodeObject code)
    {
        if (LegacyBinaryOps.TryGetValue(ins.OpName, out var bin))
        {
            yield return ins with
            {
                OpCode = OpCodes.BINARY_OP,
                OpName = "BINARY_OP",
                OpArg = bin.Op,
                ArgVal = (long)bin.Op,
                ArgRepr = bin.Repr
            };
            yield break;
        }

        switch (ins.OpName)
        {
            case "PRECALL":
            case "POP_ITER":
                yield return ins with { OpCode = OpCodes.NOP, OpName = "NOP" };
                yield break;

            case "NOT_TAKEN":


                yield return ins with { OpCode = OpCodes.NOT_TAKEN, OpName = "NOT_TAKEN" };
                yield break;

            case "JUMP_ABSOLUTE":
            {
                var backward = (ins.TargetOffset ?? 0) < ins.Offset;
                yield return ins with
                {
                    OpCode = backward ? OpCodes.JUMP_BACKWARD : OpCodes.JUMP_FORWARD,
                    OpName = backward ? "JUMP_BACKWARD" : "JUMP_FORWARD"
                };
                yield break;
            }

            case "SETUP_FINALLY":
            case "SETUP_EXCEPT":
                yield return ins with { OpCode = OpCodes.LEGACY_SETUP_FINALLY, OpName = "SETUP_FINALLY" };
                yield break;

            case "POP_BLOCK":
                yield return ins with { OpCode = OpCodes.LEGACY_POP_BLOCK, OpName = "POP_BLOCK" };
                yield break;

            case "END_FINALLY":
                yield return ins with { OpCode = OpCodes.LEGACY_END_FINALLY, OpName = "END_FINALLY" };
                yield break;

            case "DUP_TOP":
                yield return ins with { OpCode = OpCodes.LEGACY_DUP_TOP, OpName = "DUP_TOP" };
                yield break;

            case "DUP_TOP_TWO":
                yield return ins with { OpCode = OpCodes.LEGACY_DUP_TOP_TWO, OpName = "DUP_TOP_TWO" };
                yield break;

            case "ROT_TWO":
                yield return ins with { OpCode = OpCodes.LEGACY_ROT_TWO, OpName = "ROT_TWO" };
                yield break;

            case "ROT_THREE":
                yield return ins with { OpCode = OpCodes.LEGACY_ROT_THREE, OpName = "ROT_THREE" };
                yield break;

            case "ROT_FOUR":
                yield return ins with { OpCode = OpCodes.LEGACY_ROT_FOUR, OpName = "ROT_FOUR" };
                yield break;

            case "LIST_TO_TUPLE":
                yield return ins with { OpCode = OpCodes.LEGACY_LIST_TO_TUPLE, OpName = "LIST_TO_TUPLE" };
                yield break;

            case "BUILD_TUPLE_UNPACK":
                yield return ins with { OpCode = OpCodes.LEGACY_BUILD_TUPLE_UNPACK, OpName = "BUILD_TUPLE_UNPACK" };
                yield break;

            case "BUILD_LIST_UNPACK":
                yield return ins with { OpCode = OpCodes.LEGACY_BUILD_LIST_UNPACK, OpName = "BUILD_LIST_UNPACK" };
                yield break;

            case "BUILD_TUPLE_UNPACK_WITH_CALL":
                yield return ins with { OpCode = OpCodes.LEGACY_BUILD_TUPLE_UNPACK_WITH_CALL, OpName = "BUILD_TUPLE_UNPACK_WITH_CALL" };
                yield break;

            case "CALL_FINALLY":
                yield return ins with { OpCode = OpCodes.LEGACY_CALL_FINALLY, OpName = "CALL_FINALLY" };
                yield break;

            case "COPY_DICT_WITHOUT_KEYS":
                yield return ins with { OpCode = OpCodes.LEGACY_COPY_DICT_WITHOUT_KEYS, OpName = "COPY_DICT_WITHOUT_KEYS" };
                yield break;

            case "ROT_N":
                yield return ins with { OpCode = OpCodes.LEGACY_ROT_N, OpName = "ROT_N" };
                yield break;

            case "PREP_RERAISE_STAR":
                yield return ins with { OpCode = OpCodes.LEGACY_PREP_RERAISE_STAR, OpName = "PREP_RERAISE_STAR" };
                yield break;

            case "ASYNC_GEN_WRAP":
                yield return ins with { OpCode = OpCodes.LEGACY_ASYNC_GEN_WRAP, OpName = "ASYNC_GEN_WRAP" };
                yield break;

            case "JUMP_IF_NOT_EXC_MATCH":
                yield return ins with { OpCode = OpCodes.LEGACY_JUMP_IF_NOT_EXC_MATCH, OpName = "JUMP_IF_NOT_EXC_MATCH" };
                yield break;


            case "JUMP_IF_TRUE_OR_POP":
            case "JUMP_IF_FALSE_OR_POP":
            {
                var isTrue = ins.OpName == "JUMP_IF_TRUE_OR_POP";
                yield return ins with
                {
                    OpCode = OpCodes.COPY,
                    OpName = "COPY",
                    OpArg = 1,
                    ArgVal = 1L,
                    ArgRepr = "1",
                    TargetOffset = null
                };
                yield return ins with
                {
                    OpCode = isTrue ? OpCodes.POP_JUMP_IF_TRUE : OpCodes.POP_JUMP_IF_FALSE,
                    OpName = isTrue ? "POP_JUMP_IF_TRUE" : "POP_JUMP_IF_FALSE"
                };
                yield return ins with { OpCode = OpCodes.POP_TOP, OpName = "POP_TOP", TargetOffset = null };
                yield break;
            }

            case "CALL_FUNCTION_KW":
                yield return ins with { OpCode = OpCodes.LEGACY_CALL_FUNCTION_KW, OpName = "CALL_FUNCTION_KW" };
                yield break;

            case "BUILD_SET_UNPACK":
                yield return ins with { OpCode = OpCodes.LEGACY_BUILD_SET_UNPACK, OpName = "BUILD_SET_UNPACK" };
                yield break;

            case "BUILD_MAP_UNPACK":
                yield return ins with { OpCode = OpCodes.LEGACY_BUILD_MAP_UNPACK, OpName = "BUILD_MAP_UNPACK" };
                yield break;

            case "BUILD_MAP_UNPACK_WITH_CALL":
                yield return ins with { OpCode = OpCodes.LEGACY_BUILD_MAP_UNPACK_WITH_CALL, OpName = "BUILD_MAP_UNPACK_WITH_CALL" };
                yield break;

            case "SETUP_WITH":
            case "SETUP_ASYNC_WITH":
                yield return ins with { OpCode = OpCodes.LEGACY_SETUP_WITH, OpName = "SETUP_WITH" };
                yield break;

            case "YIELD_FROM":
                yield return ins with { OpCode = OpCodes.LEGACY_YIELD_FROM, OpName = "YIELD_FROM" };
                yield break;

            case "GEN_START":
            case "BEGIN_FINALLY":
            case "POP_FINALLY":
            case "WITH_CLEANUP_START":
            case "WITH_CLEANUP_FINISH":
            case "TO_BOOL":
                yield return ins with { OpCode = OpCodes.NOP, OpName = "NOP" };
                yield break;

            case "SET_FUNCTION_ATTRIBUTE":
                yield return ins with { OpCode = OpCodes.SET_FUNCTION_ATTRIBUTE, OpName = "SET_FUNCTION_ATTRIBUTE" };
                yield break;

            case "CONVERT_VALUE":
                yield return ins with { OpCode = OpCodes.CONVERT_VALUE, OpName = "CONVERT_VALUE" };
                yield break;

            case "FORMAT_SIMPLE":
                yield return ins with { OpCode = OpCodes.FORMAT_SIMPLE, OpName = "FORMAT_SIMPLE" };
                yield break;

            case "FORMAT_WITH_SPEC":
                yield return ins with { OpCode = OpCodes.FORMAT_WITH_SPEC, OpName = "FORMAT_WITH_SPEC" };
                yield break;

            case "CALL_KW":
                yield return ins with { OpCode = OpCodes.LEGACY_CALL_FUNCTION_KW, OpName = "CALL_FUNCTION_KW" };
                yield break;

            case "LOAD_SPECIAL":
                yield return ins with { OpCode = OpCodes.LOAD_SPECIAL, OpName = "LOAD_SPECIAL" };
                yield break;

            case "CALL_FUNCTION":
            case "CALL_METHOD":
                yield return ins with { OpCode = OpCodes.CALL, OpName = "CALL" };
                yield break;


            case "POP_JUMP_FORWARD_IF_FALSE":
            case "POP_JUMP_BACKWARD_IF_FALSE":
                yield return ins with { OpCode = OpCodes.POP_JUMP_IF_FALSE, OpName = "POP_JUMP_IF_FALSE" };
                yield break;

            case "POP_JUMP_FORWARD_IF_TRUE":
            case "POP_JUMP_BACKWARD_IF_TRUE":
                yield return ins with { OpCode = OpCodes.POP_JUMP_IF_TRUE, OpName = "POP_JUMP_IF_TRUE" };
                yield break;

            case "POP_JUMP_FORWARD_IF_NONE":
            case "POP_JUMP_BACKWARD_IF_NONE":
                yield return ins with { OpCode = OpCodes.POP_JUMP_IF_NONE, OpName = "POP_JUMP_IF_NONE" };
                yield break;

            case "POP_JUMP_FORWARD_IF_NOT_NONE":
            case "POP_JUMP_BACKWARD_IF_NOT_NONE":
                yield return ins with { OpCode = OpCodes.POP_JUMP_IF_NOT_NONE, OpName = "POP_JUMP_IF_NOT_NONE" };
                yield break;

            case "LOAD_METHOD":
                yield return ins with { OpCode = OpCodes.LOAD_ATTR, OpName = "LOAD_ATTR" };
                yield break;


            case "LOAD_FAST_LOAD_FAST":
            case "LOAD_FAST_BORROW_LOAD_FAST_BORROW":
            {
                var (first, second) = FusedLocals(ins, code);
                yield return MakeLocal(ins, OpCodes.LOAD_FAST, "LOAD_FAST", first, code);
                yield return MakeLocal(ins, OpCodes.LOAD_FAST, "LOAD_FAST", second, code);
                yield break;
            }

            case "STORE_FAST_LOAD_FAST":
            case "STORE_FAST_STORE_FAST":
            {
                var (first, second) = FusedLocals(ins, code);
                var firstOp = ins.OpName == "STORE_FAST_LOAD_FAST" ? OpCodes.STORE_FAST : OpCodes.STORE_FAST;
                yield return MakeLocal(ins, firstOp, "STORE_FAST", first, code);
                yield return MakeLocal(ins, ins.OpName == "STORE_FAST_LOAD_FAST" ? OpCodes.LOAD_FAST : OpCodes.STORE_FAST,
                    ins.OpName == "STORE_FAST_LOAD_FAST" ? "LOAD_FAST" : "STORE_FAST", second, code);
                yield break;
            }

            case "LOAD_FAST_BORROW":
            case "LOAD_FAST_CHECK":
                yield return MakeLocal(ins, OpCodes.LOAD_FAST, "LOAD_FAST", ins.OpArg ?? 0, code);
                yield break;

            case "LOAD_SMALL_INT":
            {
                var value = ins.OpArg ?? 0;


                yield return ins with
                {
                    OpCode = OpCodes.LOAD_CONST,
                    OpName = "LOAD_SMALL_INT",
                    ArgVal = (long)value,
                    ArgRepr = value.ToString()
                };
                yield break;
            }

            case "LOAD_COMMON_CONSTANT":


                if (ins.ArgRepr == "AssertionError")
                {
                    yield return ins with { OpCode = OpCodes.LOAD_ASSERTION_ERROR, OpName = "LOAD_ASSERTION_ERROR" };
                    yield break;
                }
                yield return ins with
                {
                    OpCode = OpCodes.LOAD_CONST,
                    OpName = "LOAD_COMMON_CONSTANT",
                    ArgRepr = ins.ArgRepr ?? ins.ArgVal?.ToString()
                };
                yield break;

            case "COMPARE_OP":
            {


                var repr = ins.ArgVal as string ?? ins.ArgRepr ?? "==";
                yield return ins with { OpCode = OpCodes.COMPARE_OP, ArgRepr = repr };
                yield break;
            }
        }

        if (OpCodeByName.TryGetValue(ins.OpName, out var opcode))
        {
            yield return ins with { OpCode = opcode };
            yield break;
        }


        throw new FailFastException(
            $"Unsupported opcode '{ins.OpName}' (arg {ins.OpArg}) at offset {ins.Offset}: no dialect mapping");
    }

    private static (int First, int Second) FusedLocals(PythonInstruction ins, PythonCodeObject code)
    {
        if (ins.ArgVal is List<object?> { Count: 2 } pair)
        {
            var a = pair[0] is string sa ? LocalIndex(code, sa) : -1;
            var b = pair[1] is string sb ? LocalIndex(code, sb) : -1;
            if (a >= 0 && b >= 0) return (a, b);
        }
        var arg = ins.OpArg ?? 0;
        return (arg >> 4, arg & 0xF);
    }

    private static int LocalIndex(PythonCodeObject code, string name)
    {
        for (var i = 0; i < code.VarNames.Count; i++)
        {
            if (string.Equals(code.VarNames[i], name, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static PythonInstruction MakeLocal(
        PythonInstruction source, int opcode, string opName, int index, PythonCodeObject code)
    {
        var name = index >= 0 && index < code.VarNames.Count ? code.VarNames[index] : $"__local_{index}";
        return source with
        {
            OpCode = opcode,
            OpName = opName,
            OpArg = index,
            ArgVal = name,
            ArgRepr = name
        };
    }

    private static PythonCodeObject ParseCodeObject(JsonNode node)
    {
        var instrs = new List<PythonInstruction>();
        if (node["instructions"] is JsonArray insArray)
        {
            foreach (var item in insArray)
            {
                if (item == null) continue;
                instrs.Add(new PythonInstruction(
                    item["offset"]?.GetValue<int>() ?? 0,
                    item["opcode"]?.GetValue<int>() ?? 0,
                    item["opname"]?.GetValue<string>() ?? "",
                    item["arg"]?.GetValue<int>(),
                    ExtractJsonValue(item["argval"]),
                    item["argrepr"]?.GetValue<string>(),
                    item["target"]?.GetValue<int>(),
                    item["is_jump_target"]?.GetValue<bool>() ?? false,
                    item["starts_line"]?.GetValue<int>()
                ));
            }
        }

        var excs = new List<ExceptionTableEntry>();
        if (node["exception_table"] is JsonArray excArray)
        {
            foreach (var item in excArray)
            {
                if (item == null) continue;
                excs.Add(new ExceptionTableEntry(
                    item["start"]?.GetValue<int>() ?? 0,
                    item["end"]?.GetValue<int>() ?? 0,
                    item["target"]?.GetValue<int>() ?? 0,
                    item["depth"]?.GetValue<int>() ?? 0,
                    item["lasti"]?.GetValue<bool>() ?? false
                ));
            }
        }

        var consts = new List<object?>();
        if (node["constants"] is JsonArray constArray)
        {
            foreach (var item in constArray)
            {
                consts.Add(ExtractJsonValue(item));
            }
        }

        var nested = new List<PythonCodeObject>();
        if (node["nested_codes"] is JsonArray nestedArray)
        {
            foreach (var item in nestedArray)
            {
                if (item != null)
                {
                    nested.Add(ParseCodeObject(item));
                }
            }
        }

        return new PythonCodeObject
        {
            Name = node["name"]?.GetValue<string>() ?? "",
            QualName = node["qualname"]?.GetValue<string>() ?? "",
            PythonVersion = node["python_version"]?.GetValue<string>() ?? "",
            FileName = node["filename"]?.GetValue<string>() ?? "",
            FirstLineNo = node["firstlineno"]?.GetValue<int>() ?? 1,
            ArgCount = node["argcount"]?.GetValue<int>() ?? 0,
            PosOnlyArgCount = node["posonlyargcount"]?.GetValue<int>() ?? 0,
            KwOnlyArgCount = node["kwonlyargcount"]?.GetValue<int>() ?? 0,
            StackSize = node["stacksize"]?.GetValue<int>() ?? 0,
            Flags = node["flags"]?.GetValue<int>() ?? 0,
            Constants = consts,
            Names = ReadStringList(node["names"]),
            VarNames = ReadStringList(node["varnames"]),
            FreeVars = ReadStringList(node["freevars"]),
            CellVars = ReadStringList(node["cellvars"]),
            Instructions = instrs,
            ExceptionTable = excs,
            NestedCodes = nested
        };
    }

    private static object? ExtractJsonValue(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.GetValueKind() == JsonValueKind.Number)
            {


                var raw = val.ToJsonString();
                if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
                {
                    if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
                    {
                        return l;
                    }
                    return System.Numerics.BigInteger.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                }
                return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (val.TryGetValue<string>(out var s)) return s;
        }
        if (node is JsonArray arr)
        {
            var list = new List<object?>();
            foreach (var item in arr)
            {
                list.Add(ExtractJsonValue(item));
            }
            return list;
        }
        if (node is JsonObject obj && obj.TryGetPropertyValue("__type__", out var typeProp))
        {
            var typeStr = typeProp?.ToString();
            if (typeStr == "ellipsis")
            {
                return PyEllipsis.Instance;
            }
            if (typeStr == "set" && obj.TryGetPropertyValue("items", out var itemsProp) && itemsProp is JsonArray setArr)
            {
                var setList = new List<object?>();
                foreach (var item in setArr)
                {
                    setList.Add(ExtractJsonValue(item));
                }
                return new HashSet<object?>(setList);
            }
            if (typeStr == "frozenset" && obj.TryGetPropertyValue("items", out var frozenProp) && frozenProp is JsonArray frozenArr)
            {
                var frozenList = new List<object?>();
                foreach (var item in frozenArr)
                {
                    frozenList.Add(ExtractJsonValue(item));
                }
                return new FrozenSetValue(new HashSet<object?>(frozenList));
            }
            if (typeStr == "float" && obj.TryGetPropertyValue("value", out var floatValueProp))
            {
                return floatValueProp?.GetValue<string>() switch
                {
                    "nan" => double.NaN,
                    "inf" => double.PositiveInfinity,
                    "-inf" => double.NegativeInfinity,
                    _ => 0d
                };
            }
            if (typeStr == "complex" &&
                obj.TryGetPropertyValue("real", out var realProp) &&
                obj.TryGetPropertyValue("imag", out var imagProp))
            {
                var real = realProp is null ? 0d : realProp.GetValue<double>();
                var imag = imagProp is null ? 0d : imagProp.GetValue<double>();
                return new System.Numerics.Complex(real, imag);
            }
            if (typeStr == "bytes" && obj.TryGetPropertyValue("hex", out var hexProp))
            {
                var hex = hexProp?.GetValue<string>() ?? "";
                return Convert.FromHexString(hex);
            }
            if (typeStr == "slice")
            {
                return new SliceValue(
                    obj.TryGetPropertyValue("start", out var sliceStart) ? ExtractJsonValue(sliceStart) : null,
                    obj.TryGetPropertyValue("stop", out var sliceStop) ? ExtractJsonValue(sliceStop) : null,
                    obj.TryGetPropertyValue("step", out var sliceStep) ? ExtractJsonValue(sliceStep) : null);
            }
        }
        return node.ToString();
    }

    private static List<string> ReadStringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item != null)
                {
                    list.Add(item.GetValue<string>());
                }
            }
        }
        return list;
    }
}
