using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class CliArgsTests
{
	[Fact]
	public void Shows_Usage_When_Insufficient_Args()
	{
		var res = CliTestHost.RunWithCliArgs();
		Assert.Equal(1, res.ExitCode);
		Assert.Contains("Usage: insightor", res.Stderr);
	}

	[Fact]
	public void Passes_Program_Args_To_TopLevel_Main()
	{
		var code = """
var n = args.Length;
Console.WriteLine(n);
""";
		var result = CliTestHost.Run(code, programArgs: new[] { "one", "two", "three" });
		Assert.Equal(0, result.ExitCode);
		Assert.True(result.Entries.Count > 0);
		var entry = result.Entries.FirstOrDefault(e => e.variables.ContainsKey("n"));
		Assert.NotNull(entry);
		Assert.Equal(3, entry!.variables["n"].GetInt32());
	}

	[Fact]
	public void Creates_Output_Directory_If_Missing()
	{
		var tmp = Path.Combine(Path.GetTempPath(), "insightor_out_" + Guid.NewGuid().ToString("N"));
		var outPath = Path.Combine(tmp, "nested", "trace.jsonl");
		var res = CliTestHost.Run("""Console.WriteLine(42);""", outPath: outPath);
		Assert.Equal(0, res.ExitCode);
		Assert.True(File.Exists(outPath));
	}
}

