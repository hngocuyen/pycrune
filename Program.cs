namespace PycRune;

using System.Diagnostics;
using PycRune.CFG;
using PycRune.Core;
using PycRune.Decompiler;
using PycRune.Verification;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "decompile" => HandleDecompile(args[1..]),
                "verify" => HandleVerify(args[1..]),
                "compare" => HandleCompare(args[1..]),
                "batch" => HandleBatch(args[1..]),
                "inspect" => HandleInspect(args[1..]),
                _ => PrintUnknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[CRITICAL ERROR] {ex}");
            Console.ResetColor();
            return 2;
        }
    }

    private static int HandleDecompile(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: pycrune decompile <path-to-pyc-or-py> [-o <out.py>] [--verify]");
            return 1;
        }

        var inputPath = args[0];
        string? outputPath = null;
        var verify = false;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (args[i] == "--verify")
            {
                verify = true;
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[*] Loading: {inputPath}");
        Console.ResetColor();

        var sw = Stopwatch.StartNew();
        var codeObj = PyBridge.Load(inputPath);
        Console.WriteLine($"[+] Parsed {codeObj.Instructions.Count} instructions, {codeObj.ExceptionTable.Count} exception table entries.");

        var decompiler = new StructuralDecompiler(codeObj);
        var stmts = decompiler.Decompile();
        var decompiledSource = PythonEmitter.Emit(stmts);
        
        var signature = """
        # ================================================================
        # PYCRUNE - Hngocuyen Python Bytecode Decompiler
        # The project has lasted two years.
        # From 6/2024 to 24/11/2026
        # Author: Vuong Tien Dat (王达)
        # ================================================================
        
        """;
        
        decompiledSource = signature + decompiledSource;
        sw.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[+] Decompiled {stmts.Count} top-level AST statements in {sw.ElapsedMilliseconds} ms.");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(outputPath))
        {
            File.WriteAllText(outputPath, decompiledSource);
            Console.WriteLine($"[+] Saved source to: {outputPath}");
        }
        else
        {
            Console.WriteLine("\n--- DECOMPILED PYTHON SOURCE ---");
            Console.WriteLine(decompiledSource.Length > 2000 ? decompiledSource[..2000] + "\n... [truncated]" : decompiledSource);
            Console.WriteLine("--------------------------------\n");
        }

        if (verify)
        {
            return RunVerificationSuite(inputPath, decompiledSource);
        }

        return 0;
    }

    private static int HandleVerify(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: pycrune verify <original-file> <decompiled-file>");
            return 1;
        }

        var originalPath = args[0];
        var decompiledPath = args[1];
        var decompiledSource = File.ReadAllText(decompiledPath);

        return RunVerificationSuite(originalPath, decompiledSource);
    }

    private static int RunVerificationSuite(string originalPath, string decompiledSource)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[*] Running Strict Verification Suite...");
        Console.ResetColor();

        var execSuccess = ExecutionVerifier.VerifyExecution(originalPath, decompiledSource, out var execReport);
        if (execSuccess)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] {execReport}");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FAIL] Execution divergence:\n{execReport}");
            Console.ResetColor();
        }

        Console.WriteLine("[*] Running Bytecode Instruction Comparison...");
        var diff = BytecodeComparer.Compare(originalPath, decompiledSource);

        if (diff.IsIdentical)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] Bytecode identical: {diff.TotalOriginal} instructions matched with 0 differences!");
            Console.ResetColor();
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[!] Bytecode Differences: {diff.Discrepancies.Count} discrepancies found between {diff.TotalOriginal} orig and {diff.TotalDecompiled} decompiled instructions.");
            foreach (var d in diff.Discrepancies.Take(10))
            {
                Console.WriteLine(d);
            }
            if (diff.Discrepancies.Count > 10)
            {
                Console.WriteLine($"... and {diff.Discrepancies.Count - 10} more discrepancies.");
            }
            Console.ResetColor();
            return execSuccess ? 0 : 1;
        }
    }

    private static int HandleCompare(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: pycrune compare <original-pyc> <decompiled-source>");
            return 1;
        }

        var source = File.ReadAllText(args[1]);
        var diff = BytecodeComparer.Compare(args[0], source);

        if (diff.IsIdentical)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] Bytecode identical: {diff.TotalOriginal} instructions matched with 0 differences.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"[FAIL] Bytecode mismatch: {diff.Discrepancies.Count} discrepancies between " +
                          $"{diff.TotalOriginal} original and {diff.TotalDecompiled} decompiled instructions.");
        foreach (var discrepancy in diff.Discrepancies.Take(10))
        {
            Console.WriteLine(discrepancy);
        }
        if (diff.Discrepancies.Count > 10)
        {
            Console.WriteLine($"... and {diff.Discrepancies.Count - 10} more discrepancies.");
        }
        Console.ResetColor();
        return 1;
    }

    private static int HandleBatch(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: pycrune batch <directory>");
            return 1;
        }

        var dir = args[0];
        var files = Directory.GetFiles(dir, "*.pyc", SearchOption.AllDirectories);
        Console.WriteLine($"[*] Found {files.Length} pyc files in {dir}");

        var passed = 0;
        foreach (var file in files)
        {
            Console.Write($"[-] Decompiling {Path.GetFileName(file)} ... ");
            try
            {
                var code = PyBridge.Load(file);
                var decompiler = new StructuralDecompiler(code);
                var stmts = decompiler.Decompile();
                var src = PythonEmitter.Emit(stmts);
                passed++;
                Console.WriteLine($"OK ({stmts.Count} stmts)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {ex.Message}");
            }
        }

        Console.WriteLine($"[*] Batch result: {passed}/{files.Length} decompiled.");
        return 0;
    }

    private static int HandleInspect(string[] args)
    {
        if (args.Length == 0) return 1;
        var code = PyBridge.Load(args[0]);
        var cfg = ControlFlowGraph.Build(code);
        var dom = new DominatorTree(cfg);
        var scopes = ExceptionScope.Analyze(cfg, dom);
        Console.WriteLine($"[+] CFG Blocks: {cfg.Blocks.Count}, Scopes: {scopes.Count}");
        foreach (var s in scopes)
        {
            Console.WriteLine($"Scope #{s.Id}: [{s.StartOffset}..{s.EndOffset}) Handlers: {s.Handlers.Count}, Finally: {s.HasFinally}, Exit: #{s.ExitBlock?.Id}");
        }
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
================================================================
  PYCRUNE - Hngocuyen Python Bytecode Decompiler (C#)
  The project has lasted two years.
  From 6/2024 to 24/11/2026, Good Day
  我特别喜欢 Python，已经写了五年代码了。祝你今天过得愉快！
  我使用的型号 : Opus4.6-8,Astra 6,DS4.1 (30% In Project)

                                            Sig 哈哈
                                       Vuong Tien Dat (王达)
================================================================
Commands:
  decompile <path> [-o <out.py>] [--verify]
  verify <original> <decompiled>
  compare <original> <decompiled>
  batch <directory>
  inspect <path>
""");
    }

    private static int PrintUnknown(string cmd)
    {
        Console.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 1;
    }
}
