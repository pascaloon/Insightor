using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace Insightor.Core;

/// <summary>
/// Executes compiled assemblies with runtime instrumentation
/// </summary>
public static class RuntimeExecutor
{
    /// <summary>
    /// Executes a compiled assembly with instrumentation
    /// </summary>
    /// <param name="peStream">The compiled assembly PE stream</param>
    /// <param name="pdbStream">The debug symbols PDB stream</param>
    /// <param name="outputPath">Path to write probe output</param>
    /// <param name="programArgs">Arguments to pass to the program</param>
    /// <returns>Exit code from the executed program</returns>
    public static async Task<int> ExecuteAsync(
        Stream peStream,
        Stream pdbStream,
        string outputPath,
        string[] programArgs)
    {
        // Provide output path for probe
        Environment.SetEnvironmentVariable("INSIGHTOR_OUT", outputPath);

        var alc = new AssemblyLoadContext("InstrumentedContext", isCollectible: true);
        var assembly = alc.LoadFromStream(peStream, pdbStream);

        // Find entry point (top-level statements use synthesized Program.Main)
        var entry = assembly.EntryPoint;
        if (entry is null)
        {
            System.Console.Error.WriteLine("No entry point found in input file.");
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

            return 0;
        }
        catch (TargetInvocationException ex)
        {
            System.Console.Error.WriteLine(ex.InnerException?.ToString() ?? ex.ToString());
            return 4;
        }
        finally
        {
            alc.Unload();
        }
    }
}
