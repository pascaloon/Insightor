using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class BranchProbeTests
{
    [Fact]
    public void Captures_If_Condition_And_Branch_Variables()
    {
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "insightor_tests_" + Guid.NewGuid().ToString("N"))).FullName;
        var srcPath = Path.Combine(tempDir, "Program.cs");
        var outPath = Path.Combine(tempDir, "out.jsonl");
        File.WriteAllText(srcPath, """
int x = 1;

if (x > 0)
{
    x += 1;
}

int y = x + 2;

Console.WriteLine($"x = {x}, y = {y}");
""");

        var root = GetRepoRoot();
        var cliProj = Path.Combine(root, "cli.insightor", "Insightor", "Insightor.csproj");
        RunCli(cliProj, srcPath, outPath);

        Assert.True(File.Exists(outPath), "Output file not created.");
        var lines = File.ReadAllLines(outPath);
        Assert.True(lines.Length >= 4, "Expected at least four probe lines.");

        var entries = lines.Select(l => JsonSerializer.Deserialize<ProbeEntry>(l)!).ToList();

        // find condition line (line 3 in the snippet)
        var cond = entries.FirstOrDefault(e => e.line == 3);
        Assert.NotNull(cond);
        Assert.True(cond!.variables.ContainsKey("x"));

        // On line with declaration of y, both x and y should be present
        var yDecl = entries.FirstOrDefault(e => e.line == 9) ?? entries.FirstOrDefault(e => e.variables.ContainsKey("y"));
        Assert.NotNull(yDecl);
        Assert.True(yDecl!.variables.ContainsKey("x"));
        Assert.True(yDecl!.variables.ContainsKey("y"));

        // After branch, x should be 2
        var xEntry = entries.Last(e => e.variables.ContainsKey("x"));
        Assert.Equal(2, xEntry.variables["x"].GetInt32());

        // y should be 4
        var yEntry = entries.Last(e => e.variables.ContainsKey("y"));
        Assert.Equal(4, yEntry.variables["y"].GetInt32());
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
        // Build into unique temp output directory to avoid locks
        var buildOut = Path.Combine(Path.GetTempPath(), "insightor_cli_build_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildOut);
        var objOut = (Path.Combine(buildOut, "obj").Replace('\\','/') + "/");
        TestUtils.EnsureBuilt(cliProj);

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


