using MacroDeck.Sdk.Variables;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Handles variable declarations and state when NO valid server configuration exists.
    /// Exposes sample templates marked 'Available after setup' in DeclaredVariables.
    /// </summary>
    public static class VcpPreConfigLogic
    {
        /// <summary>
        /// DeclaredVariables shown on the Integration details page pre-configuration.
        /// Returns the template definitions with placeholder tokens.
        /// </summary>
        public static IReadOnlyList<VariableDefinition> GetDeclaredVariables() => VcpVariableTemplates.Templates;

        /// <summary>
        /// Variables shown on the Variables page pre-configuration.
        /// Returns empty so no unbound dummy variables appear before setup.
        /// </summary>
        public static IReadOnlyList<VariableDefinition> GetVariables() => [];

        /// <summary>
        /// Pre-configuration variable reading fallback.
        /// </summary>
        public static ValueTask<VariableReading> ReadAsync(string localId) =>
            ValueTask.FromResult(VariableReading.Unavailable);
    }
}
