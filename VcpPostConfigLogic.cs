using System.Collections.Concurrent;
using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Variables;
using MultiDisplayVCPServer.Shared;
using Serilog;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Handles post-configuration logic: connection management, capability querying,
    /// dynamic variable generation, and read/write execution for configured VCP servers.
    /// </summary>
    public sealed class VcpPostConfigLogic
    {
        private readonly ILogger _logger;
        private readonly IPluginCatalogNotifier? _catalogNotifier;
        private readonly ConcurrentDictionary<string, VcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<MonitorInfoDto>> _clientMonitors = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, VcpVariableInfo> _variableMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _connectionStatusMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Guid> _hostVariables = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<VariableDefinition> _variables = [];
        private readonly object _lock = new();

        public IIntegrationContext? Context { get; set; }
        public IVariableSink? VariableSink { get; set; }

        public VcpPostConfigLogic(ILogger logger, IPluginCatalogNotifier? catalogNotifier = null)
        {
            _logger = logger.ForContext<VcpPostConfigLogic>();
            _catalogNotifier = catalogNotifier;
        }

        public bool HasConfigurations => !_clients.IsEmpty;
        public bool HasConnectedMonitors => _clientMonitors.Values.Any(m => m.Count > 0);

        public IReadOnlyDictionary<string, VcpClient> Clients => _clients;
        public ConcurrentDictionary<string, List<MonitorInfoDto>> ClientMonitors => _clientMonitors;
        public ConcurrentDictionary<string, VcpVariableInfo> VariableMap => _variableMap;
        public ConcurrentDictionary<string, bool> ConnectionStatusMap => _connectionStatusMap;
        public ConcurrentDictionary<string, Guid> HostVariables => _hostVariables;

        public VcpClient? GetClient(string connectionName)
        {
            _clients.TryGetValue(connectionName, out var client);
            return client;
        }

        public IReadOnlyList<VariableDefinition> GetVariables()
        {
            lock (_lock)
            {
                return HasConfigurations ? _variables.ToList() : [];
            }
        }

        public IReadOnlyList<VariableDefinition> GetDeclaredVariables()
        {
            lock (_lock)
            {
                return HasConfigurations ? _variables.ToList() : VcpVariableTemplates.Templates;
            }
        }

        /// <summary>
        /// Builds a valid LocalIdKind.Declared ID (lowercase, hyphen-separated, starting with letter, max 64 chars).
        /// </summary>
        public static string BuildDeclaredLocalId(Guid? entryId, string connectionSlug, string monSlug, string featSlugHyphen, byte vcpCode)
        {
            if (entryId.HasValue)
            {
                string candidate = $"entry-{entryId.Value:N}-{monSlug}-{featSlugHyphen}";
                if (candidate.Length <= 64)
                {
                    return candidate;
                }

                // If candidate is too long, build a deterministic compact ID within the 64-char budget
                string shortMon = monSlug.Length > 8 ? monSlug[..8].TrimEnd('-') : monSlug;
                string codeHex = $"{vcpCode:x2}";
                string shortFeat = featSlugHyphen.Length > 12 ? featSlugHyphen[..12].TrimEnd('-') : featSlugHyphen;
                string candidate2 = $"entry-{entryId.Value:N}-{shortMon}-{codeHex}-{shortFeat}";
                if (candidate2.Length <= 64)
                {
                    return candidate2;
                }

                return $"entry-{entryId.Value:N}-{shortMon}-{codeHex}";
            }

            string fallback = $"multidisplay-{connectionSlug}-{monSlug}-{featSlugHyphen}";
            if (fallback.Length <= 64)
            {
                return fallback;
            }
            return VcpHelpers.ToLocalId(fallback);
        }

        public async Task AddOrUpdateClientAsync(string name, string host, int port, string password, Guid? entryId = null)
        {
            if (_clients.TryGetValue(name, out var existing))
            {
                _logger.Information("Updating existing connection '{Name}' ({Host}:{Port}).", name, host, port);
                existing.Disconnect();
            }
            else
            {
                _logger.Information("Adding new connection '{Name}' ({Host}:{Port}).", name, host, port);
            }

            var client = new VcpClient(name, host, port, password)
            {
                EntryId = entryId
            };
            _clients[name] = client;

            string connectionSlug = VcpHelpers.Slugify(name);

            // Create initial connection status variables upon configuration detection
            string connectedVarId = $"multidisplay_{connectionSlug}_connected";
            string connectedLocalId = entryId.HasValue
                ? $"entry-{entryId.Value:N}-connected"
                : VcpHelpers.ToLocalId(connectedVarId);

            var connectedDef = VariableDefinition.Eager(connectedVarId, VariableType.Boolean, decimalPlaces: null, refreshInterval: null) with
            {
                Id = connectedLocalId,
                Configuration = entryId.HasValue ? new VariableConfiguration(entryId.Value.ToString("N"), LocalizedText.FromLiteral(name)) : null,
                DisplayName = LocalizedText.FromLiteral($"{name} Connected"),
                Description = LocalizedText.FromLiteral($"Connection state for '{name}' server"),
                IsBindable = true
            };

            lock (_lock)
            {
                _variables.RemoveAll(v => v.Name == connectedVarId || v.Id == connectedLocalId);
                _variables.Add(connectedDef);
            }

            _connectionStatusMap[connectedVarId] = false;
            _connectionStatusMap[connectedLocalId] = false;
            _catalogNotifier?.CatalogChanged("variables");

            client.ConnectionStateChanged += async (_, state) =>
            {
                bool isConnected = state == ConnectionState.Connected;
                _connectionStatusMap[connectedVarId] = isConnected;
                _connectionStatusMap[connectedLocalId] = isConnected;
                _logger.Information("Connection '{Name}' state changed to {State} (Connected: {Connected}).", name, state, isConnected);

                if (isConnected)
                {
                    if (!_clientMonitors.ContainsKey(client.Name))
                    {
                        await RefreshVariablesForClientAsync(client);
                    }
                }
                else
                {
                    RemoveClientVariables(client);
                }

                if (VariableSink != null)
                {
                    try
                    {
                        await VariableSink.PublishAsync([
                            VariableValue.Of(connectedLocalId, VariableReading.Of(isConnected)),
                            VariableValue.Of(connectedVarId, VariableReading.Of(isConnected)),
                            VariableValue.Of($"status/{connectionSlug}", VariableReading.Of(isConnected))
                        ]);
                        await VariableSink.InvalidateCatalogAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("Failed to publish connection status update for '{Name}': {Msg}", name, ex.Message);
                    }
                }
            };

            // Attempt an immediate quick connect so variables are populated before initial Describe() if server is online
            try
            {
                using var quickCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                bool ok = await client.ConnectAsync(quickCts.Token);
                if (ok)
                {
                    _logger.Information("Immediately connected to '{Name}'. Fetching capabilities...", name);
                    await RefreshVariablesForClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                _logger.Information("Initial connection attempt to '{Name}' skipped ({Msg}); background auto-reconnect will retry.", name, ex.Message);
            }

            // Connect and populate variables in background with auto-reconnect
            _ = Task.Run(async () =>
            {
                while (_clients.ContainsKey(name))
                {
                    await SyncConfigurationsAsync();

                    try
                    {
                        if (client.State != ConnectionState.Connected)
                        {
                            bool ok = await client.ConnectAsync();
                            if (ok)
                            {
                                _logger.Information("Connected to '{Name}'. Fetching capabilities...", name);
                                await RefreshVariablesForClientAsync(client);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_connectionStatusMap.TryGetValue(connectedVarId, out bool wasConn) && wasConn)
                        {
                            _logger.Warning("Connection to '{Name}' lost or failed: {Msg}", name, ex.Message);
                        }
                        _connectionStatusMap[connectedVarId] = false;
                        _connectionStatusMap[connectedLocalId] = false;
                        RemoveClientVariables(client);
                        if (VariableSink != null)
                        {
                            try
                            {
                                await VariableSink.PublishAsync([
                                    VariableValue.Of(connectedLocalId, VariableReading.Of(false)),
                                    VariableValue.Of(connectedVarId, VariableReading.Of(false)),
                                    VariableValue.Of($"status/{connectionSlug}", VariableReading.Of(false))
                                ]);
                                await VariableSink.InvalidateCatalogAsync();
                            }
                            catch { }
                        }
                    }

                    int delaySeconds = client.State == ConnectionState.Connected ? 15 : 5;
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
            });

            await Task.CompletedTask;
        }

        public async Task SyncConfigurationsAsync()
        {
            if (Context == null) return;
            try
            {
                var entries = await Context.Config.GetEntriesAsync();
                var validEntryIds = entries.Select(e => e.Id).ToHashSet();
                var validTitles = entries.Select(e => e.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Update EntryId if missing
                foreach (var entry in entries)
                {
                    if (_clients.TryGetValue(entry.Title, out var client) && (!client.EntryId.HasValue || client.EntryId.Value != entry.Id))
                    {
                        client.EntryId = entry.Id;
                    }
                }

                // Identify removed clients
                var staleClients = _clients.Values
                    .Where(c => (c.EntryId.HasValue && !validEntryIds.Contains(c.EntryId.Value)) || (!c.EntryId.HasValue && !validTitles.Contains(c.Name)))
                    .ToList();

                foreach (var stale in staleClients)
                {
                    _logger.Information("Connection '{Name}' removed from configuration. Cleaning up runtime state...", stale.Name);
                    RemoveClient(stale);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("SyncConfigurationsAsync encountered an error: {Msg}", ex.Message);
            }
        }

        public void RemoveClientVariables(VcpClient client)
        {
            string connectionSlug = VcpHelpers.Slugify(client.Name);
            string connPrefixUnder = $"multidisplay_{connectionSlug}_";
            string connPrefixHyphen = $"multidisplay-{connectionSlug}-";
            string? entryPrefix = client.EntryId.HasValue ? $"entry-{client.EntryId.Value:N}-" : null;

            _clientMonitors.TryRemove(client.Name, out _);

            bool MatchesConn(VariableDefinition v) =>
                (entryPrefix != null && (v.Id?.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase) == true || v.ResolvedId?.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase) == true)) ||
                v.ResolvedId?.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) == true ||
                v.ResolvedId?.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) == true ||
                v.Id?.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) == true ||
                v.Id?.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) == true ||
                v.Name?.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) == true;

            lock (_lock)
            {
                string connectedVarId = $"multidisplay_{connectionSlug}_connected";
                string connectedLocalId = client.EntryId.HasValue
                    ? $"entry-{client.EntryId.Value:N}-connected"
                    : VcpHelpers.ToLocalId(connectedVarId);

                // Keep connection status definitions in place, remove monitor variables
                _variables.RemoveAll(v => MatchesConn(v) &&
                    v.Name != connectedVarId && v.Id != connectedLocalId);

                foreach (var key in _variableMap.Keys.Where(k =>
                    (entryPrefix != null && k.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase)) ||
                    k.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith($"vcp/{connectionSlug}/", StringComparison.OrdinalIgnoreCase) ||
                    k.StartsWith($"status/{connectionSlug}", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _variableMap.TryRemove(key, out _);
                }
            }

            _catalogNotifier?.CatalogChanged("variables");
        }

        public void RemoveClient(VcpClient client)
        {
            client.Disconnect();
            _clients.TryRemove(client.Name, out _);
            RemoveClientVariables(client);

            string connectionSlug = VcpHelpers.Slugify(client.Name);
            string connPrefixUnder = $"multidisplay_{connectionSlug}_";
            string connPrefixHyphen = $"multidisplay-{connectionSlug}-";
            string? entryPrefix = client.EntryId.HasValue ? $"entry-{client.EntryId.Value:N}-" : null;

            lock (_lock)
            {
                string connectedVarId = $"multidisplay_{connectionSlug}_connected";
                string connectedLocalId = client.EntryId.HasValue
                    ? $"entry-{client.EntryId.Value:N}-connected"
                    : VcpHelpers.ToLocalId(connectedVarId);

                _variables.RemoveAll(v => v.Name == connectedVarId || v.Id == connectedLocalId);
            }

            foreach (var key in _connectionStatusMap.Keys.Where(k =>
                (entryPrefix != null && k.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase)) ||
                k.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith($"status/{connectionSlug}", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _connectionStatusMap.TryRemove(key, out _);
            }

            foreach (var key in _hostVariables.Keys.Where(k =>
                (entryPrefix != null && k.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase)) ||
                k.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (_hostVariables.TryRemove(key, out var guid) && Context != null)
                {
                    try
                    {
                        _ = Context.Variables.DeleteAsync(guid);
                    }
                    catch { }
                }
            }

            if (VariableSink != null)
            {
                _ = VariableSink.InvalidateCatalogAsync();
            }

            _catalogNotifier?.CatalogChanged("variables");
        }

        public async Task RefreshVariablesForClientAsync(VcpClient client)
        {
            try
            {
                CapabilitiesResponse? caps = null;
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    caps = await client.GetCapabilitiesAsync();
                    if (caps.Success && caps.Monitors.Count > 0)
                    {
                        break;
                    }

                    if (attempt < 5)
                    {
                        _logger.Information("Capabilities from '{Name}' not ready yet ({Msg}). Retrying in 2s (attempt {Attempt}/5)...", client.Name, caps?.Message ?? "warming", attempt);
                        await Task.Delay(2000);
                    }
                }

                if (caps == null || !caps.Success || caps.Monitors.Count == 0)
                {
                    _logger.Warning("Failed to retrieve monitor capabilities from '{Name}': {Msg}", client.Name, caps?.Message ?? "No response");
                    return;
                }

                _clientMonitors[client.Name] = caps.Monitors;

                string connectionSlug = VcpHelpers.Slugify(client.Name);
                string connectedVarId = $"multidisplay_{connectionSlug}_connected";
                string connectedLocalId = client.EntryId.HasValue
                    ? $"entry-{client.EntryId.Value:N}-connected"
                    : VcpHelpers.ToLocalId(connectedVarId);

                _connectionStatusMap[connectedVarId] = true;
                _connectionStatusMap[connectedLocalId] = true;

                var newVariables = new List<VariableDefinition>();
                var newVariableInfos = new List<VcpVariableInfo>();
                var initialValues = new List<VariableValue>
                {
                    VariableValue.Of(connectedLocalId, VariableReading.Of(true)),
                    VariableValue.Of(connectedVarId, VariableReading.Of(true)),
                    VariableValue.Of($"status/{connectionSlug}", VariableReading.Of(true))
                };

                // Add connection status definitions
                var connectedDef = VariableDefinition.Eager(connectedVarId, VariableType.Boolean, decimalPlaces: null, refreshInterval: null) with
                {
                    Id = connectedLocalId,
                    Configuration = client.EntryId.HasValue ? new VariableConfiguration(client.EntryId.Value.ToString("N"), LocalizedText.FromLiteral(client.Name)) : null,
                    DisplayName = LocalizedText.FromLiteral($"{client.Name} Connected"),
                    Description = LocalizedText.FromLiteral($"Connection state for '{client.Name}' server"),
                    IsBindable = true
                };
                newVariables.Add(connectedDef);

                // Build the full list of monitor-specific variables sent from the server
                bool hasDuplicatePnp = caps.Monitors.GroupBy(m => VcpHelpers.Slugify(m.DeviceID)).Any(g => g.Count() > 1);

                for (int i = 0; i < caps.Monitors.Count; i++)
                {
                    var mon = caps.Monitors[i];
                    if (string.IsNullOrWhiteSpace(mon.DeviceID)) continue;

                    int dispIndex = i + 1;
                    string baseMonSlug = VcpHelpers.Slugify(mon.DeviceID);
                    string monSlug = hasDuplicatePnp ? $"{baseMonSlug}_disp{dispIndex}" : baseMonSlug;
                    string monDesc = string.IsNullOrWhiteSpace(mon.Description) ? mon.DeviceID : mon.Description;

                    foreach (var feat in mon.Capabilities)
                    {
                        string featName = string.IsNullOrWhiteSpace(feat.Name) ? $"VCP 0x{feat.Code:X2}" : feat.Name;
                        string featSlug = VcpHelpers.Slugify(featName);
                        string featSlugHyphen = featSlug.Replace('_', '-');
                        string canonicalName = $"multidisplay_{connectionSlug}_{monSlug}_{featSlug}";
                        string localId = BuildDeclaredLocalId(client.EntryId, connectionSlug, monSlug, featSlugHyphen, feat.Code);

                        var eagerDef = VariableDefinition.Eager(canonicalName, VariableType.Numeric, decimalPlaces: 0, refreshInterval: TimeSpan.FromSeconds(2)) with
                        {
                            Id = localId,
                            DisplayName = LocalizedText.FromLiteral(featName),
                            Description = LocalizedText.FromLiteral($"{featName} on {monDesc} ({client.Name})"),
                            Configuration = client.EntryId.HasValue ? new VariableConfiguration(client.EntryId.Value.ToString("N"), LocalizedText.FromLiteral(client.Name)) : null,
                            IsBindable = true,
                            Write = feat.ReadWrite ? new VariableWriteCapability { CommitOnRelease = false } : null
                        };
                        newVariables.Add(eagerDef);

                        var info = new VcpVariableInfo
                        {
                            VariableId = canonicalName,
                            ConnectionName = client.Name,
                            MonitorPnpId = mon.DeviceID,
                            MonitorDescription = monDesc,
                            VcpCode = feat.Code,
                            FeatureName = featName,
                            MinValue = 0,
                            MaxValue = feat.MaximumValue > 0 ? feat.MaximumValue : 100,
                            CurrentValue = feat.CurrentValue,
                            ReadWrite = feat.ReadWrite
                        };
                        newVariableInfos.Add(info);

                        var reading = VariableReading.Of(feat.CurrentValue, min: 0, max: feat.MaximumValue > 0 ? feat.MaximumValue : 100, step: 1);
                        initialValues.Add(VariableValue.Of(localId, reading));
                        initialValues.Add(VariableValue.Of(canonicalName, reading));
                        initialValues.Add(VariableValue.Of($"vcp/{connectionSlug}/{monSlug}/{feat.Code:x2}", reading));
                    }
                }

                lock (_lock)
                {
                    string connPrefixUnder = $"multidisplay_{connectionSlug}_";
                    string connPrefixHyphen = $"multidisplay-{connectionSlug}-";
                    string? entryPrefix = client.EntryId.HasValue ? $"entry-{client.EntryId.Value:N}-" : null;

                    bool MatchesConn(VariableDefinition v) =>
                        (entryPrefix != null && (v.Id?.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase) == true || v.ResolvedId?.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase) == true)) ||
                        v.ResolvedId?.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) == true ||
                        v.ResolvedId?.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) == true ||
                        v.Id?.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) == true ||
                        v.Id?.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) == true ||
                        v.Name?.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) == true;

                    _variables.RemoveAll(MatchesConn);
                    _variables.AddRange(newVariables);

                    // Clean old variable map entries for this connection
                    foreach (var key in _variableMap.Keys.Where(k =>
                        (entryPrefix != null && k.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase)) ||
                        k.StartsWith(connPrefixUnder, StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith(connPrefixHyphen, StringComparison.OrdinalIgnoreCase) ||
                        k.StartsWith($"vcp/{connectionSlug}/", StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        _variableMap.TryRemove(key, out _);
                    }

                    // Populate variable map
                    foreach (var info in newVariableInfos)
                    {
                        string featSlug = VcpHelpers.Slugify(info.FeatureName);
                        string featSlugHyphen = featSlug.Replace('_', '-');
                        string codeHex = $"{info.VcpCode:x2}";
                        string monSlug = VcpHelpers.Slugify(info.MonitorPnpId);
                        string localId = BuildDeclaredLocalId(client.EntryId, connectionSlug, monSlug, featSlugHyphen, info.VcpCode);

                        _variableMap[info.VariableId] = info;
                        _variableMap[localId] = info;
                        _variableMap[$"vcp/{connectionSlug}/{monSlug}/{codeHex}"] = info;
                        _variableMap[$"multidisplay_{connectionSlug}_{monSlug}_0x{codeHex}"] = info;
                        _variableMap[$"multidisplay_{connectionSlug}_{monSlug}_{codeHex}"] = info;
                    }
                }

                _logger.Information("Registered {Count} variables for connection '{Name}'.", newVariables.Count, client.Name);

                if (VariableSink != null)
                {
                    await VariableSink.PublishAsync(initialValues);
                    await VariableSink.InvalidateCatalogAsync();
                }

                // Notify Macro Deck host to refresh Variables and DeclaredVariables
                _catalogNotifier?.CatalogChanged("variables");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to refresh variables for client '{Name}'.", client.Name);
            }
        }

        public async Task UpdateVariableValueAsync(string connectionName, string monitorPnpId, byte vcpCode, uint newValue)
        {
            var matching = _variableMap.Values.FirstOrDefault(v =>
                string.Equals(v.ConnectionName, connectionName, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(v.MonitorPnpId, monitorPnpId, StringComparison.OrdinalIgnoreCase) ||
                 VcpHelpers.Slugify(v.MonitorPnpId).Equals(VcpHelpers.Slugify(monitorPnpId), StringComparison.OrdinalIgnoreCase)) &&
                v.VcpCode == vcpCode);

            if (matching != null)
            {
                matching.CurrentValue = newValue;

                if (_hostVariables.TryGetValue(matching.VariableId, out var varGuid) && Context != null)
                {
                    try
                    {
                        await Context.Variables.SetValueAsync(varGuid, (double)newValue);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("Failed to update host variable {Var}: {Msg}", matching.VariableId, ex.Message);
                    }
                }

                if (VariableSink != null)
                {
                    try
                    {
                        string connSlug = VcpHelpers.Slugify(matching.ConnectionName);
                        string monSlug = VcpHelpers.Slugify(matching.MonitorPnpId);
                        string catalogLeafId = $"vcp/{connSlug}/{monSlug}/{matching.VcpCode:x2}";
                        string localIdSlug = VcpHelpers.ToLocalId(matching.VariableId);
                        var reading = VariableReading.Of(newValue, min: matching.MinValue, max: matching.MaxValue, step: 1);

                        await VariableSink.PublishAsync([
                            VariableValue.Of(localIdSlug, reading),
                            VariableValue.Of(matching.VariableId, reading),
                            VariableValue.Of(catalogLeafId, reading)
                        ]);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning("Failed to publish updated variable value for {Var}: {Msg}", matching.VariableId, ex.Message);
                    }
                }
            }
        }

        public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
        {
            // Status check
            if (_connectionStatusMap.TryGetValue(localId, out bool isConnected))
            {
                return ValueTask.FromResult(VariableReading.Of(isConnected));
            }
            if (localId.StartsWith("status/", StringComparison.OrdinalIgnoreCase))
            {
                string connSlug = localId["status/".Length..];
                var client = _clients.Values.FirstOrDefault(c => VcpHelpers.Slugify(c.Name).Equals(connSlug, StringComparison.OrdinalIgnoreCase));
                bool connected = client != null && client.State == ConnectionState.Connected;
                return ValueTask.FromResult(VariableReading.Of(connected));
            }

            // Connection status fallback
            if (localId.EndsWith("connected", StringComparison.OrdinalIgnoreCase))
            {
                var client = _clients.Values.FirstOrDefault(c =>
                    localId.Contains(VcpHelpers.Slugify(c.Name), StringComparison.OrdinalIgnoreCase) ||
                    (c.EntryId.HasValue && localId.Contains(c.EntryId.Value.ToString("N"), StringComparison.OrdinalIgnoreCase)));
                bool connected = client != null && client.State == ConnectionState.Connected;
                return ValueTask.FromResult(VariableReading.Of(connected));
            }

            // VCP feature check via variableMap
            if (_variableMap.TryGetValue(localId, out var info))
            {
                var client = GetClient(info.ConnectionName);
                if (client == null || client.State != ConnectionState.Connected)
                {
                    return ValueTask.FromResult(VariableReading.Unavailable);
                }
                return ValueTask.FromResult(VariableReading.Of(info.CurrentValue, min: info.MinValue, max: info.MaxValue, step: 1));
            }

            // Direct vcp/{connSlug}/{monSlug}/{codeHex} lookup
            if (localId.StartsWith("vcp/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = localId.Split('/');
                if (parts.Length >= 4 && byte.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
                {
                    string connSlug = parts[1];
                    string monSlug = parts[2];
                    var client = _clients.Values.FirstOrDefault(c => VcpHelpers.Slugify(c.Name).Equals(connSlug, StringComparison.OrdinalIgnoreCase));
                    if (client != null && client.State == ConnectionState.Connected && _clientMonitors.TryGetValue(client.Name, out var mons))
                    {
                        var mon = mons.FirstOrDefault(m => VcpHelpers.Slugify(m.DeviceID).Equals(monSlug, StringComparison.OrdinalIgnoreCase));
                        var feat = mon?.Capabilities.FirstOrDefault(f => f.Code == code);
                        if (feat != null)
                        {
                            return ValueTask.FromResult(VariableReading.Of(feat.CurrentValue, min: 0, max: feat.MaximumValue > 0 ? feat.MaximumValue : 100, step: 1));
                        }
                    }
                }
            }

            return ValueTask.FromResult(VariableReading.Unavailable);
        }

        public async ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value, CancellationToken cancellationToken = default)
        {
            if (_connectionStatusMap.ContainsKey(localId) || localId.StartsWith("status/", StringComparison.OrdinalIgnoreCase))
            {
                return VariableWriteResult.NotWritable(LocalizedText.FromLiteral($"Variable '{localId}' is connection status and cannot be written to."));
            }

            if (!_variableMap.TryGetValue(localId, out var info))
            {
                if (localId.StartsWith("vcp/", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = localId.Split('/');
                    if (parts.Length >= 4 && byte.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
                    {
                        string connSlug = parts[1];
                        string monSlug = parts[2];
                        var foundClient = _clients.Values.FirstOrDefault(c => VcpHelpers.Slugify(c.Name).Equals(connSlug, StringComparison.OrdinalIgnoreCase));
                        if (foundClient != null && _clientMonitors.TryGetValue(foundClient.Name, out var mons))
                        {
                            var mon = mons.FirstOrDefault(m => VcpHelpers.Slugify(m.DeviceID).Equals(monSlug, StringComparison.OrdinalIgnoreCase));
                            if (mon != null)
                            {
                                var feat = mon.Capabilities.FirstOrDefault(f => f.Code == code);
                                if (feat != null)
                                {
                                    info = new VcpVariableInfo
                                    {
                                        VariableId = $"multidisplay_{connSlug}_{monSlug}_{VcpHelpers.Slugify(feat.Name)}",
                                        ConnectionName = foundClient.Name,
                                        MonitorPnpId = mon.DeviceID,
                                        MonitorDescription = mon.Description,
                                        VcpCode = code,
                                        FeatureName = feat.Name,
                                        MinValue = 0,
                                        MaxValue = feat.MaximumValue > 0 ? feat.MaximumValue : 100,
                                        CurrentValue = feat.CurrentValue,
                                        ReadWrite = feat.ReadWrite
                                    };
                                    _variableMap[localId] = info;
                                }
                            }
                        }
                    }
                }
            }

            if (info == null)
            {
                return VariableWriteResult.NotFound(LocalizedText.FromLiteral($"Variable '{localId}' was not found."));
            }

            if (!info.ReadWrite)
            {
                return VariableWriteResult.NotWritable(LocalizedText.FromLiteral($"Feature '{info.FeatureName}' is read-only."));
            }

            uint numericValue = 0;
            if (value is double d) numericValue = (uint)Math.Clamp(Math.Round(d), info.MinValue, info.MaxValue);
            else if (value is float f) numericValue = (uint)Math.Clamp(Math.Round(f), info.MinValue, info.MaxValue);
            else if (value is int i) numericValue = (uint)Math.Clamp(i, (int)info.MinValue, (int)info.MaxValue);
            else if (value is long l) numericValue = (uint)Math.Clamp(l, (long)info.MinValue, (long)info.MaxValue);
            else if (value is string s && uint.TryParse(s, out var parsed)) numericValue = Math.Clamp(parsed, info.MinValue, info.MaxValue);
            else if (value != null && uint.TryParse(value.ToString(), out var parsedStr)) numericValue = Math.Clamp(parsedStr, info.MinValue, info.MaxValue);

            var client = GetClient(info.ConnectionName);
            if (client == null || client.State != ConnectionState.Connected)
            {
                return VariableWriteResult.Unavailable(LocalizedText.FromLiteral($"Server '{info.ConnectionName}' is offline or not found."));
            }

            var resp = await client.SetVcpAsync(info.MonitorPnpId, info.VcpCode, numericValue, cancellationToken);
            if (!resp.Success)
            {
                _logger.Warning("SetValueAsync failed for {Var}: {Msg}", localId, resp.Message);
                return VariableWriteResult.Failed(LocalizedText.FromLiteral(resp.Message));
            }

            info.CurrentValue = numericValue;

            if (_hostVariables.TryGetValue(info.VariableId, out var varGuid) && Context != null)
            {
                try
                {
                    await Context.Variables.SetValueAsync(varGuid, (double)numericValue);
                }
                catch (Exception ex)
                {
                    _logger.Warning("Failed to update host variable {Var}: {Msg}", info.VariableId, ex.Message);
                }
            }

            if (VariableSink != null)
            {
                string connSlug = VcpHelpers.Slugify(info.ConnectionName);
                string monSlug = VcpHelpers.Slugify(info.MonitorPnpId);
                string catalogLeafId = $"vcp/{connSlug}/{monSlug}/{info.VcpCode:x2}";
                string localIdSlug = VcpHelpers.ToLocalId(info.VariableId);

                var reading = VariableReading.Of(numericValue, min: info.MinValue, max: info.MaxValue, step: 1);
                await VariableSink.PublishAsync(
                    [
                        VariableValue.Of(localId, reading),
                        VariableValue.Of(catalogLeafId, reading),
                        VariableValue.Of(localIdSlug, reading),
                        VariableValue.Of(info.VariableId, reading)
                    ],
                    cancellationToken);
            }

            return VariableWriteResult.Applied();
        }

        public async Task PublishInitialReadingsAsync(IVariableSink sink, CancellationToken cancellationToken = default)
        {
            var initialValues = new List<VariableValue>();
            foreach (var kvp in _connectionStatusMap)
            {
                initialValues.Add(VariableValue.Of(kvp.Key, VariableReading.Of(kvp.Value)));
            }
            foreach (var info in _variableMap.Values.DistinctBy(v => v.VariableId))
            {
                string localIdSlug = VcpHelpers.ToLocalId(info.VariableId);
                string connSlug = VcpHelpers.Slugify(info.ConnectionName);
                string monSlug = VcpHelpers.Slugify(info.MonitorPnpId);
                string catalogLeafId = $"vcp/{connSlug}/{monSlug}/{info.VcpCode:x2}";
                var reading = VariableReading.Of(info.CurrentValue, min: info.MinValue, max: info.MaxValue, step: 1);

                initialValues.Add(VariableValue.Of(localIdSlug, reading));
                initialValues.Add(VariableValue.Of(info.VariableId, reading));
                initialValues.Add(VariableValue.Of(catalogLeafId, reading));
            }

            if (initialValues.Count > 0)
            {
                try
                {
                    await sink.PublishAsync(initialValues, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warning("Failed to publish initial readings on attach: {Msg}", ex.Message);
                }
            }
        }

        public async Task ShutdownAsync()
        {
            if (VariableSink != null)
            {
                var offlineValues = new List<VariableValue>();
                foreach (var key in _connectionStatusMap.Keys)
                {
                    _connectionStatusMap[key] = false;
                    offlineValues.Add(VariableValue.Of(key, VariableReading.Of(false)));
                }
                if (offlineValues.Count > 0)
                {
                    try
                    {
                        await VariableSink.PublishAsync(offlineValues);
                    }
                    catch { }
                }
            }

            foreach (var client in _clients.Values)
            {
                try
                {
                    client.Disconnect();
                }
                catch { }
            }
            _clients.Clear();
            _clientMonitors.Clear();
            _variableMap.Clear();
            _connectionStatusMap.Clear();
            lock (_lock)
            {
                _variables.Clear();
            }
            Context = null;
            VariableSink = null;
        }
    }
}
