using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace VisualCronAI.Bridge
{
    /// <summary>
    /// Entry point. Vendor assemblies are resolved from the VisualCron install directory, so this class must not
    /// reference any VisualCron type directly (the JIT would try to load them before the resolver is installed).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string installDir = ResolveInstallDir(args);
            VendorAssemblyResolver.Install(installDir);

            // Keep a private handle to the real stdout for the JSON protocol, then redirect Console.Out to stderr
            // so nothing the vendor API prints can ever corrupt the protocol stream.
            var protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var protocolIn = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            Console.SetOut(Console.Error);

            try
            {
                return Run(installDir, protocolIn, protocolOut);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Fatal bridge error: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string installDir, TextReader input, TextWriter output)
        {
            using (var host = new BridgeHost(installDir, input, output))
            {
                host.RunLoop();
            }
            return 0;
        }

        private static string ResolveInstallDir(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--install-dir", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            string? env = Environment.GetEnvironmentVariable("VISUALCRON_INSTALL_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env!;
            return @"C:\Program Files (x86)\VisualCron";
        }
    }

    /// <summary>
    /// Loads VisualCron assemblies (and their dependencies) from the install directory by simple name. This acts as
    /// a universal binding redirect so the bridge works with whatever VisualCron 12.x build is installed.
    /// </summary>
    public static class VendorAssemblyResolver
    {
        private static string _installDir = "";
        private static bool _installed;

        public static string InstallDir => _installDir;

        public static void Install(string installDir)
        {
            if (_installed) return;
            if (!Directory.Exists(installDir))
                throw new DirectoryNotFoundException("VisualCron install directory not found: " + installDir +
                    ". Set VISUALCRON_INSTALL_DIR or pass --install-dir.");
            _installDir = installDir;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            _installed = true;
        }

        private static Assembly? OnResolve(object? sender, ResolveEventArgs e)
        {
            string simpleName = new AssemblyName(e.Name).Name ?? "";
            if (simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase)) return loaded;
            }
            string candidate = Path.Combine(_installDir, simpleName + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            candidate = Path.Combine(_installDir, simpleName + ".exe");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            return null;
        }
    }

    internal static class BridgeLog
    {
        // stderr only. Never log secrets: callers pass only identifiers and messages.
        public static void Info(string message) => Write("INF", message);
        public static void Warn(string message) => Write("WRN", message);
        public static void Error(string message) => Write("ERR", message);

        private static void Write(string level, string message)
        {
            try { Console.Error.WriteLine(DateTime.UtcNow.ToString("o") + " [bridge] " + level + " " + message); }
            catch { /* stderr closed */ }
        }
    }
}
