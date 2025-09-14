using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class LoopProbeTests
{
    [Fact]
    public void Captures_For_Loop_Iterator()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "insightor_tests_" + Guid.NewGuid().ToString("N"))).FullName;
        var srcPath = Path.Combine(tempDir, "Program.cs");
        var outPath = Path.Combine(tempDir, "out.jsonl");
        File.WriteAllText(srcPath, """
int x = 0;
for (int i = 0; i < 3; i++)
{
    x += 1;
}
Console.WriteLine(x);
""");

        var root = GetRepoRoot();
        var cliProj = Path.Combine(root, "cli.insightor", "Insightor", "Insightor.csproj");
        RunCli(cliProj, srcPath, outPath);

        Assert.True(File.Exists(outPath));
        var entries = File.ReadAllLines(outPath).Select(l => JsonSerializer.Deserialize<ProbeEntry>(l)!).ToList();

        // Find the loop header line
        var loopLine = entries.First(e => e.variables.ContainsKey("i")).line;
        var iSnapshots = entries.Where(e => e.line == loopLine && e.variables.ContainsKey("i")).Select(e => e.variables["i"].GetInt32()).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, iSnapshots);
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "cli.insightor")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void RunCli(string cliProj, string srcPath, string outPath)
    {
        var buildOut = Path.Combine(Path.GetTempPath(), "insightor_cli_build_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildOut);
        var objOut = (Path.Combine(buildOut, "obj").Replace('\\','/') + "/");
        var build = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{cliProj}\" -c Debug /p:UseSharedCompilation=false /nr:false",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using (var bp = Process.Start(build)!)
        {
            var bo = bp.StandardOutput.ReadToEnd();
            var be = bp.StandardError.ReadToEnd();
            bp.WaitForExit(60000);
            Assert.True(bp.ExitCode == 0, "Build failed.\n" + bo + "\n" + be);
        }

        // Run with minimal retry for potential file locks
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var run = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --no-build --project \"{cliProj}\" -- \"{srcPath}\" \"{outPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var rp = Process.Start(run)!;
            var ro = rp.StandardOutput.ReadToEnd();
            var re = rp.StandardError.ReadToEnd();
            rp.WaitForExit(60000);
            if (rp.ExitCode == 0)
            {
                return;
            }
            if (re.Contains("CS2012") || re.Contains("used by another process"))
            {
                System.Threading.Thread.Sleep(500);
                continue;
            }
            Assert.True(rp.ExitCode == 0, "Run failed.\nSTDOUT:\n" + ro + "\nSTDERR:\n" + re);
        }
        throw new Exception("Run failed after retries due to file lock.");
    }

    private record ProbeEntry(int line, Dictionary<string, JsonElement> variables);
}


