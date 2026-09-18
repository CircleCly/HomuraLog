using System.Globalization;
using HomuraLog.Domain;
using MegaCrit.Sts2.Core.Localization;

namespace HomuraLog.UI;

internal static class LocalizedIntent
{
    public static string Format(CreatureState enemy)
    {
        if (enemy.Intents is { Count: > 0 })
            return string.Join(" + ", enemy.Intents.Select(Format));

        // Schema v1 records only contain already-rendered text. It cannot be
        // translated safely, so only show it when it plausibly matches the UI.
        bool containsCjk = enemy.Intent.Any(character => character is >= '\u3400' and <= '\u9fff');
        return containsCjk == HomuraText.Chinese ? enemy.Intent : "";
    }

    private static string Format(IntentState intent)
    {
        try
        {
            string title = new LocString("intents", intent.TitleKey).GetFormattedText().Trim();
            LocString label = new("intents", intent.LabelKey);
            foreach (IntentVariable variable in intent.Variables ?? [])
                label.AddObj(variable.Name, Parse(variable));
            string labelText = label.GetFormattedText().Trim();
            return string.IsNullOrEmpty(labelText) ? title
                : string.IsNullOrEmpty(title) ? labelText : $"{title} {labelText}";
        }
        catch
        {
            return "";
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
