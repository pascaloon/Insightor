using System.IO;
using System.Text;
using System.Text.Json;

namespace Insightor.Core;

/// <summary>
/// Source code for the runtime probe helper
/// </summary>
public static class ProbeSource
{
    /// <summary>
    /// Gets the source code for the __Insightor.__Probe class
    /// </summary>
    /// <returns>C# source code as a string</returns>
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
