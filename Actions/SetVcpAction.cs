using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MultiDisplayVCPServer.Shared;
using Serilog;

namespace MultiDisplayVCPClient.Actions
{
    /// <summary>
    /// Macro Deck 3 action allowing buttons to set VCP feature values (e.g. brightness, contrast, input)
    /// on any monitor across any connected VCP server.
    /// </summary>
    public sealed class SetVcpAction : IActionDefinition, IDynamicOptionsActionDefinition
    {
        private const string ConnectionParam = "connection";
        private const string MonitorParam = "monitor";
        private const string VcpCodeParam = "vcp_code";
        private const string ValueParam = "value";

        private readonly ILogger _logger;
        private readonly VcpPluginIntegration _integration;

        public SetVcpAction(ILogger logger, VcpPluginIntegration integration)
        {
            _logger = logger.ForContext<SetVcpAction>();
            _integration = integration;
        }

        public string Id => "set-vcp";

        public LocalizedText Name => Strings.Actions.SetVcp.Name();
        public LocalizedText Description => Strings.Actions.SetVcp.Description();

        public IReadOnlyList<ActionParameter> Parameters { get; } =
        [
            ActionParameter.DynamicChoice(
                ConnectionParam,
                label: Strings.Actions.SetVcp.Connection.Label(),
                description: Strings.Actions.SetVcp.Connection.Description(),
                placeholder: Strings.Actions.SetVcp.Connection.Placeholder(),
                required: true),
            ActionParameter.DynamicChoice(
                MonitorParam,
                label: Strings.Actions.SetVcp.Monitor.Label(),
                description: Strings.Actions.SetVcp.Monitor.Description(),
                placeholder: Strings.Actions.SetVcp.Monitor.Placeholder(),
                required: true),
            ActionParameter.DynamicChoice(
                VcpCodeParam,
                label: Strings.Actions.SetVcp.VcpCode.Label(),
                description: Strings.Actions.SetVcp.VcpCode.Description(),
                placeholder: Strings.Actions.SetVcp.VcpCode.Placeholder(),
                required: true),
            ActionParameter.Number(
                ValueParam,
                label: Strings.Actions.SetVcp.Value.Label(),
                description: Strings.Actions.SetVcp.Value.Description(),
                defaultValue: 50,
                min: 0,
                max: 100)
        ];

        public MacroDeckPlatform Platforms => MacroDeckPlatform.All;

        public async Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
        {
            var options = new List<ActionParameterOption>();

            if (context.ParameterName == ConnectionParam)
            {
                var clients = _integration.GetClients();
                foreach (var name in clients.Keys)
                {
                    options.Add(new ActionParameterOption
                    {
                        Value = name,
                        Label = LocalizedText.FromLiteral(name)
                    });
                }
            }
            else if (context.ParameterName == MonitorParam)
            {
                string? connName = context.CurrentParameters.TryGetValue(ConnectionParam, out var cObj) ? cObj?.ToString() : null;

                // If specific connection is selected, get its monitors
                if (!string.IsNullOrWhiteSpace(connName))
                {
                    var client = _integration.GetClient(connName);
                    if (client != null)
                    {
                        var caps = client.CachedCapabilities;
                        if (caps == null || caps.Monitors.Count == 0)
                        {
                            try { caps = await client.GetCapabilitiesAsync(cancellationToken); } catch { }
                        }

                        if (caps?.Monitors != null)
                        {
                            foreach (var m in caps.Monitors)
                            {
                                string label = string.IsNullOrWhiteSpace(m.Description)
                                    ? m.DeviceID
                                    : $"{m.Description} ({m.DeviceID})";

                                options.Add(new ActionParameterOption
                                {
                                    Value = m.DeviceID,
                                    Label = LocalizedText.FromLiteral(label)
                                });
                            }
                        }
                    }
                }
                else
                {
                    // No connection selected yet: if only 1 client exists, use it; otherwise list all monitors across all connections
                    var allClients = _integration.GetClients();
                    foreach (var kvp in allClients)
                    {
                        var client = kvp.Value;
                        var caps = client.CachedCapabilities;
                        if (caps == null || caps.Monitors.Count == 0)
                        {
                            try { caps = await client.GetCapabilitiesAsync(cancellationToken); } catch { }
                        }

                        if (caps?.Monitors != null)
                        {
                            foreach (var m in caps.Monitors)
                            {
                                string label = allClients.Count > 1
                                    ? $"{m.Description} ({m.DeviceID}) - [{client.Name}]"
                                    : $"{m.Description} ({m.DeviceID})";

                                options.Add(new ActionParameterOption
                                {
                                    Value = m.DeviceID,
                                    Label = LocalizedText.FromLiteral(label)
                                });
                            }
                        }
                    }
                }
            }
            else if (context.ParameterName == VcpCodeParam)
            {
                string? connName = context.CurrentParameters.TryGetValue(ConnectionParam, out var cObj) ? cObj?.ToString() : null;
                string? monitorId = context.CurrentParameters.TryGetValue(MonitorParam, out var mObj) ? mObj?.ToString() : null;

                VcpClient? targetClient = null;
                if (!string.IsNullOrWhiteSpace(connName))
                {
                    targetClient = _integration.GetClient(connName);
                }
                else if (_integration.GetClients().Count == 1)
                {
                    targetClient = _integration.GetClients().Values.FirstOrDefault();
                }

                MonitorInfoDto? targetMon = null;
                if (targetClient != null)
                {
                    var caps = targetClient.CachedCapabilities;
                    if (caps == null || caps.Monitors.Count == 0)
                    {
                        try { caps = await targetClient.GetCapabilitiesAsync(cancellationToken); } catch { }
                    }

                    targetMon = caps?.Monitors.FirstOrDefault(m =>
                        !string.IsNullOrWhiteSpace(monitorId) && m.DeviceID.Equals(monitorId, StringComparison.OrdinalIgnoreCase));
                }

                if (targetMon != null && targetMon.Capabilities.Count > 0)
                {
                    foreach (var f in targetMon.Capabilities)
                    {
                        options.Add(new ActionParameterOption
                        {
                            Value = $"0x{f.Code:X2}",
                            Label = LocalizedText.FromLiteral($"{f.Name} (0x{f.Code:X2})")
                        });
                    }
                }
                else
                {
                    // Fallback to standard common VCP features if monitor not yet selected or offline
                    options.Add(new ActionParameterOption { Value = "0x10", Label = LocalizedText.FromLiteral("Luminance / Brightness (0x10)") });
                    options.Add(new ActionParameterOption { Value = "0x12", Label = LocalizedText.FromLiteral("Contrast (0x12)") });
                    options.Add(new ActionParameterOption { Value = "0x60", Label = LocalizedText.FromLiteral("Input Source Select (0x60)") });
                    options.Add(new ActionParameterOption { Value = "0x62", Label = LocalizedText.FromLiteral("Audio Speaker Volume (0x62)") });
                    options.Add(new ActionParameterOption { Value = "0xD6", Label = LocalizedText.FromLiteral("Power Mode Control (0xD6)") });
                    options.Add(new ActionParameterOption { Value = "0x14", Label = LocalizedText.FromLiteral("Select Color Preset (0x14)") });
                    options.Add(new ActionParameterOption { Value = "0x16", Label = LocalizedText.FromLiteral("Red Video Gain (0x16)") });
                    options.Add(new ActionParameterOption { Value = "0x18", Label = LocalizedText.FromLiteral("Green Video Gain (0x18)") });
                    options.Add(new ActionParameterOption { Value = "0x1A", Label = LocalizedText.FromLiteral("Blue Video Gain (0x1A)") });
                }
            }

            return new DynamicOptionsResult
            {
                Options = options,
                AllowsCustomValue = true
            };
        }

