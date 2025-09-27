using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class ControlFlowTests
{
	[Fact]
	public void If_Else_Condition_Variables_Appear()
	{
		var res = CliTestHost.Run("""
int x = 5;
if (x < 0) { x -= 1; }
else if (x < 5) { x += 1; }
else { x += 2; }
""");
		var linesWithX = res.Entries.Where(e => e.variables.ContainsKey("x")).Select(e => e.line).Distinct().ToList();
		Assert.True(linesWithX.Count >= 2);
	}

	[Fact]
	public void For_Loop_Iterator_Sequence()
	{
		var res = CliTestHost.Run("""
for (int i = 0; i < 3; i++)
{
	System.Console.Write(i);
}
""");
		var loopLine = res.Entries.First(e => e.variables.ContainsKey("i")).line;
		var seq = res.Entries.Where(e => e.line == loopLine && e.variables.ContainsKey("i")).Select(e => e.variables["i"].GetInt32()).ToList();
		Assert.Equal(new[] { 0, 1, 2 }, seq);
	}

	[Fact]
	public void While_Loop_Condition_Sequence()
	{
		var res = CliTestHost.Run("""
int x = 0;
while (x < 3)
{
	x++;
}
""");
		var line = res.Entries.Where(e => e.variables.ContainsKey("x")).GroupBy(e => e.line).OrderByDescending(g => g.Count()).First().Key;
		var seq = res.Entries.Where(e => e.line == line && e.variables.ContainsKey("x")).Select(e => e.variables["x"].GetInt32()).ToList();
		Assert.Equal(new[] { 0, 1, 2 }, seq);
	}
}

