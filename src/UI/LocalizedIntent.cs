using System.Globalization;
using HomuraLog.Domain;
using MegaCrit.Sts2.Core.Localization;

namespace HomuraLog.UI;

internal static class LocalizedIntent
{
    public static string Format(CreatureState enemy) => string.Join('\n', FormatLines(enemy));

    public static IReadOnlyList<string> FormatLines(CreatureState enemy)
    {
        if (enemy.Intents is { Count: > 0 })
        {
            string[] lines = enemy.Intents.Select(Format)
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            return lines.Length > 0 ? lines : [HomuraText.UnknownIntent];
        }

        // Schema v1 records only contain already-rendered text. It cannot be
        // translated safely, so only show it when it plausibly matches the UI.
        bool containsCjk = enemy.Intent.Any(character => character is >= '\u3400' and <= '\u9fff');
        return containsCjk == HomuraText.Chinese && !string.IsNullOrWhiteSpace(enemy.Intent)
            ? [RichText.ToPlainText(enemy.Intent).Trim()]
            : [];
    }

    private static string Format(IntentState intent)
    {
        try
        {
            string title = RichText.ToPlainText(
                new LocString("intents", intent.TitleKey).GetFormattedText()).Trim();
            LocString label = new("intents", intent.LabelKey);
            foreach (IntentVariable variable in intent.Variables ?? [])
                label.AddObj(variable.Name, Parse(variable));
            string labelText = RichText.ToPlainText(label.GetFormattedText()).Trim();
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(labelText))
                return HomuraText.UnknownIntent;
            if (string.IsNullOrEmpty(labelText)) return title;
            if (string.IsNullOrEmpty(title)) return labelText;
            if (labelText.StartsWith(title, StringComparison.CurrentCultureIgnoreCase)) return labelText;
            return $"{title}: {labelText}";
        }
        catch
        {
            return HomuraText.UnknownIntent;
        }
    }

    private static object Parse(IntentVariable variable) => variable.Kind switch
    {
        "bool" when bool.TryParse(variable.Value, out bool value) => value,
        "integer" when decimal.TryParse(variable.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal value) => value,
        "decimal" when decimal.TryParse(variable.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) => value,
        _ => variable.Value,
    };
}
