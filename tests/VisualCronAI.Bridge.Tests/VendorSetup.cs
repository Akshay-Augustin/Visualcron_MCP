using System;
using System.Runtime.CompilerServices;

namespace System.Runtime.CompilerServices
{
    // Polyfill so the C# compiler emits a module initializer on net48.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace VisualCronAI.Bridge.Tests
{
    internal static class VendorSetup
    {
        /// <summary>Installs the vendor assembly resolver before any test touches a VisualCron type.</summary>
        [ModuleInitializer]
        internal static void Init()
        {
            string dir = Environment.GetEnvironmentVariable("VISUALCRON_INSTALL_DIR") ?? @"C:\Program Files (x86)\VisualCron";
            VendorAssemblyResolver.Install(dir);
        }
    }
}
