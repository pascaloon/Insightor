using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Insightor.Tests;

internal static class CliTestHost
{
	private static readonly object _buildLock = new object();
	private static bool _built;

	public static string GetCliProjectPath()
	{
		var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (dir is not null)
		{
			var candidate = Path.Combine(dir.FullName, "cli.insightor", "Insightor", "Insightor.csproj");
			if (File.Exists(candidate)) return candidate;
			dir = dir.Parent;
		}
		throw new FileNotFoundException("Could not locate Insightor.csproj");
	}

	public static void EnsureBuilt()
	{
		if (_built) return;
		lock (_buildLock)
		{
			if (_built) return;
			for (int attempt = 0; attempt < 5; attempt++)
			{
				var psi = new ProcessStartInfo
				{
					FileName = "dotnet",
					Arguments = $"build \"{GetCliProjectPath()}\" -c Debug /p:UseSharedCompilation=false /nr:false",
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};
				using var p = Process.Start(psi)!;
				var so = p.StandardOutput.ReadToEnd();
				var se = p.StandardError.ReadToEnd();
				p.WaitForExit(120000);
				if (p.ExitCode == 0)
				{
					_built = true;
					return;
				}
				if (se.Contains("CS2012") || se.Contains("used by another process"))
				{
					System.Threading.Thread.Sleep(800);
					continue;
				}
				throw new Exception("Build failed.\n" + so + "\n" + se);
			}
			throw new Exception("Build failed after retries due to file lock.");
		}
	}

	public static RunResult Run(string sourceCode, string? outPath = null, string[]? programArgs = null)
	{
		EnsureBuilt();
		var tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "insightor_tests_" + Guid.NewGuid().ToString("N"))).FullName;
		var srcPath = Path.Combine(tempDir, "Program.cs");
		File.WriteAllText(srcPath, sourceCode);
		var outFile = outPath ?? Path.Combine(tempDir, "out", "out.jsonl");

		var cliProj = GetCliProjectPath();
		var args = $"run --no-build --project \"{cliProj}\" -- \"{srcPath}\" \"{outFile}\"";
		if (programArgs is not null && programArgs.Length > 0)
		{
			args += " --args " + string.Join(" ", programArgs.Select(EscapeArg));
		}

		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = args,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var p = Process.Start(psi)!;
		var stdout = p.StandardOutput.ReadToEnd();
		var stderr = p.StandardError.ReadToEnd();
		p.WaitForExit(120000);

		List<ProbeJson> entries = new();
		if (File.Exists(outFile))
		{
			entries = File.ReadAllLines(outFile)
				.Where(l => !string.IsNullOrWhiteSpace(l))
				.Select(l => JsonSerializer.Deserialize<ProbeJson>(l, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				})!)
				.ToList();
		}

		return new RunResult(p.ExitCode, stdout, stderr, outFile, entries);
	}

	public static RunResult RunWithCliArgs(params string[] cliArgs)
	{
		EnsureBuilt();
		var cliProj = GetCliProjectPath();
		var args = $"run --no-build --project \"{cliProj}\" --";
		if (cliArgs.Length > 0)
		{
			args += " " + string.Join(" ", cliArgs.Select(EscapeArg));
		}
		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = args,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		using var p = Process.Start(psi)!;
		var stdout = p.StandardOutput.ReadToEnd();
		var stderr = p.StandardError.ReadToEnd();
		p.WaitForExit(60000);
		return new RunResult(p.ExitCode, stdout, stderr, string.Empty, new List<ProbeJson>());
	}

	private static string EscapeArg(string arg)
	{
		// Keep tests simple: only quote when spaces exist
		return arg.Contains(' ') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
	}

	public sealed record RunResult(int ExitCode, string Stdout, string Stderr, string OutputPath, List<ProbeJson> Entries);

	public sealed class ProbeJson
	{
		public string? type { get; set; }
		public int line { get; set; }
		public string? method { get; set; }
		public Dictionary<string, JsonElement> variables { get; set; } = new();
	}
}

