using MacroDeck.Localization;
using MacroDeck.Sdk.Variables;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Specification for common VCP features exposed automatically as eager variables.
    /// </summary>
    public sealed record CommonFeatureSpec(
        byte Code,
        string SlotName,
        string NameSuffix,
        string DisplayName,
        string Description);

    /// <summary>
    /// Static templates for MultiDisplayVCP variables shown prior to configuration.
    /// Uses VariableNameTemplate.Placeholder("configuration") with Id = null
    /// so Macro Deck displays them as 'Available after setup' under Integrations.
    /// </summary>
    public static class VcpVariableTemplates
    {
        public static readonly CommonFeatureSpec[] CommonFeatures =
        [
            new(0x10, "brightness", "brightness", "Brightness", "Display brightness level (0-100)"),
            new(0x12, "contrast", "contrast", "Contrast", "Display contrast level (0-100)"),
            new(0x60, "input-select", "input_select", "Input Select", "Display video input source"),
            new(0xD6, "power-mode", "power_mode", "Power Mode", "Display power mode"),
            new(0x14, "color-preset", "select_color_preset", "Select Color Preset", "Display color temperature preset"),
            new(0x62, "speaker-volume", "audio_speaker_volume", "Audio: Speaker Volume", "Display speaker volume (0-100)")
        ];

        public static bool IsCommonVcpCode(byte code) => CommonFeatures.Any(f => f.Code == code);

        public static IReadOnlyList<VariableDefinition> Templates { get; } = CreateTemplates();

        private static List<VariableDefinition> CreateTemplates()
        {
            string conn = VariableNameTemplate.Placeholder("configuration");
            string disp = VariableNameTemplate.Placeholder("display");

            var templates = new List<VariableDefinition>
            {
                VariableDefinition.Eager($"multidisplay_{conn}_connected", VariableType.Boolean) with
                {
                    Id = null,
                    Configuration = null,
                    DisplayName = LocalizedText.FromLiteral("Connected"),
                    Description = LocalizedText.FromLiteral("Connection state to VCP server"),
                    IsBindable = true
                }
            };

            foreach (var cf in CommonFeatures)
            {
                templates.Add(
                    VariableDefinition.Eager($"multidisplay_{conn}_{disp}_{cf.NameSuffix}", VariableType.Numeric, decimalPlaces: 0, refreshInterval: TimeSpan.FromSeconds(2)) with
                    {
                        Id = null,
                        Configuration = null,
                        DisplayName = LocalizedText.FromLiteral(cf.DisplayName),
                        Description = LocalizedText.FromLiteral(cf.Description),
                        IsBindable = true,
                        Write = new VariableWriteCapability { CommitOnRelease = false }
                    });
            }

            return templates;
        }
    }
}
