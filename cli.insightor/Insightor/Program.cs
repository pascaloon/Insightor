using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Insightor.Core;
using Insightor.Utils;

/// <summary>
/// Insightor CLI: instruments a C# file with probe statements, compiles, runs, and emits JSON-lines with variable values.
/// Main entry point that orchestrates the entire process.
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            // Parse command line arguments
            var arguments = ArgumentParser.Parse(args);
            if (!arguments.IsValid)
            {
                Console.Error.WriteLine("Usage: insightor <input.cs> <output.jsonl> [--args <program-args...>]");
                return 1;
            }

            // Initialize output file
            Directory.CreateDirectory(Path.GetDirectoryName(arguments.OutputPath)!);
            File.WriteAllText(arguments.OutputPath, string.Empty);

            // Load and parse input file
            var inputText = File.ReadAllText(arguments.InputPath);
            var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
            var syntaxTree = CSharpSyntaxTree.ParseText(
                SourceText.From(inputText, System.Text.Encoding.UTF8),
                parseOptions,
                path: arguments.InputPath
            );

            // Create compilation with instrumentation
            var compilation = CompilationBuilder.CreateInstrumentedCompilation(syntaxTree, arguments.InputPath);

            // Compile to memory
            var peStream = new MemoryStream();
            var pdbStream = new MemoryStream();

            try
            {
                var emitResult = compilation.Emit(peStream, pdbStream);

                if (!emitResult.Success)
                {
                    CompilationErrors.Handle(emitResult.Diagnostics);
                    return 2;
                }

                // Reset streams to beginning for loading
                peStream.Position = 0;
                pdbStream.Position = 0;

                // Execute the instrumented program
                var exitCode = await RuntimeExecutor.ExecuteAsync(
                    peStream,
                    pdbStream,
                    arguments.OutputPath,
                    arguments.ProgramArgs
                );

                return exitCode;
            }
            finally
            {
                peStream.Dispose();
                pdbStream.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return -1;
        }
    }
}

