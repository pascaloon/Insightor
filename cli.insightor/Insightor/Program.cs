using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// Insightor CLI: instruments a C# file with probe statements, compiles, runs, and emits JSON-lines with variable values.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: insightor <input.cs> <output.jsonl> [--args <program-args...>]");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var programArgs = Array.Empty<string>();
var idx = Array.IndexOf(args, "--args");
if (idx >= 0 && idx + 1 < args.Length)
{
    programArgs = args.Skip(idx + 1).ToArray();
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, string.Empty);

var inputText = File.ReadAllText(inputPath);

var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(inputText, Encoding.UTF8), parseOptions, path: inputPath);

var compilation = CSharpCompilation.Create(
    assemblyName: "InstrumentedProgram",
    syntaxTrees: new[] { syntaxTree },
    references: GetTrustedPlatformReferences(),
    options: new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));

var semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);

var rewriter = new ProbeRewriter(semanticModel);
var newRoot = rewriter.Visit(syntaxTree.GetRoot())!;

// Ensure the instrumented tree has the exact same parse options
var instrumentedTree = (CSharpSyntaxTree)syntaxTree.WithRootAndOptions(newRoot, parseOptions);

// Add __Probe helper to the compilation with identical parse options
var probeTree = CSharpSyntaxTree.ParseText(SourceText.From(ProbeSource.GetSource(), Encoding.UTF8), parseOptions);

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
var implicitUsingsTree = CSharpSyntaxTree.ParseText(SourceText.From(implicitUsings + "\n", Encoding.UTF8), parseOptions);

compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(instrumentedTree, probeTree, implicitUsingsTree);

using var peStream = new MemoryStream();
using var pdbStream = new MemoryStream();
var emitResult = compilation.Emit(peStream, pdbStream);
if (!emitResult.Success)
{
    foreach (var diag in emitResult.Diagnostics)
    {
        Console.Error.WriteLine(diag.ToString());
    }
    return 2;
}

peStream.Position = 0;
pdbStream.Position = 0;

// Provide output path for probe
Environment.SetEnvironmentVariable("INSIGHTOR_OUT", outputPath);

var alc = new AssemblyLoadContext("InstrumentedContext", isCollectible: true);
var assembly = alc.LoadFromStream(peStream, pdbStream);

// Find entry point (top-level statements use synthesized Program.Main)
var entry = assembly.EntryPoint;
if (entry is null)
{
    Console.Error.WriteLine("No entry point found in input file.");
    return 3;
}

object?[] parameters;
if (entry.GetParameters().Length == 1)
{
    parameters = new object?[] { programArgs };
}
else
{
    parameters = Array.Empty<object>();
}

