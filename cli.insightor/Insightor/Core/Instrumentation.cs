using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Insightor.Core;

/// <summary>
/// C# syntax rewriter that instruments code with probe statements for debugging
/// </summary>
public class ProbeRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private int _returnCounter = 0;

    /// <summary>
    /// Creates a new probe rewriter
    /// </summary>
    /// <param name="semanticModel">The semantic model for the code being instrumented</param>
    public ProbeRewriter(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    /// <summary>
    /// Instruments the compilation unit with probes
    /// </summary>
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
            var endRoot = SyntaxFactory.ExpressionStatement(CreateCallInvocation(false, "Program",
                Math.Max(1, node.GetLocation().GetLineSpan().EndLinePosition.Line + 1)));
            newMembers.Add(SyntaxFactory.GlobalStatement(endRoot));
        }

        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    /// <summary>
    /// Instruments blocks with probes
    /// </summary>
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

    /// <summary>
    /// Instruments method declarations with call tracking
    /// </summary>
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

    /// <summary>
    /// Instruments constructor declarations with call tracking
    /// </summary>
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

    /// <summary>
    /// Instruments local function statements with call tracking
    /// </summary>
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

    /// <summary>
    /// Instruments if statements with condition probes
    /// </summary>
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

    /// <summary>
    /// Instruments simple lambda expressions with return probes
    /// </summary>
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

    /// <summary>
    /// Instruments parenthesized lambda expressions with return probes
    /// </summary>
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

    /// <summary>
    /// Instruments for statements with iteration probes
    /// </summary>
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

    /// <summary>
    /// Instruments while statements with condition probes
    /// </summary>
    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        // Compute variables referenced in the while condition
        var vars = node.Condition is null ?
            new List<(string name, ExpressionSyntax expr)>() :
            CollectReferencedIdentifiers(node.Condition);
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

    /// <summary>
    /// Creates a condition probe statement for if statements
    /// </summary>
    private ExpressionStatementSyntax? TryCreateConditionProbeStatement(IfStatementSyntax ifs)
    {
        var ids = CollectReferencedIdentifiers(ifs.Condition);
        if (ids.Count == 0) return null;

        int line = ifs.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var probe = CreateProbeInvocation(line, ids);
        return SyntaxFactory.ExpressionStatement(probe);
    }

    /// <summary>
    /// Transforms return statements to include probes
    /// </summary>
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

    /// <summary>
    /// Creates a probe statement for various statement types
    /// </summary>
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
                vars = vars.GroupBy(p => p.name).Select(g => g.First()).ToList();
                if (vars.Count == 0) return null;
                break;

            case ExpressionStatementSyntax exprStmt:
                vars = CollectReferencedIdentifiers(exprStmt.Expression);
                if (vars.Count == 0) return null;
                break;

            case ReturnStatementSyntax retStmt:
                vars = retStmt.Expression is null ?
                    new List<(string, ExpressionSyntax)>() :
                    CollectReferencedIdentifiers(retStmt.Expression);
                if (vars.Count == 0) return null;
                break;

            default:
                return null;
        }

        var probe = CreateProbeInvocation(line, vars);
        return SyntaxFactory.ExpressionStatement(probe);
    }

    /// <summary>
    /// Helper method to instrument child statements with probes
    /// </summary>
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

    /// <summary>
    /// Collects referenced identifiers from a syntax node
    /// </summary>
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

        return results.GroupBy(x => x.name).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Creates a probe invocation for line-level probes
    /// </summary>
    private static InvocationExpressionSyntax CreateProbeInvocation(int line, List<(string name, ExpressionSyntax expr)> vars)
    {
        var args = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(line)))
        };

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

    /// <summary>
    /// Creates a call invocation for method start/end probes
    /// </summary>
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

    /// <summary>
    /// Gets a display name for a method symbol
    /// </summary>
    private static string GetMethodDisplayName(IMethodSymbol symbol)
    {
        // Example: Namespace.Type.Method(paramType1, paramType2)
        var containing = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "";
        var method = symbol.Name;
        var parms = string.Join(", ", symbol.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        var qual = string.IsNullOrEmpty(containing) ? method : containing + "." + method;
        return parms.Length > 0 ? $"{qual}({parms})" : $"{qual}()";
    }

    /// <summary>
    /// Gets parameter bindings for method calls
    /// </summary>
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
