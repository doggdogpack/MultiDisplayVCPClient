using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk.Variables;
using MultiDisplayVCPServer.Shared;
using Serilog;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Handles on-demand variable catalog browsing and resolution (System 2: Variable Catalog).
    /// </summary>
    public sealed class VcpVariableCatalog
    {
        private readonly ILogger _logger;
        private readonly VcpPostConfigLogic _postConfig;
        private readonly HashSet<string> _subscribedCatalogIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public const string CatalogName = "MultiDisplayVCP";
        public const bool SupportsCatalog = true;
        public const bool SupportsPush = true;
        public const bool SupportsSearch = true;
        public static int? CatalogEntryCount => null;

        public VcpVariableCatalog(ILogger logger, VcpPostConfigLogic postConfig)
        {
            _logger = logger.ForContext<VcpVariableCatalog>();
            _postConfig = postConfig;
        }

        public ValueTask<VariableCatalogPage> DiscoverAsync(VariableCatalogQuery query, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                return ValueTask.FromResult(DiscoverSearch(query.Search));
            }

            if (query.ParentId == null)
            {
                return ValueTask.FromResult(DiscoverRoots());
            }

            return ValueTask.FromResult(DiscoverChildren(query.ParentId));
        }

        private VariableCatalogPage DiscoverRoots()
        {
            var connectedClients = _postConfig.Clients.Values
                .Where(c => c.State == ConnectionState.Connected && _postConfig.ClientMonitors.TryGetValue(c.Name, out var mons) && mons.Count > 0)
                .ToList();

            if (connectedClients.Count == 0)
            {
                return VariableCatalogPage.Empty;
            }

            // Multiple connections: show connection folders as roots
            if (connectedClients.Count > 1)
            {
                var items = connectedClients.Select(c =>
                {
                    string connSlug = VcpHelpers.Slugify(c.Name);
                    return VariableDefinition.OnDemand($"conn/{connSlug}", VariableType.Text) with
                    {
                        DisplayName = LocalizedText.FromLiteral(c.Name),
                        IsContainer = true,
                        IsBindable = false
                    };
                }).ToList();
                return new VariableCatalogPage { Items = items };
            }

            // Single connection: show monitor containers and connection status directly at root
            return DiscoverConnectionChildren(connectedClients[0], isRoot: true);
        }

        private VariableCatalogPage DiscoverChildren(string parentId)
        {
            if (parentId.StartsWith("conn/", StringComparison.OrdinalIgnoreCase))
            {
                string connSlug = parentId["conn/".Length..];
                var client = _postConfig.Clients.Values.FirstOrDefault(c => VcpHelpers.Slugify(c.Name).Equals(connSlug, StringComparison.OrdinalIgnoreCase));
                if (client == null) return VariableCatalogPage.Empty;
                return DiscoverConnectionChildren(client, isRoot: false);
            }

            if (parentId.StartsWith("mon/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = parentId.Split('/');
                if (parts.Length < 3) return VariableCatalogPage.Empty;
                string connSlug = parts[1];
                string monSlug = parts[2];

                var client = _postConfig.Clients.Values.FirstOrDefault(c => VcpHelpers.Slugify(c.Name).Equals(connSlug, StringComparison.OrdinalIgnoreCase));
                if (client == null || !_postConfig.ClientMonitors.TryGetValue(client.Name, out var monitors)) return VariableCatalogPage.Empty;

                var monitor = monitors.FirstOrDefault(m => VcpHelpers.Slugify(m.DeviceID).Equals(monSlug, StringComparison.OrdinalIgnoreCase));
                if (monitor == null) return VariableCatalogPage.Empty;

                var items = monitor.Capabilities.Select(feat =>
                {
                    string featName = string.IsNullOrWhiteSpace(feat.Name) ? $"VCP 0x{feat.Code:X2}" : feat.Name;
                    string featSlug = VcpHelpers.Slugify(featName);
                    string leafId = $"vcp/{connSlug}/{monSlug}/{feat.Code:x2}";

                    var def = VariableDefinition.OnDemand(leafId, VariableType.Numeric) with
                    {
                        Name = $"multidisplay_{connSlug}_{monSlug}_{featSlug}",
                        DisplayName = LocalizedText.FromLiteral(featName),
                        ParentId = parentId,
                        DecimalPlaces = 0,
                        RefreshInterval = TimeSpan.FromSeconds(2),
                        Description = LocalizedText.FromLiteral($"VCP 0x{feat.Code:X2} ({featName}) on {monitor.DeviceID} ({client.Name})")
                    };

                    if (feat.ReadWrite)
                    {
                        def = def with { Write = new VariableWriteCapability { CommitOnRelease = false } };
                    }

                    return def;
                }).ToList();

                return new VariableCatalogPage { Items = items };
            }

            return VariableCatalogPage.Empty;
        }

        private VariableCatalogPage DiscoverConnectionChildren(VcpClient client, bool isRoot)
        {
            string connSlug = VcpHelpers.Slugify(client.Name);
            string? parentId = isRoot ? null : $"conn/{connSlug}";
            var items = new List<VariableDefinition>();

            // Connection status leaf
            items.Add(VariableDefinition.OnDemand($"status/{connSlug}", VariableType.Boolean) with
            {
                Name = $"multidisplay_{connSlug}_connected",
                DisplayName = LocalizedText.FromLiteral($"{client.Name} Connected"),
                ParentId = parentId,
                Description = LocalizedText.FromLiteral($"Connection state for '{client.Name}' server")
            });

            if (_postConfig.ClientMonitors.TryGetValue(client.Name, out var monitors))
            {
                for (int i = 0; i < monitors.Count; i++)
                {
                    var mon = monitors[i];
                    string monSlug = VcpHelpers.Slugify(mon.DeviceID);
                    string monContainerId = $"mon/{connSlug}/{monSlug}";
                    string monTitle = string.IsNullOrWhiteSpace(mon.Description)
                        ? $"Display {i + 1}"
                        : $"Display {i + 1} ({mon.Description})";

                    items.Add(VariableDefinition.OnDemand(monContainerId, VariableType.Text) with
                    {
                        DisplayName = LocalizedText.FromLiteral(monTitle),
                        ParentId = parentId,
                        IsContainer = true,
                        IsBindable = false
                    });
                }
            }

            return new VariableCatalogPage { Items = items };
        }

        private VariableCatalogPage DiscoverSearch(string query)
        {
            var items = new List<VariableDefinition>();

            foreach (var client in _postConfig.Clients.Values.Where(c => c.State == ConnectionState.Connected))
            {
                if (!_postConfig.ClientMonitors.TryGetValue(client.Name, out var monitors)) continue;
                string connSlug = VcpHelpers.Slugify(client.Name);

                foreach (var mon in monitors)
                {
                    string monSlug = VcpHelpers.Slugify(mon.DeviceID);
                    string monContainerId = $"mon/{connSlug}/{monSlug}";

                    foreach (var feat in mon.Capabilities)
                    {
                        string featName = string.IsNullOrWhiteSpace(feat.Name) ? $"VCP 0x{feat.Code:X2}" : feat.Name;
                        if (!featName.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                            !mon.Description.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                            !$"0x{feat.Code:x2}".Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string featSlug = VcpHelpers.Slugify(featName);
                        string leafId = $"vcp/{connSlug}/{monSlug}/{feat.Code:x2}";

                        var def = VariableDefinition.OnDemand(leafId, VariableType.Numeric) with
                        {
                            Name = $"multidisplay_{connSlug}_{monSlug}_{featSlug}",
                            DisplayName = LocalizedText.FromLiteral($"{mon.Description} - {featName}"),
                            ParentId = monContainerId,
                            DecimalPlaces = 0,
                            RefreshInterval = TimeSpan.FromSeconds(2),
                            Description = LocalizedText.FromLiteral($"VCP 0x{feat.Code:X2} ({featName}) on {mon.DeviceID} ({client.Name})")
                        };

                        if (feat.ReadWrite)
                        {
                            def = def with { Write = new VariableWriteCapability { CommitOnRelease = false } };
                        }

                        items.Add(def);
                    }
                }
            }

            return new VariableCatalogPage { Items = items };
        }

        public ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken = default)
        {
            // 1. Check eager variables
            var eager = _postConfig.GetVariables().FirstOrDefault(v =>
                string.Equals(v.ResolvedId, localId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Id, localId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Name, localId, StringComparison.OrdinalIgnoreCase));
            if (eager != null)
            {
                return ValueTask.FromResult<VariableDefinition?>(eager);
            }

            // 2. Connection status on-demand: status/{connSlug}
            if (localId.StartsWith("status/", StringComparison.OrdinalIgnoreCase))
            {
                string connSlug = localId["status/".Length..];
                var client = _postConfig.Clients.Values.FirstOrDefault(c => VcpHelpers.Slugify(c.Name).Equals(connSlug, StringComparison.OrdinalIgnoreCase));
                string clientName = client?.Name ?? connSlug;
                return ValueTask.FromResult<VariableDefinition?>(
                    VariableDefinition.OnDemand(localId, VariableType.Boolean) with
                    {
                        Name = $"multidisplay_{connSlug}_connected",
                        DisplayName = LocalizedText.FromLiteral($"{clientName} Connected"),
                        Description = LocalizedText.FromLiteral($"Connection state for '{clientName}' server")
                    });
            }

            // 3. VCP leaf on-demand: vcp/{connSlug}/{monSlug}/{codeHex}
            if (localId.StartsWith("vcp/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = localId.Split('/');
                if (parts.Length >= 4 && byte.TryParse(parts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
                {
                    string connSlug = parts[1];
                    string monSlug = parts[2];
                    string monContainerId = $"mon/{connSlug}/{monSlug}";

                    _postConfig.VariableMap.TryGetValue(localId, out var info);
                    string featName = info?.FeatureName ?? $"VCP 0x{code:X2}";
                    string featSlug = VcpHelpers.Slugify(featName);
                    string monDesc = info?.MonitorDescription ?? monSlug;
                    string connName = info?.ConnectionName ?? connSlug;

                    var onDemand = VariableDefinition.OnDemand(localId, VariableType.Numeric) with
                    {
                        Name = $"multidisplay_{connSlug}_{monSlug}_{featSlug}",
                        DisplayName = LocalizedText.FromLiteral(featName),
                        ParentId = monContainerId,
                        DecimalPlaces = 0,
                        RefreshInterval = TimeSpan.FromSeconds(2),
                        Description = LocalizedText.FromLiteral($"VCP 0x{code:X2} ({featName}) on {monDesc} ({connName})")
                    };

                    if (info?.ReadWrite ?? true)
                    {
                        onDemand = onDemand with { Write = new VariableWriteCapability { CommitOnRelease = false } };
                    }

                    return ValueTask.FromResult<VariableDefinition?>(onDemand);
                }
            }

            // 4. Monitor container: mon/{connSlug}/{monSlug}
            if (localId.StartsWith("mon/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = localId.Split('/');
                if (parts.Length >= 3)
                {
                    string connSlug = parts[1];
                    string monSlug = parts[2];
                    return ValueTask.FromResult<VariableDefinition?>(
                        VariableDefinition.OnDemand(localId, VariableType.Text) with
                        {
                            DisplayName = LocalizedText.FromLiteral(monSlug),
                            IsContainer = true,
                            IsBindable = false
                        });
                }
            }

            // 5. Connection container: conn/{connSlug}
            if (localId.StartsWith("conn/", StringComparison.OrdinalIgnoreCase))
            {
                string connSlug = localId["conn/".Length..];
                return ValueTask.FromResult<VariableDefinition?>(
                    VariableDefinition.OnDemand(localId, VariableType.Text) with
                    {
                        DisplayName = LocalizedText.FromLiteral(connSlug),
                        IsContainer = true,
                        IsBindable = false
                    });
            }

            return ValueTask.FromResult<VariableDefinition?>(null);
        }

        public async ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(IReadOnlyCollection<string> localIds, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _subscribedCatalogIds.Clear();
                foreach (var id in localIds)
                {
                    _subscribedCatalogIds.Add(id);
                }
            }

            var results = new List<VariableValue>();
            foreach (var id in localIds)
            {
                var reading = await _postConfig.ReadAsync(id, cancellationToken);
                results.Add(VariableValue.Of(id, reading));
            }
            return results;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _subscribedCatalogIds.Clear();
            }
        }
    }
}
