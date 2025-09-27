using System.Linq;
using Xunit;

namespace Insightor.Tests;

public class FunctionsAndLambdasTests
{
	[Fact]
	public void Method_BlockBody_Emits_CallStart_End_And_Return_Probe()
	{
		var res = CliTestHost.Run("""
int Add(int a, int b)
{
	var s = a + b;
	return s;
}

System.Console.WriteLine(Add(2,3));
""");
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callStart" && e.method!.Contains("Add(")));
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callEnd" && e.method!.Contains("Add(")));
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "line" && e.variables.ContainsKey("return") && e.variables["return"].GetInt32() == 5));
	}

	[Fact]
	public void Expression_Bodied_Method_Emits_Return_Probe()
	{
		var res = CliTestHost.Run("""
int Double(int x) => x * 2;
System.Console.WriteLine(Double(7));
""");
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callStart" && e.method!.Contains("Double(") && e.variables.ContainsKey("x") && e.variables["x"].GetInt32() == 7));
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callEnd" && e.method!.Contains("Double(")));
	}

	[Fact]
	public void Local_Function_Emits_Call_And_Return_Probe()
	{
		var res = CliTestHost.Run("""
int Outer(int x)
{
	int Inner(int y)
	{
		return y + 1;
	}
	return Inner(x);
}
System.Console.WriteLine(Outer(10));
""");
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callStart" && e.method!.Contains("Inner(")));
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.variables.ContainsKey("return") && e.variables["return"].GetInt32() == 11));
	}

	[Fact]
	public void Lambdas_Emit_Return_Probe_For_Expression_Body()
	{
		var res = CliTestHost.Run("""
System.Func<int,int> f = x => x + 3;
var v = f(4);
""");
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.variables.ContainsKey("return") && e.variables["return"].GetInt32() == 7));
	}

	[Fact]
	public void Constructor_Emits_CallStart_End()
	{
		var res = CliTestHost.Run("""
class C
{
	public int V;
	public C(int a)
	{
		V = a;
	}
}
var c = new C(42);
""");
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callStart" && e.method!.Contains(".ctor(")));
		Assert.NotNull(res.Entries.FirstOrDefault(e => e.type == "callEnd" && e.method!.Contains(".ctor(")));
	}
}

