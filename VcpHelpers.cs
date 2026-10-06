using System.Text.RegularExpressions;

namespace MultiDisplayVCPClient
{
    /// <summary>
    /// Helper utilities for slugifying display names and formatting Macro Deck local IDs.
    /// </summary>
    public static partial class VcpHelpers
    {
        [GeneratedRegex(@"[^a-z0-9\s_-]", RegexOptions.Compiled)]
        private static partial Regex SlugifyInvalidCharsRegex();

        [GeneratedRegex(@"[\s_-]+", RegexOptions.Compiled)]
        private static partial Regex SlugifySeparatorsRegex();

        /// <summary>
        /// Converts human-readable text into a clean snake_case slug suitable for variable names.
        /// </summary>
        public static string Slugify(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            string slug = text.ToLowerInvariant();
            slug = SlugifyInvalidCharsRegex().Replace(slug, " ");
            slug = SlugifySeparatorsRegex().Replace(slug, "_");
            return slug.Trim('_');
        }

        /// <summary>
        /// Sanitizes a variable name into a valid Macro Deck LocalIdKind.Declared ID
        /// (lowercase, hyphen-separated, starting with a letter, max 64 characters).
        /// </summary>
        public static string ToLocalId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "var";

            string id = name.Trim().ToLowerInvariant();
            id = Regex.Replace(id, @"[^a-z0-9-]+", "-");
            id = Regex.Replace(id, @"-+", "-").Trim('-');

            if (id.Length == 0 || id[0] < 'a' || id[0] > 'z')
            {
                id = "v-" + id;
            }

            if (id.Length > 64)
            {
                string hash = Math.Abs(name.GetHashCode()).ToString("x4");
                id = id[..58].TrimEnd('-') + "-" + hash;
            }

            return id;
        }
    }
}
