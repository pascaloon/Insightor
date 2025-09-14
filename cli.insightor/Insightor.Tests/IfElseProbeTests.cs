using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class IfElseProbeTests
{
    [Fact]
    public void Captures_If_ElseIf_Else_Condition_Variables()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "insightor_tests_" + Guid.NewGuid().ToString("N"))).FullName;
        var srcPath = Path.Combine(tempDir, "Program.cs");
        var outPath = Path.Combine(tempDir, "out.jsonl");
        File.WriteAllText(srcPath, """
int x = 5;
if (x < 0)
{
    x -= 1;
}
else if (x < 5)
{
    x += 1;
}
else
{
    x += 2;
}
Console.WriteLine(x);
""");

        var root = GetRepoRoot();
        var cliProj = Path.Combine(root, "cli.insightor", "Insightor", "Insightor.csproj");
        TestUtils.EnsureBuilt(cliProj);

        // Run
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
            if (rp.ExitCode == 0) break;
            if (re.Contains("CS2012") || re.Contains("used by another process")) { System.Threading.Thread.Sleep(500); continue; }
            Assert.True(rp.ExitCode == 0, "Run failed.\n" + ro + "\n" + re);
        }

        var entries = File.ReadAllLines(outPath).Select(l => JsonSerializer.Deserialize<ProbeEntry>(l)!).ToList();
        // There should be at least one condition probe snapshot for x on each conditional line
        var linesWithX = entries.Where(e => e.variables.ContainsKey("x")).Select(e => e.line).Distinct().ToList();
        Assert.True(linesWithX.Count >= 2);
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

    private record ProbeEntry(int line, Dictionary<string, JsonElement> variables);
}


