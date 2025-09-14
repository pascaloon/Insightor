using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class SimpleProbeTests
{
    [Fact]
    public void Captures_Local_Variables()
    {
        // Arrange: create temp source file
        var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "insightor_tests_" + Guid.NewGuid().ToString("N"))).FullName;
        var srcPath = Path.Combine(tempDir, "Program.cs");
        var outPath = Path.Combine(tempDir, "out.jsonl");
        File.WriteAllText(srcPath, """
int x = 1;
int y = x + 2;
System.Console.WriteLine(y);
""");

        // Act: build once, then run with --no-build to avoid file lock issues
        var root = GetRepoRoot();
        var cliProj = Path.Combine(root, "cli.insightor", "Insightor", "Insightor.csproj");
        RunCli(cliProj, srcPath, outPath);

        // Assert: output exists and contains expected variables
        Assert.True(File.Exists(outPath), "Output file not created.");
        var lines = File.ReadAllLines(outPath);
        Assert.True(lines.Length >= 2, "Expected at least two probe lines.");

        var entries = lines.Select(l => JsonSerializer.Deserialize<ProbeEntry>(l)!).ToList();
        // Find line with y
        var yEntry = entries.FirstOrDefault(e => e.variables.ContainsKey("y"));
        Assert.NotNull(yEntry);
        Assert.Equal(3, yEntry!.variables["y"].GetInt32());

        // x should be 1
        var xEntry = entries.FirstOrDefault(e => e.variables.ContainsKey("x"));
        Assert.NotNull(xEntry);
        Assert.Equal(1, xEntry!.variables["x"].GetInt32());
    }

    private static string GetRepoRoot()
    {
        // Traverse up from current directory to find the repo root containing cli.insightor
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
        var build = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{cliProj}\" -c Debug -o \"{buildOut}\" /p:UseSharedCompilation=false /nr:false",
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

        var exePath = Path.Combine(buildOut, "Insightor.dll");
        // Run with minimal retry for potential file locks
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var run = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{exePath}\" \"{srcPath}\" \"{outPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Console.WriteLine("Running CLI: dotnet " + run.Arguments);
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