        public IActionExecutor CreateExecutor() => new Executor(_logger, _integration);

        private sealed class Executor : IActionExecutor
        {
            private readonly ILogger _logger;
            private readonly VcpPluginIntegration _integration;

            public Executor(ILogger logger, VcpPluginIntegration integration)
            {
                _logger = logger;
                _integration = integration;
            }

            public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
            {
                var connName = context.Parameters.TryGetValue(ConnectionParam, out var cVal) ? cVal?.ToString() : null;
                var monitorId = context.Parameters.TryGetValue(MonitorParam, out var mVal) ? mVal?.ToString() : null;
                var codeStr = context.Parameters.TryGetValue(VcpCodeParam, out var cdVal) ? cdVal?.ToString() : null;
                var valueObj = context.Parameters.TryGetValue(ValueParam, out var vVal) ? vVal : null;

                if (string.IsNullOrWhiteSpace(connName) || string.IsNullOrWhiteSpace(monitorId) || string.IsNullOrWhiteSpace(codeStr))
                {
                    return ActionResult.Failed(ActionErrorCodes.InvalidParameter, "Connection, Monitor, and VCP Code are required.");
                }

                byte vcpCode;
                if (codeStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!byte.TryParse(codeStr[2..], System.Globalization.NumberStyles.HexNumber, null, out vcpCode))
                        return ActionResult.Failed(ActionErrorCodes.InvalidParameter, $"Invalid hex VCP code: {codeStr}");
                }
                else
                {
                    if (!byte.TryParse(codeStr, out vcpCode))
                        return ActionResult.Failed(ActionErrorCodes.InvalidParameter, $"Invalid VCP code: {codeStr}");
                }

                uint val = 0;
                if (valueObj is double d) val = (uint)Math.Round(d);
                else if (valueObj is float f) val = (uint)Math.Round(f);
                else if (valueObj is int i) val = (uint)Math.Max(0, i);
                else if (valueObj is long l) val = (uint)Math.Max(0, l);
                else if (valueObj is string s && uint.TryParse(s, out var parsed)) val = parsed;
                else if (valueObj != null && uint.TryParse(valueObj.ToString(), out var parsedObj)) val = parsedObj;

                var client = _integration.GetClient(connName);
                if (client == null)
                {
                    return ActionResult.Failed(ActionErrorCodes.NotConnected, $"Server connection '{connName}' not found or offline.");
                }

                var resp = await client.SetVcpAsync(monitorId, vcpCode, val, context.CancellationToken);
                if (!resp.Success)
                {
                    _logger.Warning("SetVcp failed on {Conn}: {Msg}", connName, resp.Message);
                    return ActionResult.Failed(ActionErrorCodes.ProviderError, resp.Message);
                }

                _logger.Information("SetVcp succeeded: {Monitor} 0x{Code:X2} = {Val} on {Conn}", monitorId, vcpCode, val, connName);

                try
                {
                    await _integration.UpdateVariableValueAsync(connName, monitorId, vcpCode, val);
                }
                catch (Exception ex)
                {
                    _logger.Warning("Failed to update variable value after SetVcp: {Msg}", ex.Message);
                }

                return ActionResult.Success();
            }
        }
    }
}