using System;
using System.Diagnostics;

namespace Insightor.Tests;

internal static class TestUtils
{
    private static readonly object _buildLock = new object();
    private static bool _built;

    public static void EnsureBuilt(string cliProj)
    {
        if (_built) return;
        lock (_buildLock)
        {
            if (_built) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var build = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{cliProj}\" -c Debug /p:UseSharedCompilation=false /nr:false",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var bp = Process.Start(build)!;
                var bo = bp.StandardOutput.ReadToEnd();
                var be = bp.StandardError.ReadToEnd();
                bp.WaitForExit(60000);
                if (bp.ExitCode == 0)
                {
                    _built = true;
                    return;
                }
                if (be.Contains("CS2012") || be.Contains("used by another process"))
                {
                    System.Threading.Thread.Sleep(800);
                    continue;
                }
                throw new Exception("Build failed.\n" + bo + "\n" + be);
            }
            throw new Exception("Build failed after retries due to file lock.");
        }
    }
}


