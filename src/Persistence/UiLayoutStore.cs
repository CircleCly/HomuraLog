using System.Text.Json;
using HomuraLog.Domain;

namespace HomuraLog.Persistence;

public sealed record SavedLargeWindowLayout(
    int SchemaVersion,
    float X,
    float Y,
    float Width,
    float Height,
    float ViewportWidth,
    float ViewportHeight,
    DateTimeOffset UpdatedAt,
    float? RelativeX = null,
    float? RelativeY = null,
    float? RelativeWidth = null,
    float? RelativeHeight = null)
{
    public const int CurrentSchemaVersion = 2;

    public LargeWindowBounds Resolve(float viewportWidth, float viewportHeight)
    {
        if (RelativeX is { } x && RelativeY is { } y
            && RelativeWidth is { } width && RelativeHeight is { } height
            && float.IsFinite(x) && float.IsFinite(y)
            && float.IsFinite(width) && float.IsFinite(height)
            && width > 0 && height > 0)
            return new LargeWindowBounds(x * viewportWidth, y * viewportHeight,
                width * viewportWidth, height * viewportHeight);
        return new LargeWindowBounds(X, Y, Width, Height);
    }
}

public sealed class UiLayoutStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _path;

    public UiLayoutStore(string path) => _path = path;

    public SavedLargeWindowLayout? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            SavedLargeWindowLayout? layout = JsonSerializer.Deserialize<SavedLargeWindowLayout>(
                File.ReadAllBytes(_path), Json);
            if (layout != null && IsFinite(layout) && layout.Width > 0 && layout.Height > 0)
            {
                if (layout.SchemaVersion == SavedLargeWindowLayout.CurrentSchemaVersion
                    && HasValidRelativeBounds(layout))
                    return layout;
                if (layout.SchemaVersion == 1 && layout.ViewportWidth > 0 && layout.ViewportHeight > 0)
                    return layout with
                    {
                        SchemaVersion = SavedLargeWindowLayout.CurrentSchemaVersion,
                        RelativeX = layout.X / layout.ViewportWidth,
                        RelativeY = layout.Y / layout.ViewportHeight,
                        RelativeWidth = layout.Width / layout.ViewportWidth,
                        RelativeHeight = layout.Height / layout.ViewportHeight,
                    };
            }
            Backup(".unsupported");
        }
        catch (Exception) when (File.Exists(_path))
        {
            Backup(".corrupt");
        }
        return null;
    }

    public void Save(LargeWindowBounds bounds, float viewportWidth, float viewportHeight)
    {
        if (!float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight)
            || viewportWidth <= 0 || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "Viewport dimensions must be positive and finite.");
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        SavedLargeWindowLayout layout = new(SavedLargeWindowLayout.CurrentSchemaVersion,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, viewportWidth, viewportHeight,
            DateTimeOffset.UtcNow,
            RelativeX: bounds.X / viewportWidth,
            RelativeY: bounds.Y / viewportHeight,
            RelativeWidth: bounds.Width / viewportWidth,
            RelativeHeight: bounds.Height / viewportHeight);
        string temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(layout, Json));
        File.Move(temporary, _path, true);
    }

    private void Backup(string suffix)
    {
        try
        {
            File.Move(_path, _path + suffix + "." + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true);
        }
        catch { }
    }

    private static bool IsFinite(SavedLargeWindowLayout layout) =>
        float.IsFinite(layout.X) && float.IsFinite(layout.Y)
        && float.IsFinite(layout.Width) && float.IsFinite(layout.Height)
        && float.IsFinite(layout.ViewportWidth) && float.IsFinite(layout.ViewportHeight);

    private static bool HasValidRelativeBounds(SavedLargeWindowLayout layout) =>
        layout.RelativeX is { } x && layout.RelativeY is { } y
        && layout.RelativeWidth is { } width && layout.RelativeHeight is { } height
        && float.IsFinite(x) && float.IsFinite(y)
        && float.IsFinite(width) && float.IsFinite(height)
        && width > 0 && height > 0;
}
