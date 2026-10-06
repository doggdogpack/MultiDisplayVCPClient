namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Metadata and state for an active VCP variable registered with Macro Deck 3.
    /// </summary>
    public sealed class VcpVariableInfo
    {
        public required string VariableId { get; init; }
        public required string ConnectionName { get; init; }
        public required string MonitorPnpId { get; init; }
        public required string MonitorDescription { get; init; }
        public required byte VcpCode { get; init; }
        public required string FeatureName { get; init; }
        public required uint MinValue { get; init; }
        public required uint MaxValue { get; init; }
        public uint CurrentValue { get; set; }
        public required bool ReadWrite { get; init; }
    }
}
