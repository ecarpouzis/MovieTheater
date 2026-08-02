using System;
using System.IO;

namespace MovieTheater.Tools.ExtractCoreOptions
{
    /// <summary>
    /// Two subcommands:
    ///   extract --dll &lt;path&gt; --out &lt;file&gt; [--timeout &lt;sec&gt;] [--tmp &lt;dir&gt;]
    ///       Load ONE core DLL and dump what it declares. Run per-core by the PowerShell driver so a
    ///       crash is isolated to one core (and recorded as a crash, never as "no options").
    ///   build --extract-dir &lt;dir&gt; --policy &lt;policy.json&gt; --old &lt;catalog.json&gt; --config &lt;config.worker-gl.yaml&gt;
    ///         --out &lt;catalog.json&gt; --report &lt;drift.md&gt;
    ///       Fold every per-core extraction into the site's core-options-catalog.json (honouring the
    ///       per-core policy) and write the drift report.
    /// </summary>
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            if (args.Length == 0) { Usage(); return 2; }
            try
            {
                switch (args[0])
                {
                    case "extract": return Harness.Run(new Args(args));
                    case "build": return CatalogBuilder.Run(new Args(args));
                    default: Usage(); return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        private static void Usage()
        {
            Console.Error.WriteLine("usage: extract-core-options extract --dll <path> --out <file> [--timeout <sec>] [--tmp <dir>]");
            Console.Error.WriteLine("       extract-core-options build --extract-dir <dir> --policy <f> --old <f> --config <f> --out <f> --report <f>");
        }
    }

    internal sealed class Args
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _v =
            new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] args)
        {
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                var key = args[i].Substring(2);
                var val = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                _v[key] = val;
            }
        }

        public string Get(string name, string fallback = null)
        {
            if (_v.TryGetValue(name, out var v)) return v;
            if (fallback != null) return fallback;
            throw new ArgumentException("missing required argument --" + name);
        }

        public int GetInt(string name, int fallback) =>
            _v.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : fallback;

        public string GetPath(string name, string fallback = null)
        {
            var p = Get(name, fallback);
            return p == null ? null : Path.GetFullPath(p);
        }
    }
}
