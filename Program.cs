using MacroDeck.Localization;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;

namespace MultiDisplayVCPClient
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            await MacroDeckPlugin.CreatePlugin(args)
                .UseMacroDeckLogging()
                .UseLocalization(Strings.LocalizationCatalog)
                .RegisterIntegration<VcpPluginIntegration>()
                .Build()
                .RunAsync();
        }
    }
}
