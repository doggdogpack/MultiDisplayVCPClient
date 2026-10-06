using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Net.Client;
using MagicOnion.Client;
using MultiDisplayVCPServer.Shared;

namespace MultiDisplayVCPClient
{
    public enum ConnectionState
    {
        Offline,
        Connecting,
        Connected
    }

    /// <summary>
    /// Network client communicating with a MultiDisplayVCP server via MagicOnion gRPC
    /// (with automatic fallback to legacy TCP text protocol).
    /// </summary>
    public class VcpClient
    {
        public string Name { get; }
        public string Host { get; }
        public int Port { get; }
        private readonly string _password;

        public ConnectionState State { get; private set; } = ConnectionState.Offline;
        public Guid? EntryId { get; set; }
        public event EventHandler<ConnectionState>? ConnectionStateChanged;

        private GrpcChannel? _channel;
        private IVcpService? _grpcService;
        private bool _isLegacyTcp = false;

        public CapabilitiesResponse? CachedCapabilities { get; private set; }

        public VcpClient(string name, string host, int port, string password)
        {
            Name = name;
            Host = host;
            Port = port;
            _password = password;
        }

        private void EnsureGrpcClient()
        {
            if (_grpcService != null && _channel != null) return;

            string address = Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             Host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Host
                : $"http://{Host}:{Port}";

            _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
                }
            });

            _grpcService = MagicOnionClient.Create<IVcpService>(_channel);
        }

        private (string hash, long timestamp) CreateHmac(string command)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string messageToHash = command + timestamp.ToString(CultureInfo.InvariantCulture);
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_password));
            byte[] computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(messageToHash));
            return (Convert.ToBase64String(computed), timestamp);
        }

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (State == ConnectionState.Connecting) return false;

            try
            {
                State = ConnectionState.Connecting;
                ConnectionStateChanged?.Invoke(this, State);

                // Quick probe for gRPC (2-second timeout)
                EnsureGrpcClient();
                var (hash, ts) = CreateHmac("PING");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));

                var pingResult = await _grpcService!.PingAsync(hash, ts).ResponseAsync;
                if (pingResult.Success)
                {
                    _isLegacyTcp = false;
                    State = ConnectionState.Connected;
                    ConnectionStateChanged?.Invoke(this, State);
                    return true;
                }

                throw new Exception($"PING returned failure: {pingResult.Message}");
            }
            catch (Exception)
            {
                // Try legacy TCP fallback if gRPC direct connection failed
                try
                {
                    bool tcpOk = await LegacyTcpPingAsync(cancellationToken);
                    if (tcpOk)
                    {
                        _isLegacyTcp = true;
                        State = ConnectionState.Connected;
                        ConnectionStateChanged?.Invoke(this, State);
                        return true;
                    }
                }
                catch { }

                Disconnect();
                throw;
            }
        }

        public async Task<CapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            if (_isLegacyTcp)
            {
                return await LegacyTcpGetCapabilitiesAsync(cancellationToken);
            }

            EnsureGrpcClient();
            var (hash, ts) = CreateHmac("GET_CAPS");

            try
            {
                var result = await _grpcService!.GetCapabilitiesAsync(hash, ts).ResponseAsync;
                if (result.Success)
                {
                    CachedCapabilities = result;
                    return result;
                }
            }
            catch
            {
                // Fallback to TCP if gRPC failed
                _isLegacyTcp = true;
                return await LegacyTcpGetCapabilitiesAsync(cancellationToken);
            }

            return CachedCapabilities ?? new CapabilitiesResponse { Success = false, Message = "Failed to fetch capabilities." };
        }

        public async Task<SetVcpResponse> SetVcpAsync(string monitorPnpId, byte vcpCode, uint value, CancellationToken cancellationToken = default)
        {
            if (_isLegacyTcp)
            {
                return await LegacyTcpSetVcpAsync(monitorPnpId, vcpCode, value, cancellationToken);
            }

            EnsureGrpcClient();
            var (hash, ts) = CreateHmac("SET_VCP");

            try
            {
                var result = await _grpcService!.SetVcpAsync(hash, ts, monitorPnpId, vcpCode, value).ResponseAsync;
                if (result.Success && CachedCapabilities != null)
                {
                    // Optimistically update cached capability value
                    var mon = CachedCapabilities.Monitors.FirstOrDefault(m => m.DeviceID == monitorPnpId);
                    var feat = mon?.Capabilities.FirstOrDefault(f => f.Code == vcpCode);
                    if (feat != null)
                    {
                        feat.CurrentValue = value;
                    }
                }
                return result;
            }
            catch
            {
                // Fallback to TCP if gRPC failed
                _isLegacyTcp = true;
                return await LegacyTcpSetVcpAsync(monitorPnpId, vcpCode, value, cancellationToken);
            }
        }

        public void Disconnect()
        {
            if (State == ConnectionState.Offline) return;

            State = ConnectionState.Offline;
            _isLegacyTcp = false;
            _channel?.Dispose();
            _channel = null;
            _grpcService = null;
            ConnectionStateChanged?.Invoke(this, State);
        }

        #region Legacy TCP Fallback

        private async Task<bool> LegacyTcpPingAsync(CancellationToken cancellationToken)
        {
            string resp = await LegacyTcpSendAsync("PING", isTest: true, cancellationToken);
            return resp == "PING_SUCCESS" || resp.StartsWith("OK");
        }

        private async Task<CapabilitiesResponse> LegacyTcpGetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            string resp = await LegacyTcpSendAsync("GET_CAPS", isTest: false, cancellationToken);
            if (resp.StartsWith("ERROR:"))
            {
                return new CapabilitiesResponse { Success = false, Message = resp };
            }

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            var response = new CapabilitiesResponse
            {
                Success = true,
                Message = TryGetPropString(root, "OK", "message", "Message")
            };

            if (TryGetProp(root, out var monitorsProp, "monitors", "Monitors") && monitorsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var monEl in monitorsProp.EnumerateArray())
                {
                    var monDto = new MonitorInfoDto
                    {
                        DeviceID = TryGetPropString(monEl, "", "id", "DeviceID", "deviceId"),
                        Description = TryGetPropString(monEl, "", "description", "Description")
                    };

                    if (TryGetProp(monEl, out var capsEl, "capabilities", "Capabilities") && capsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var capEl in capsEl.EnumerateArray())
                        {
                            var featDto = new VcpFeatureDto
                            {
                                Code = TryGetPropByte(capEl, 0, "code", "Code"),
                                Name = TryGetPropString(capEl, "", "name", "Name"),
                                Type = TryGetPropString(capEl, "Continuous", "type", "Type"),
                                ReadWrite = TryGetPropBool(capEl, true, "readWrite", "ReadWrite"),
                                CurrentValue = TryGetPropUInt(capEl, 0, "current", "CurrentValue", "currentValue"),
                                MaximumValue = TryGetPropUInt(capEl, 100, "max", "MaximumValue", "maximumValue")
                            };

                            if (TryGetProp(capEl, out var ncVal, "nonContinuousValues", "NonContinuousValues") && ncVal.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var ncProp in ncVal.EnumerateObject())
                                {
                                    if (uint.TryParse(ncProp.Name, out uint k))
                                    {
                                        featDto.NonContinuousValues[k] = ncProp.Value.GetString() ?? "";
                                    }
                                }
                            }

                            monDto.Capabilities.Add(featDto);
                        }
                    }

                    response.Monitors.Add(monDto);
                }
            }

            CachedCapabilities = response;
            return response;
        }

        private async Task<SetVcpResponse> LegacyTcpSetVcpAsync(string monitorPnpId, byte vcpCode, uint value, CancellationToken cancellationToken)
        {
            string cmd = $"SET:{monitorPnpId}:{vcpCode}:{value}";
            string resp = await LegacyTcpSendAsync(cmd, isTest: false, cancellationToken);
            bool ok = !resp.StartsWith("ERROR:");
            if (ok && CachedCapabilities != null)
            {
                var mon = CachedCapabilities.Monitors.FirstOrDefault(m => m.DeviceID == monitorPnpId);
                var feat = mon?.Capabilities.FirstOrDefault(f => f.Code == vcpCode);
                if (feat != null)
                {
                    feat.CurrentValue = value;
                }
            }
            return new SetVcpResponse { Success = ok, Message = resp };
        }

        private async Task<string> LegacyTcpSendAsync(string command, bool isTest, CancellationToken cancellationToken)
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(isTest ? 5000 : 30000);

            await client.ConnectAsync(Host, Port, cts.Token);
            await using var stream = client.GetStream();

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string timestampStr = timestamp.ToString(CultureInfo.InvariantCulture);
            string messageToHash = command + timestampStr;

            using var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(_password));
            byte[] computedHash = hmac.ComputeHash(Encoding.ASCII.GetBytes(messageToHash));
            string hashBase64 = Convert.ToBase64String(computedHash);

            string message = $"{timestampStr}|{hashBase64}|{command}";
            byte[] data = Encoding.ASCII.GetBytes(message);
            await stream.WriteAsync(data.AsMemory(0, data.Length), cts.Token);

            using var ms = new MemoryStream();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
            {
                ms.Write(buffer, 0, bytesRead);
            }
            string response = Encoding.UTF8.GetString(ms.ToArray()).Trim();

            if (isTest)
            {
                if (response.StartsWith("ERROR: Invalid Hash"))
                    throw new Exception("Invalid password (hash mismatch).");
                return "PING_SUCCESS";
            }

            return response;
        }

        private static bool TryGetProp(JsonElement el, out JsonElement val, params string[] names)
        {
            foreach (var n in names)
            {
                if (el.TryGetProperty(n, out val)) return true;
            }
            foreach (var prop in el.EnumerateObject())
            {
                foreach (var n in names)
                {
                    if (string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase))
                    {
                        val = prop.Value;
                        return true;
                    }
                }
            }
            val = default;
            return false;
        }

        private static string TryGetPropString(JsonElement el, string fallback, params string[] names)
        {
            return TryGetProp(el, out var val, names) && val.ValueKind == JsonValueKind.String
                ? val.GetString() ?? fallback
                : fallback;
        }

        private static byte TryGetPropByte(JsonElement el, byte fallback, params string[] names)
        {
            if (TryGetProp(el, out var val, names) && val.ValueKind == JsonValueKind.Number && val.TryGetByte(out byte b))
            {
                return b;
            }
            return fallback;
        }

        private static uint TryGetPropUInt(JsonElement el, uint fallback, params string[] names)
        {
            if (TryGetProp(el, out var val, names) && val.ValueKind == JsonValueKind.Number && val.TryGetUInt32(out uint u))
            {
                return u;
            }
            return fallback;
        }

        private static bool TryGetPropBool(JsonElement el, bool fallback, params string[] names)
        {
            if (TryGetProp(el, out var val, names))
            {
                if (val.ValueKind == JsonValueKind.True) return true;
                if (val.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        #endregion
    }
}