using System.Linq;
using Microsoft.CodeAnalysis;

namespace Insightor.Core;

/// <summary>
/// Handles compilation errors and diagnostics
/// </summary>
public static class CompilationErrors
{
    /// <summary>
    /// Handles compilation diagnostics and reports errors
    /// </summary>
    /// <param name="diagnostics">The compilation diagnostics to process</param>
    public static void Handle(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            System.Console.Error.WriteLine(diag.ToString());
        }
    }
}
