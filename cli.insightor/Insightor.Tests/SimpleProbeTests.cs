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

        // Act: run CLI via dotnet run
        var root = GetRepoRoot();
        var cliProj = Path.Combine(root, "cli.insightor", "Insightor", "Insightor.csproj");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{cliProj}\" -- \"{srcPath}\" \"{outPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Console.WriteLine("Running CLI: " + psi.Arguments);
        using var proc = Process.Start(psi)!;
        var stdOut = proc.StandardOutput.ReadToEnd();
        var stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);
        Assert.True(proc.HasExited, "Process did not exit. STDOUT: " + stdOut + "\nSTDERR: " + stdErr);
        Assert.True(proc.ExitCode == 0, "Process did not exit successfully. STDOUT: " + stdOut + "\nSTDERR: " + stdErr);

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

    private record ProbeEntry(int line, Dictionary<string, JsonElement> variables);
}


