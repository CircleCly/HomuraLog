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
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
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
            if (layout?.SchemaVersion == SavedLargeWindowLayout.CurrentSchemaVersion
                && IsFinite(layout) && layout.Width > 0 && layout.Height > 0)
                return layout;
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
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        SavedLargeWindowLayout layout = new(SavedLargeWindowLayout.CurrentSchemaVersion,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, viewportWidth, viewportHeight,
            DateTimeOffset.UtcNow);
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
}
