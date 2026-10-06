using System.Globalization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Interactive setup flow for creating or editing VCP Server connection entries in Macro Deck 3.
    /// </summary>
    public sealed class VcpConfigFlow : IConfigFlow
    {
        private readonly ILogger _logger;
        private readonly VcpPluginIntegration _integration;

        public VcpConfigFlow(ILogger logger, VcpPluginIntegration integration)
        {
            _logger = logger.ForContext<VcpConfigFlow>();
            _integration = integration;
        }

        public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(ConfigFlowResult.Step(ConnectionStep(context, _integration)));
        }

        public async Task<ConfigFlowResult> SubmitAsync(
            string stepId,
            IReadOnlyDictionary<string, object?> input,
            IConfigFlowContext context,
            CancellationToken cancellationToken)
        {
            string name = (input.GetValueOrDefault("name") as string ?? string.Empty).Trim();
            string host = (input.GetValueOrDefault("host") as string ?? string.Empty).Trim();
            string portStr = input.GetValueOrDefault("port")?.ToString() ?? "5002";
            string password = input.GetValueOrDefault("password") as string ?? string.Empty;

            if (string.IsNullOrWhiteSpace(name))
            {
                name = (context as IConfigFlowEntryContext)?.EntryTitle ?? host;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                return ConfigFlowResult.Error(ConnectionStep(context, _integration), "Host / IP Address is required.");
            }

            if (!int.TryParse(portStr, CultureInfo.InvariantCulture, out int port) || port <= 0 || port > 65535)
            {
                port = 5002;
            }

            // Test connectivity before saving
            var testClient = new VcpClient(name, host, port, password);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                bool connected = await testClient.ConnectAsync(cts.Token);
                if (!connected)
                {
                    return ConfigFlowResult.Error(ConnectionStep(context, _integration), Strings.Setup.CannotConnect());
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Setup flow connection test failed: {Msg}", ex.Message);
                return ConfigFlowResult.Error(ConnectionStep(context, _integration), $"{Strings.Setup.CannotConnect()} ({ex.Message})");
            }
            finally
            {
                testClient.Disconnect();
            }

            // Save connection in runtime integration
            await _integration.AddOrUpdateClientAsync(name, host, port, password);

            // Persist connection parameters in Macro Deck 3 secure storage
            var values = new Dictionary<string, ConfigFlowValue>
            {
                ["host"] = ConfigFlowValue.Plain(host),
                ["port"] = ConfigFlowValue.Plain(port.ToString(CultureInfo.InvariantCulture)),
                ["password"] = ConfigFlowValue.Secret(password)
            };

            return ConfigFlowResult.Complete(name, values);
        }

        private static ConfigFlowStep ConnectionStep(IConfigFlowContext context, VcpPluginIntegration integration)
        {
            var fields = new List<ActionParameter>();
            string defaultHost = "";
            int defaultPort = 5002;

            if (context is IConfigFlowEntryContext entryContext && !string.IsNullOrWhiteSpace(entryContext.EntryTitle))
            {
                var existing = integration.GetClient(entryContext.EntryTitle);
                if (existing != null)
                {
                    defaultHost = existing.Host;
                    defaultPort = existing.Port;
                }
            }
            else
            {
                fields.Add(ActionParameter.Text(
                    "name",
                    label: Strings.Setup.Name(),
                    placeholder: "Main Desktop",
                    required: true));
            }

            fields.Add(ActionParameter.Text(
                "host",
                label: Strings.Setup.Host(),
                placeholder: "192.168.1.100",
                defaultValue: defaultHost,
                required: true));

            fields.Add(ActionParameter.Number(
                "port",
                label: Strings.Setup.Port(),
                defaultValue: defaultPort,
                min: 1,
                max: 65535));

            fields.Add(ActionParameter.Secret(
                "password",
                label: Strings.Setup.Password(),
                required: true));

            return new ConfigFlowStep
            {
                StepId = "connection",
                Title = Strings.Setup.ConnectionTitle(),
                Description = Strings.Setup.ConnectionDescription(),
                Fields = fields
            };
        }
    }
}
