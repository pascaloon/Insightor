using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class WhileProbeTests
{
    [Fact]
    public void Captures_While_Loop_Condition_Variables()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "insightor_tests_" + Guid.NewGuid().ToString("N"))).FullName;
        var srcPath = Path.Combine(tempDir, "Program.cs");
        var outPath = Path.Combine(tempDir, "out.jsonl");
        File.WriteAllText(srcPath, """
int x = 0;
while (x < 3)
{
    x++;
}
Console.WriteLine(x);
""");

        var root = GetRepoRoot();
        var cliProj = Path.Combine(root, "cli.insightor", "Insightor", "Insightor.csproj");
        RunCli(cliProj, srcPath, outPath);

        var entries = File.ReadAllLines(outPath).Select(l => JsonSerializer.Deserialize<ProbeEntry>(l)!).ToList();
        var loopGroup = entries.Where(e => e.variables.ContainsKey("x")).GroupBy(e => e.line).OrderByDescending(g => g.Count()).First();
        var snapshots = loopGroup.Select(e => e.variables["x"].GetInt32()).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, snapshots);
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
        TestUtils.EnsureBuilt(cliProj);

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


