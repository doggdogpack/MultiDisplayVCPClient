using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using MacroDeck.Sdk.Variables;
using MultiDisplayVCPClient.Actions;
using Serilog;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Main Macro Deck 3 plugin integration managing connections, actions, config flows, and variables.
    /// Orchestrates modular sub-components:
    /// - <see cref="VcpVariableTemplates"/>: Static templates shown pre-configuration.
    /// - <see cref="VcpPreConfigLogic"/>: State handling prior to configuration.
    /// - <see cref="VcpPostConfigLogic"/>: Runtime connection and variable lifecycle post-configuration.
    /// - <see cref="VcpVariableCatalog"/>: Hierarchical on-demand variable catalog browsing and resolution.
    /// - <see cref="VcpHelpers"/>: Formatting and slugification utilities.
    /// </summary>
    public sealed class VcpPluginIntegration : IPluginIntegration, IConfigFlowProvider, IVariableProvider
    {
        private readonly ILogger _logger;
        private readonly IPluginCatalogNotifier? _catalogNotifier;
        private readonly VcpPostConfigLogic _postConfig;
        private readonly VcpVariableCatalog _catalog;

        public static IReadOnlyList<VariableDefinition> Templates => VcpVariableTemplates.Templates;
        public static string Slugify(string text) => VcpHelpers.Slugify(text);
        public static string ToLocalId(string name) => VcpHelpers.ToLocalId(name);

        public VcpPluginIntegration(ILogger logger, IPluginCatalogNotifier? catalogNotifier = null)
        {
            _logger = logger.ForContext<VcpPluginIntegration>();
            _catalogNotifier = catalogNotifier;
            _postConfig = new VcpPostConfigLogic(_logger, catalogNotifier);
            _catalog = new VcpVariableCatalog(_logger, _postConfig);
            Actions = [new SetVcpAction(_logger, this)];
        }

        #region IPluginIntegration

        public IReadOnlyList<IActionDefinition> Actions { get; }

        public async Task InitializeAsync(IIntegrationContext context)
        {
            _logger.Information("Initializing MultiDisplayVCP plugin integration...");
            _postConfig.Context = context;

            // Audit and cache existing host-persisted variables
            try
            {
                var existingVars = await context.Variables.GetAllAsync();
                _logger.Information("Auditing {Count} existing integration variable(s) in host...", existingVars.Count);
                foreach (var v in existingVars)
                {
                    _postConfig.HostVariables[v.Name] = v.Id;
                    if (!string.IsNullOrEmpty(v.DefinitionId))
                    {
                        _postConfig.HostVariables[v.DefinitionId] = v.Id;
                    }
                    _logger.Information("Cached existing host variable '{Name}' (ID {Id}, DefId {DefId}).", v.Name, v.Id, v.DefinitionId);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("Failed to audit existing host variables: {Msg}", ex.Message);
            }

            try
            {
                var entries = await context.Config.GetEntriesAsync();
                _logger.Information("Found {Count} configured VCP server connection(s).", entries.Count);

                foreach (var entry in entries)
                {
                    try
                    {
                        string host = await context.Config.GetStringAsync(entry.Id, "host") ?? string.Empty;
                        string portStr = await context.Config.GetStringAsync(entry.Id, "port") ?? "5002";
                        string password = string.Empty;
                        try
                        {
                            password = await context.Config.GetSecretAsync(entry.Id, "password") ?? string.Empty;
                        }
                        catch
                        {
                            // Secret may be empty or unconfigured
                        }

                        if (string.IsNullOrWhiteSpace(host)) continue;
                        if (!int.TryParse(portStr, CultureInfo.InvariantCulture, out int port)) port = 5002;

                        await AddOrUpdateClientAsync(entry.Title, host, port, password, entry.Id);
                    }
                    catch (Exception entryEx)
                    {
                        _logger.Warning("Failed to load connection entry '{Title}' ({Id}): {Msg}", entry.Title, entry.Id, entryEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load VCP server connections from configuration: {Message}", ex.Message);
            }
        }

        public async Task ShutdownAsync()
        {
            _logger.Information("Shutting down MultiDisplayVCP plugin integration...");
            _catalog.Clear();
            await _postConfig.ShutdownAsync();
        }

        #endregion

        #region IConfigFlowProvider

        public bool AllowsMultipleConfigurations => true;
        public bool RequiresConfiguration => true;

        public IConfigFlow CreateConfigFlow() => new VcpConfigFlow(_logger, this);

        #endregion

        #region Connection Delegation

        public VcpClient? GetClient(string connectionName) => _postConfig.GetClient(connectionName);

        public IReadOnlyDictionary<string, VcpClient> GetClients() => _postConfig.Clients;

        public Task AddOrUpdateClientAsync(string name, string host, int port, string password, Guid? entryId = null) =>
            _postConfig.AddOrUpdateClientAsync(name, host, port, password, entryId);

        public Task UpdateVariableValueAsync(string connectionName, string monitorPnpId, byte vcpCode, uint newValue) =>
            _postConfig.UpdateVariableValueAsync(connectionName, monitorPnpId, vcpCode, newValue);

        #endregion

        #region IVariableProvider - Variables & Declarations

        public bool VariablesDependOnConfiguration => true;

        /// <summary>
        /// Variables view in Macro Deck.
        /// Pre-configuration: returns [] (nothing to show until configured).
        /// Post-configuration: returns registered common VCP variables and connection status.
        /// </summary>
        public IReadOnlyList<VariableDefinition> Variables =>
            _postConfig.HasConfigurations ? _postConfig.GetVariables() : VcpPreConfigLogic.GetVariables();

        /// <summary>
        /// Integrations page in Macro Deck.
        /// Pre-configuration: returns Templates (marked 'Available after setup').
        /// Post-configuration: returns concrete common VCP variables populated with actual monitor names.
        /// </summary>
        public IReadOnlyList<VariableDefinition> DeclaredVariables =>
            _postConfig.HasConfigurations ? _postConfig.GetDeclaredVariables() : VcpPreConfigLogic.GetDeclaredVariables();

        public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default) =>
            _postConfig.HasConfigurations ? _postConfig.ReadAsync(localId, cancellationToken) : VcpPreConfigLogic.ReadAsync(localId);

        public ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value, CancellationToken cancellationToken = default) =>
            _postConfig.SetValueAsync(localId, value, cancellationToken);

        public Task OnAttachedAsync(IVariableSink sink, CancellationToken cancellationToken = default)
        {
            _postConfig.VariableSink = sink;
            return _postConfig.PublishInitialReadingsAsync(sink, cancellationToken);
        }

        #endregion

        #region IVariableProvider - Catalog Delegation

        public bool SupportsCatalog => VcpVariableCatalog.SupportsCatalog;
        public bool SupportsPush => VcpVariableCatalog.SupportsPush;
        public bool SupportsSearch => VcpVariableCatalog.SupportsSearch;
        public string CatalogName => VcpVariableCatalog.CatalogName;
        public int? CatalogEntryCount => VcpVariableCatalog.CatalogEntryCount;

        public ValueTask<VariableCatalogPage> DiscoverAsync(VariableCatalogQuery query, CancellationToken cancellationToken = default) =>
            _catalog.DiscoverAsync(query, cancellationToken);

        public ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken = default) =>
            _catalog.ResolveAsync(localId, cancellationToken);

        public ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(IReadOnlyCollection<string> localIds, CancellationToken cancellationToken = default) =>
            _catalog.SubscribeAsync(localIds, cancellationToken);

        #endregion
    }
}
