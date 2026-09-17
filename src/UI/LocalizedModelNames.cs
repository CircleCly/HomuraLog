using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace HomuraLog.UI;

internal static class LocalizedModelNames
{
    private static string _language = "";
    private static Dictionary<string, string> _cards = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _potions = new(StringComparer.Ordinal);

    public static string Card(string id)
    {
        RefreshIfNeeded();
        return _cards.GetValueOrDefault(id, Humanize(id));
    }

    public static string Potion(string id)
    {
        RefreshIfNeeded();
        return _potions.GetValueOrDefault(id, Humanize(id));
    }

    public static string Choice(string token)
    {
        string[] parts = token.Split("::", StringSplitOptions.None);
        if (parts.Length < 2) return token;
        if (parts[0] == "INDEX") return $"#{parts[1]}";
        if (parts[0] == "PLAYER") return $"Player #{parts[1]}";
        string title = Card(parts[0]);
        int upgrade = parts.Length > 2 && parts[2].Length > 1
            && int.TryParse(parts[2].AsSpan(1), out int value) ? value : 0;
        return upgrade > 0 ? $"{title}+{upgrade}" : title;
    }

    private static void RefreshIfNeeded()
    {
        string language = LocManager.Instance.Language;
        if (string.Equals(language, _language, StringComparison.Ordinal)) return;
        _language = language;
        _cards = new Dictionary<string, string>(StringComparer.Ordinal);
        _potions = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (CardModel card in ModelDb.AllCards)
                _cards.TryAdd(card.Id.Entry, card.Title);
            foreach (PotionModel potion in ModelDb.AllPotions)
                _potions.TryAdd(potion.Id.Entry, potion.Title.GetFormattedText());
        }
        catch
        {
            // Third-party models can fail to localize. A readable ID fallback keeps UI observational.
        }
    }

    private static string Humanize(string id) => string.Join(' ', id.Split(['_', '-'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
}
