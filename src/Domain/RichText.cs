using System.Text.RegularExpressions;

namespace HomuraLog.Domain;

/// <summary>Converts localized game rich text into text safe for ordinary Godot labels.</summary>
public static partial class RichText
{
    [GeneratedRegex(@"\[(?:/?[a-zA-Z_][^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    public static string ToPlainText(string? value)
        => string.IsNullOrEmpty(value) ? "" : TagPattern().Replace(value, "");
}
