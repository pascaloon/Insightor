using System;
using System.Linq;

namespace Insightor.Utils;

/// <summary>
/// Command line argument parser for the Insightor CLI
/// </summary>
public class ArgumentParser
{
    /// <summary>
    /// Parses command line arguments
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>Parsed arguments or invalid result</returns>
    public static ParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new ParseResult { IsValid = false };
        }

        var inputPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        var programArgs = Array.Empty<string>();

        var idx = Array.IndexOf(args, "--args");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            programArgs = args.Skip(idx + 1).ToArray();
        }

        return new ParseResult
        {
            IsValid = true,
            InputPath = inputPath,
            OutputPath = outputPath,
            ProgramArgs = programArgs
        };
    }
}

/// <summary>
/// Result of parsing command line arguments
/// </summary>
public class ParseResult
{
    public bool IsValid { get; set; }
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string[] ProgramArgs { get; set; } = Array.Empty<string>();
}
