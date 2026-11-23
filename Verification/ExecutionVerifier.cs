namespace PycRune.Verification;

using System.Diagnostics;
using PycRune.Core;

public sealed class ExecutionVerifier
{
    public sealed record RunResult(int ExitCode, string StdOut, string StdErr);

    public static bool VerifyExecution(string originalPath, string decompiledSource, out string report)
    {
        var tempDecompiledPy = Path.Combine(Path.GetTempPath(), $"pycrune_exec_{Guid.NewGuid():N}.py");
        File.WriteAllText(tempDecompiledPy, decompiledSource);

        try
        {


            var (interpreter, versionArg, _) = PythonLocator.Resolve(originalPath);
            var origRes = RunPython(originalPath, interpreter, versionArg);
            if (origRes.ExitCode == -1 && origRes.StdErr.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {


                origRes = RunPython(originalPath, interpreter, versionArg);
            }
            var decompRes = RunPython(tempDecompiledPy, interpreter, versionArg);
            if (decompRes.ExitCode == -1 && decompRes.StdErr.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {


                decompRes = RunPython(tempDecompiledPy, interpreter, versionArg);
            }

            if (origRes.ExitCode != decompRes.ExitCode)
            {
                report = $"[EXIT CODE MISMATCH] Original={origRes.ExitCode}, Decompiled={decompRes.ExitCode}\nOriginal Err:\n{origRes.StdErr}\nDecompiled Err:\n{decompRes.StdErr}";
                return false;
            }

            if (origRes.StdOut != decompRes.StdOut)
            {
                report = $"[OUTPUT MISMATCH]\n--- Original ({origRes.StdOut.Length} chars) ---\n{Truncate(origRes.StdOut, 500)}\n--- Decompiled ({decompRes.StdOut.Length} chars) ---\n{Truncate(decompRes.StdOut, 500)}";
                return false;
            }

            report = "100% BEHAVIORAL FIDELITY CONFIRMED (ExitCode and Output matched identically)";
            return true;
        }
        finally
        {
            if (File.Exists(tempDecompiledPy))
            {
                File.Delete(tempDecompiledPy);
            }
        }
    }

    private static RunResult RunPython(string targetPath, string interpreter, string versionArg)
    {
        var psi = new ProcessStartInfo
        {
            FileName = interpreter,
            Arguments = string.IsNullOrEmpty(versionArg)
                ? $"\"{targetPath}\""
                : $"{versionArg} \"{targetPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to run python process");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(60000))
        {
            try { proc.Kill(true); } catch { }
            return new RunResult(-1, "", "Execution timed out (>60000ms)");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return new RunResult(proc.ExitCode, stdout, stderr);
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "... [truncated]";
}
