using Xunit;

namespace Insightor.Tests;

public class ErrorHandlingTests
{
	[Fact]
	public void Returns_2_On_Compile_Error()
	{
		var res = CliTestHost.Run("""
this is not c#
""");
		Assert.Equal(2, res.ExitCode);
		Assert.Contains("error CS", res.Stderr);
	}

	[Fact]
	public void Returns_4_On_Runtime_Exception()
	{
		var res = CliTestHost.Run("""
throw new System.InvalidOperationException("boom");
""");
		Assert.Equal(4, res.ExitCode);
		Assert.Contains("InvalidOperationException", res.Stderr);
		Assert.Contains("boom", res.Stderr);
	}
}

