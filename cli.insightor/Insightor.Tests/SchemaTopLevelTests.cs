using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class SchemaTopLevelTests
{
	[Fact]
	public void Emits_CallStart_And_CallEnd_For_Program()
	{
		var res = CliTestHost.Run("""
int x = 1;
int y = x + 2;
""");
		Assert.Equal(0, res.ExitCode);
		Assert.NotEmpty(res.Entries);
		var start = res.Entries.FirstOrDefault(e => e.type == "callStart" && e.method == "Program");
		var end = res.Entries.FirstOrDefault(e => e.type == "callEnd" && e.method == "Program");
		Assert.NotNull(start);
		Assert.NotNull(end);
	}

	[Fact]
	public void Line_Events_Contain_Variables_Dictionary()
	{
		var res = CliTestHost.Run("""
int a = 10;
int b = a + 5;
""");
		var line = res.Entries.First(e => e.type == "line" && e.variables.ContainsKey("b"));
		Assert.True(line.variables.ContainsKey("a"));
	}
}