try
{
    var result = entry.Invoke(null, parameters);
    if (result is Task t)
    {
        await t.ConfigureAwait(false);
    }
    // Ensure probes flush
    var probeType = assembly.GetType("__Insightor.__Probe");
    probeType?.GetMethod("Flush", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
}
catch (TargetInvocationException ex)
{
    Console.Error.WriteLine(ex.InnerException?.ToString() ?? ex.ToString());
    return 4;
}
finally
{
    alc.Unload();
}

return 0;

static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
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
    var selected = paths.Where(p => preferred.Any(pref => Path.GetFileNameWithoutExtension(p).Equals(pref, StringComparison.OrdinalIgnoreCase)))
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

sealed class ProbeRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    public ProbeRewriter(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        // Build from original members so semantic model nodes match
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is GlobalStatementSyntax gs)
            {
                if (gs.Statement is IfStatementSyntax ifs)
                {
                    var condProbe = TryCreateConditionProbeStatement(ifs);
                    if (condProbe is not null)
                    {
                        newMembers.Add(SyntaxFactory.GlobalStatement(condProbe));
                    }
                }
                var visitedStmt = (StatementSyntax)base.Visit(gs.Statement)!;
                newMembers.Add(SyntaxFactory.GlobalStatement(visitedStmt));
                var probeStmt = TryCreateProbeStatement(gs.Statement);
                if (probeStmt is not null)
                {
                    newMembers.Add(SyntaxFactory.GlobalStatement(probeStmt));
                }
            }
            else
            {
                newMembers.Add((MemberDeclarationSyntax)base.Visit(member)!);
            }
        }
        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // Interleave using original statements for semantic model correctness
        var newStatements = new List<StatementSyntax>();
        foreach (var stmt in node.Statements)
        {
            if (stmt is IfStatementSyntax ifs)
            {
                var condProbe = TryCreateConditionProbeStatement(ifs);
                if (condProbe is not null)
                {
                    newStatements.Add(condProbe);
                }
            }
            var visited = (StatementSyntax)base.Visit(stmt)!;
            newStatements.Add(visited);
            var probeStmt = TryCreateProbeStatement(stmt);
            if (probeStmt is not null)
            {
                newStatements.Add(probeStmt);
            }
        }
        return node.WithStatements(SyntaxFactory.List(newStatements));
    }

    private ExpressionStatementSyntax? TryCreateConditionProbeStatement(IfStatementSyntax ifs)
    {
        var ids = CollectReferencedIdentifiers(ifs.Condition);
        if (ids.Count == 0) return null;
        int line = ifs.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var probe = CreateProbeInvocation(line, ids);
        return SyntaxFactory.ExpressionStatement(probe);
    }

    private ExpressionStatementSyntax? TryCreateProbeStatement(StatementSyntax stmt)
    {
        int line = stmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        List<(string name, ExpressionSyntax expr)> vars;
        switch (stmt)
        {
            case LocalDeclarationStatementSyntax localDecl:
                vars = new List<(string, ExpressionSyntax)>();
                // Declared variable identifiers (e.g., y)
                foreach (var v in localDecl.Declaration.Variables)
                {
                    var name = v.Identifier.Text;
                    vars.Add((name, SyntaxFactory.IdentifierName(name)));
                }
                // Plus any referenced identifiers in initializers (e.g., x in int y = x + 2;)
                var initRefs = CollectReferencedIdentifiers(localDecl.Declaration);
                vars.AddRange(initRefs);
                // Distinct by name
                vars = vars
                    .GroupBy(p => p.name)
                    .Select(g => g.First())
                    .ToList();
                if (vars.Count == 0) return null;
                break;
            case ExpressionStatementSyntax exprStmt:
                vars = CollectReferencedIdentifiers(exprStmt.Expression);
                if (vars.Count == 0) return null;
                break;
            case ReturnStatementSyntax retStmt:
                vars = retStmt.Expression is null ? new List<(string, ExpressionSyntax)>() : CollectReferencedIdentifiers(retStmt.Expression);
                if (vars.Count == 0) return null;
                break;
            default:
                return null;
        }
        var probe = CreateProbeInvocation(line, vars);
        return SyntaxFactory.ExpressionStatement(probe);
    }

    private List<(string name, ExpressionSyntax expr)> CollectReferencedIdentifiers(SyntaxNode node)
    {
        var results = new List<(string name, ExpressionSyntax expr)>();
        foreach (var id in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbol = _semanticModel.GetSymbolInfo(id).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol)
            {
                var name = id.Identifier.Text;
                results.Add((name, SyntaxFactory.IdentifierName(name)));
            }
        }
        return results
            .GroupBy(x => x.name)
            .Select(g => g.First())
            .ToList();
    }

    private static InvocationExpressionSyntax CreateProbeInvocation(int line, List<(string name, ExpressionSyntax expr)> vars)
    {
        var args = new List<ArgumentSyntax> { SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(line))) };
        foreach (var (name, expr) in vars)
        {
            args.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name))));
            args.Add(SyntaxFactory.Argument(expr));
        }
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.QualifiedName(SyntaxFactory.IdentifierName("__Insightor"), SyntaxFactory.IdentifierName("__Probe")),
                SyntaxFactory.IdentifierName("Line")))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
    }
}

static class ProbeSource
{
    public static string GetSource() => """
// Enable nullable context to avoid CS8632 warnings in this helper
#nullable enable
namespace __Insightor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;

    public static class __Probe
    {
        private static readonly object _lock = new object();
        private static StreamWriter? _writer;

        private static StreamWriter Writer
        {
            get
            {
                if (_writer is not null) return _writer;
                lock (_lock)
                {
                    if (_writer is null)
                    {
                        var path = Environment.GetEnvironmentVariable("INSIGHTOR_OUT") ?? "insightor.out.jsonl";
                        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                    }
                }
                return _writer!;
            }
        }

        public static void Line(int line, params object?[] kvs)
        {
            try
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i + 1 < kvs.Length; i += 2)
                {
                    var key = kvs[i]?.ToString() ?? $"_{i}";
                    var val = kvs[i + 1];
                    dict[key] = val;
                }
                var payload = new
                {
                    line,
                    variables = dict
                };
                string json = JsonSerializer.Serialize(payload);
                lock (_lock)
                {
                    Writer.WriteLine(json);
                }
            }
            catch { /* swallow */ }
        }

        public static void Flush()
        {
            lock (_lock)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
""";
}

