using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Insightor.Core;

/// <summary>
/// Handles C# compilation with instrumentation for Insightor
/// </summary>
public class CompilationBuilder
{
    /// <summary>
    /// Creates a compiled assembly with instrumentation from a syntax tree
    /// </summary>
    /// <param name="syntaxTree">The parsed C# syntax tree</param>
    /// <param name="inputPath">Path to the input file for reference</param>
    /// <returns>Compiled C# compilation with instrumentation</returns>
    public static CSharpCompilation CreateInstrumentedCompilation(SyntaxTree syntaxTree, string inputPath)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilationOptions = new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug);

        var semanticModel = CSharpCompilation.Create(
            assemblyName: "InstrumentedProgram",
            syntaxTrees: new[] { syntaxTree },
            references: GetTrustedPlatformReferences(),
            options: compilationOptions
        ).GetSemanticModel(syntaxTree, ignoreAccessibility: true);

        // Apply instrumentation
        var rewriter = new ProbeRewriter(semanticModel);
        var newRoot = rewriter.Visit(syntaxTree.GetRoot())!;

        // Ensure the instrumented tree has the exact same parse options
        var instrumentedTree = (CSharpSyntaxTree)syntaxTree.WithRootAndOptions(newRoot, parseOptions);

        // Add __Probe helper to the compilation with identical parse options
        var probeTree = CSharpSyntaxTree.ParseText(
            SourceText.From(ProbeSource.GetSource(), Encoding.UTF8),
            parseOptions
        );

        // Emulate SDK implicit usings so typical code like `Console.WriteLine` compiles without explicit `using System;`
        var implicitUsings = string.Join("\n", new[]
        {
            "global using System;",
            "global using System.Collections.Generic;",
            "global using System.IO;",
            "global using System.Linq;",
            "global using System.Net.Http;",
            "global using System.Threading;",
            "global using System.Threading.Tasks;"
        });
        var implicitUsingsTree = CSharpSyntaxTree.ParseText(
            SourceText.From(implicitUsings + "\n", Encoding.UTF8),
            parseOptions
        );

        return CSharpCompilation.Create(
            assemblyName: "InstrumentedProgram",
            syntaxTrees: new[] { instrumentedTree, probeTree, implicitUsingsTree },
            references: GetTrustedPlatformReferences(),
            options: compilationOptions
        );
    }

    /// <summary>
    /// Gets trusted platform references for compilation
    /// </summary>
    /// <returns>Array of metadata references</returns>
    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrEmpty(tpa))
        {
            throw new InvalidOperationException("Unable to resolve platform assemblies.");
        }

        var paths = tpa.Split(Path.PathSeparator);
        // Prefer a small subset to reduce load time
        string[] preferred =
        {
            "System.Runtime",
            "System.Console",
            "System.Private.CoreLib",
            "System.Linq",
            "System.Collections",
            "System.IO",
            "System.Net.Http",
            "System.Threading",
            "System.Threading.Tasks",
            "System.Runtime.Extensions",
            "System.Text.Json",
        };

        var selected = paths
            .Where(p => preferred.Any(pref =>
                Path.GetFileNameWithoutExtension(p).Equals(pref, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToList();

        // Also include any implicit references the input may require (allow missing preferred)
        if (selected.Count < 6)
        {
            selected = paths.Select(p => MetadataReference.CreateFromFile(p)).ToList();
        }

        return selected;
    }
}
