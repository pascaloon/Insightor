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
    private int _returnCounter = 0;
    public ProbeRewriter(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        // Build from original members so semantic model nodes match
        var newMembers = new List<MemberDeclarationSyntax>();
        // Emit a synthetic root call start for top-level program
        {
            var startRoot = SyntaxFactory.ExpressionStatement(CreateCallInvocation(true, "Program", 1));
            newMembers.Add(SyntaxFactory.GlobalStatement(startRoot));
        }
        foreach (var member in node.Members)
        {
            if (member is GlobalStatementSyntax gs)
            {
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
        // Emit a synthetic root call end for top-level program
        {
            var endRoot = SyntaxFactory.ExpressionStatement(CreateCallInvocation(false, "Program", Math.Max(1, node.GetLocation().GetLineSpan().EndLinePosition.Line + 1)));
            newMembers.Add(SyntaxFactory.GlobalStatement(endRoot));
        }
        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // Interleave using original statements for semantic model correctness
        var newStatements = new List<StatementSyntax>();
        foreach (var stmt in node.Statements)
        {
            var visited = (StatementSyntax)base.Visit(stmt)!;
            if (stmt is ReturnStatementSyntax ret)
            {
                var transformed = TransformReturnStatement(ret);
                if (transformed is not null)
                {
                    newStatements.Add(transformed);
                    continue;
                }
                // fallback (no expression)
                var probeBefore = TryCreateProbeStatement(stmt);
                if (probeBefore is not null) newStatements.Add(probeBefore);
                newStatements.Add(visited);
                continue;
            }
            newStatements.Add(visited);
            var probeAfter = TryCreateProbeStatement(stmt);
            if (probeAfter is not null) newStatements.Add(probeAfter);
        }
        return node.WithStatements(SyntaxFactory.List(newStatements));
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // Visit children first to preserve existing instrumentation inside method body
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        string methodId = methodSymbol is null ? node.Identifier.Text : GetMethodDisplayName(methodSymbol);
        int methodLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Handle expression-bodied methods by converting to a block wrapped in try/finally
        if (visited.ExpressionBody is not null)
        {
            // Build: { __Probe.CallStart(methodId, line); try { return <expr>; } finally { __Probe.CallEnd(methodId, line); } }
            var startInv = CreateCallInvocation(true, methodId, methodLine, GetParameterBindings(node.ParameterList));
            var endInv = CreateCallInvocation(false, methodId, methodLine);
            var tryBlock = SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(visited.ExpressionBody.Expression)
            );
            var finallyBlock = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(endInv));
            var tryFinally = SyntaxFactory.TryStatement(tryBlock, default, SyntaxFactory.FinallyClause(finallyBlock));
            var newBody = SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(startInv),
                tryFinally
            );
            return visited.WithBody(newBody).WithExpressionBody(null).WithSemicolonToken(default);
        }

        if (visited.Body is null)
        {
            return visited;
        }

        // Wrap existing body in try/finally with CallStart/CallEnd
        var startCall = SyntaxFactory.ExpressionStatement(CreateCallInvocation(true, methodId, methodLine, GetParameterBindings(node.ParameterList)));
        var endCall = SyntaxFactory.ExpressionStatement(CreateCallInvocation(false, methodId, methodLine));
        var tryBody = SyntaxFactory.Block(visited.Body.Statements);
        var finallyBody = SyntaxFactory.Block(endCall);
        var wrapped = SyntaxFactory.Block(
            startCall,
            SyntaxFactory.TryStatement(tryBody, default, SyntaxFactory.FinallyClause(finallyBody))
        );
        return visited.WithBody(wrapped);
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        int methodLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        string methodId = methodSymbol is null ? node.Identifier.Text : GetMethodDisplayName(methodSymbol);

        if (visited.Body is null)
        {
            return visited;
        }

        var startCall = SyntaxFactory.ExpressionStatement(CreateCallInvocation(true, methodId, methodLine, GetParameterBindings(node.ParameterList)));
        var endCall = SyntaxFactory.ExpressionStatement(CreateCallInvocation(false, methodId, methodLine));
        var tryBody = SyntaxFactory.Block(visited.Body.Statements);
        var finallyBody = SyntaxFactory.Block(endCall);
        var wrapped = SyntaxFactory.Block(
            startCall,
            SyntaxFactory.TryStatement(tryBody, default, SyntaxFactory.FinallyClause(finallyBody))
        );
        return visited.WithBody(wrapped);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        int methodLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var symbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        string methodId = symbol is null ? node.Identifier.Text : GetMethodDisplayName(symbol);

        if (visited.ExpressionBody is not null)
        {
            var startInv = CreateCallInvocation(true, methodId, methodLine, GetParameterBindings(node.ParameterList));
            var endInv = CreateCallInvocation(false, methodId, methodLine);
            var tryBlock = SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(visited.ExpressionBody.Expression)
            );
            var finallyBlock = SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(endInv));
            var tryFinally = SyntaxFactory.TryStatement(tryBlock, default, SyntaxFactory.FinallyClause(finallyBlock));
            var newBody = SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(startInv),
                tryFinally
            );
            return visited.WithBody(newBody).WithExpressionBody(null).WithSemicolonToken(default);
        }

        if (visited.Body is null)
        {
            return visited;
        }

        var startCall = SyntaxFactory.ExpressionStatement(CreateCallInvocation(true, methodId, methodLine, GetParameterBindings(node.ParameterList)));
        var endCall = SyntaxFactory.ExpressionStatement(CreateCallInvocation(false, methodId, methodLine));
        var tryBody = SyntaxFactory.Block(visited.Body.Statements);
        var finallyBody = SyntaxFactory.Block(endCall);
        var wrapped = SyntaxFactory.Block(
            startCall,
            SyntaxFactory.TryStatement(tryBody, default, SyntaxFactory.FinallyClause(finallyBody))
        );
        return visited.WithBody(wrapped);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        // Compute probe from ORIGINAL node to match the semantic model
        var condProbe = TryCreateConditionProbeStatement(node);
        // Visit children next
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        // Ensure single-statement bodies are instrumented with post/return probes
        var newThen = InstrumentChildWithProbe(node.Statement, visited.Statement);
        IfStatementSyntax updated = visited.WithStatement(newThen);
        if (visited.Else is not null && node.Else is not null)
        {
            // If this is an else-if, leave it to recursive VisitIfStatement
            if (node.Else.Statement is IfStatementSyntax)
            {
                // keep as visited
            }
            else
            {
                var newElseStmt = InstrumentChildWithProbe(node.Else.Statement, visited.Else.Statement);
                updated = updated.WithElse(visited.Else.WithStatement(newElseStmt));
            }
        }
        if (condProbe is null)
        {
            return updated;
        }
        // Wrap with a block that emits the condition probe, then evaluates the if
        return SyntaxFactory.Block(condProbe, updated);
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        // Transform expression-bodied lambda to emit return probe
        if (node.Body is ExpressionSyntax expr)
        {
            var type = _semanticModel.GetTypeInfo(expr).ConvertedType;
            if (type is not null && type.SpecialType != SpecialType.System_Void)
            {
                int line = expr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                string tempName = "__insightor_ret" + (++_returnCounter).ToString();
                var tempId = SyntaxFactory.IdentifierName(tempName);
                var decl = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName("var"),
                        SyntaxFactory.SeparatedList(new[] {
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(tempName))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(expr))
                        })
                    )
                );
                var vars = new List<(string name, ExpressionSyntax expr)> { ("return", tempId) };
                vars.AddRange(CollectReferencedIdentifiers(expr));
                vars = vars.GroupBy(v => v.name).Select(g => g.First()).ToList();
                var probe = CreateProbeInvocation(line, vars);
                var body = SyntaxFactory.Block(
                    decl,
                    SyntaxFactory.ExpressionStatement(probe),
                    SyntaxFactory.ReturnStatement(tempId)
                );
                // Do not visit expr twice; replace body directly
                return node.WithBody(body);
            }
        }
        return base.VisitSimpleLambdaExpression(node);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        if (node.Body is ExpressionSyntax expr)
        {
            var type = _semanticModel.GetTypeInfo(expr).ConvertedType;
            if (type is not null && type.SpecialType != SpecialType.System_Void)
            {
                int line = expr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                string tempName = "__insightor_ret" + (++_returnCounter).ToString();
                var tempId = SyntaxFactory.IdentifierName(tempName);
                var decl = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName("var"),
                        SyntaxFactory.SeparatedList(new[] {
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(tempName))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(expr))
                        })
                    )
                );
                var vars = new List<(string name, ExpressionSyntax expr)> { ("return", tempId) };
                vars.AddRange(CollectReferencedIdentifiers(expr));
                vars = vars.GroupBy(v => v.name).Select(g => g.First()).ToList();
                var probe = CreateProbeInvocation(line, vars);
                var body = SyntaxFactory.Block(
                    decl,
                    SyntaxFactory.ExpressionStatement(probe),
                    SyntaxFactory.ReturnStatement(tempId)
                );
                return node.WithBody(body);
            }
        }
        return base.VisitParenthesizedLambdaExpression(node);
    }

    private StatementSyntax InstrumentChildWithProbe(StatementSyntax original, StatementSyntax visited)
    {
        if (original is ReturnStatementSyntax ret)
        {
            var transformed = TransformReturnStatement(ret);
            if (transformed is not null) return transformed;
        }
        var probe = TryCreateProbeStatement(original);
        if (probe is null)
        {
            return visited;
        }
        if (original is ReturnStatementSyntax)
        {
            // Pre-return probe
            return SyntaxFactory.Block(probe, visited);
        }
        else
        {
            // Post-statement probe
            return SyntaxFactory.Block(visited, probe);
        }
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        // Collect variables to display per-iteration (header line attribution)
        var vars = new List<(string name, ExpressionSyntax expr)>();
        if (node.Declaration is VariableDeclarationSyntax decl)
        {
            foreach (var v in decl.Variables)
            {
                var name = v.Identifier.Text;
                vars.Add((name, SyntaxFactory.IdentifierName(name)));
            }
            vars.AddRange(CollectReferencedIdentifiers(decl));
        }
        foreach (var init in node.Initializers)
        {
            vars.AddRange(CollectReferencedIdentifiers(init));
        }
        if (node.Condition is ExpressionSyntax cond)
        {
            vars.AddRange(CollectReferencedIdentifiers(cond));
        }
        foreach (var inc in node.Incrementors)
        {
            vars.AddRange(CollectReferencedIdentifiers(inc));
        }
        vars = vars.GroupBy(v => v.name).Select(g => g.First()).ToList();

        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        if (vars.Count > 0)
        {
            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var probe = CreateProbeInvocation(line, vars);
            var probeStmt = SyntaxFactory.ExpressionStatement(probe);
            if (visitedBody is BlockSyntax vb)
            {
                var newStmts = new List<StatementSyntax>();
                newStmts.Add(probeStmt);
                newStmts.AddRange(vb.Statements);
                visitedBody = vb.WithStatements(SyntaxFactory.List(newStmts));
            }
            else
            {
                visitedBody = SyntaxFactory.Block(probeStmt, visitedBody);
            }
        }

        return node.WithStatement(visitedBody);
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        // Compute variables referenced in the while condition
        var vars = node.Condition is null ? new List<(string name, ExpressionSyntax expr)>() : CollectReferencedIdentifiers(node.Condition);
        vars = vars.GroupBy(v => v.name).Select(g => g.First()).ToList();

        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        if (vars.Count > 0)
        {
            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var probe = CreateProbeInvocation(line, vars);
            var probeStmt = SyntaxFactory.ExpressionStatement(probe);
            if (visitedBody is BlockSyntax vb)
            {
                var newStmts = new List<StatementSyntax>();
                newStmts.Add(probeStmt);
                newStmts.AddRange(vb.Statements);
                visitedBody = vb.WithStatements(SyntaxFactory.List(newStmts));
            }
            else
            {
                visitedBody = SyntaxFactory.Block(probeStmt, visitedBody);
            }
        }

        return node.WithStatement(visitedBody);
    }

    private ExpressionStatementSyntax? TryCreateConditionProbeStatement(IfStatementSyntax ifs)
    {
        var ids = CollectReferencedIdentifiers(ifs.Condition);
        if (ids.Count == 0) return null;
        int line = ifs.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var probe = CreateProbeInvocation(line, ids);
        return SyntaxFactory.ExpressionStatement(probe);
    }

    private StatementSyntax? TransformReturnStatement(ReturnStatementSyntax ret)
    {
        if (ret.Expression is null) return null;
        int line = ret.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        string tempName = "__insightor_ret" + (++_returnCounter).ToString();
        var tempId = SyntaxFactory.IdentifierName(tempName);

        // var __insightor_retN = <expr>;
        var decl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SeparatedList(new[] {
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(tempName))
                    .WithInitializer(
                        SyntaxFactory.EqualsValueClause(ret.Expression!))
                })
            )
        );

        // Build variables list: return value first, then referenced identifiers
        var vars = new List<(string name, ExpressionSyntax expr)>
        {
            ("return", tempId)
        };
        vars.AddRange(CollectReferencedIdentifiers(ret.Expression!));
        vars = vars.GroupBy(v => v.name).Select(g => g.First()).ToList();
        var probe = CreateProbeInvocation(line, vars);

        var probeStmt = SyntaxFactory.ExpressionStatement(probe);
        var returnStmt = SyntaxFactory.ReturnStatement(tempId);
        return SyntaxFactory.Block(decl, probeStmt, returnStmt);
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

        bool IsLambdaBoundary(SyntaxNode n) =>
            n is SimpleLambdaExpressionSyntax
            || n is ParenthesizedLambdaExpressionSyntax
            || n is AnonymousMethodExpressionSyntax
            || n is LocalFunctionStatementSyntax;

        void Traverse(SyntaxNode n)
        {
            if (IsLambdaBoundary(n))
            {
                // Do not traverse into new parameter scope; outer probe cannot reference those names
                return;
            }
            if (n is IdentifierNameSyntax id)
            {
                var symbol = _semanticModel.GetSymbolInfo(id).Symbol;
                if (symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol)
                {
                    var name = id.Identifier.Text;
                    results.Add((name, SyntaxFactory.IdentifierName(name)));
                }
            }
            foreach (var child in n.ChildNodes()) Traverse(child);
        }

        Traverse(node);

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

    private static InvocationExpressionSyntax CreateCallInvocation(bool isStart, string methodId, int line, IEnumerable<(string name, ExpressionSyntax expr)>? vars = null)
    {
        var methodName = isStart ? "CallStart" : "CallEnd";
        var args = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(methodId))),
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(line)))
        };
        if (isStart && vars is not null)
        {
            foreach (var (name, expr) in vars)
            {
                args.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name))));
                args.Add(SyntaxFactory.Argument(expr));
            }
        }
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.QualifiedName(SyntaxFactory.IdentifierName("__Insightor"), SyntaxFactory.IdentifierName("__Probe")),
                SyntaxFactory.IdentifierName(methodName)))
            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args)));
    }

    private static string GetMethodDisplayName(IMethodSymbol symbol)
    {
        // Example: Namespace.Type.Method(paramType1, paramType2)
        var containing = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "";
        var method = symbol.Name;
        var parms = string.Join(", ", symbol.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        var qual = string.IsNullOrEmpty(containing) ? method : containing + "." + method;
        return parms.Length > 0 ? $"{qual}({parms})" : $"{qual}()";
    }

    private static IEnumerable<(string name, ExpressionSyntax expr)> GetParameterBindings(BaseParameterListSyntax? parameterList)
    {
        var bindings = new List<(string, ExpressionSyntax)>();
        if (parameterList is null) return bindings;
        foreach (var p in parameterList.Parameters)
        {
            var name = p.Identifier.Text;
            if (string.IsNullOrEmpty(name)) continue;
            bindings.Add((name, SyntaxFactory.IdentifierName(name)));
        }
        return bindings;
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
                    type = "line",
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

        public static void CallStart(string method, int line, params object?[] kvs)
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
                var payload = new { type = "callStart", method, line, variables = dict };
                string json = JsonSerializer.Serialize(payload);
                lock (_lock)
                {
                    Writer.WriteLine(json);
                }
            }
            catch { /* swallow */ }
        }

        public static void CallEnd(string method, int line)
        {
            try
            {
                var payload = new { type = "callEnd", method, line, variables = new Dictionary<string, object?>() };
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

