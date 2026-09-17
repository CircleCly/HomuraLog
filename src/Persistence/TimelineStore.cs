using System.Text.Json;
using HomuraLog.Domain;

namespace HomuraLog.Persistence;

public sealed class TimelineStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // A worldline is intentionally a strict prefix tree. Long combats can exceed
        // System.Text.Json's default nesting limit of 64 without containing a cycle.
        MaxDepth = 4096,
    };
    private readonly string _directory;

    public TimelineStore(string directory) => _directory = directory;

    public EncounterRecord? Load(string encounterKey)
    {
        string path = PathFor(encounterKey);
        if (!File.Exists(path)) return null;
        try
        {
            EncounterRecord? record = JsonSerializer.Deserialize<EncounterRecord>(File.ReadAllBytes(path), Json);
            if (record?.SchemaVersion == EncounterRecord.CurrentSchemaVersion) return record;
            Backup(path, ".unsupported");
            return null;
        }
        catch (JsonException)
        {
            Backup(path, ".corrupt");
            return null;
        }
    }

    public void Save(EncounterRecord record)
    {
        Directory.CreateDirectory(_directory);
        string path = PathFor(record.EncounterKey);
        string temporary = path + ".tmp";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, path, true);
    }

    public void SaveSummaryAndPrune(EncounterRecord record)
    {
        string summaryDirectory = Path.Combine(_directory, "archive");
        Directory.CreateDirectory(summaryDirectory);
        var summary = new
        {
            record.SchemaVersion, record.EncounterKey, record.RunId, record.Floor, record.EncounterId,
            record.StartedAt, record.EndedAt, record.Outcome,
            Nodes = Count(record.Root), Attempts = record.Root.Children.Values.Sum(x => x.VisitCount),
        };
        string path = Path.Combine(summaryDirectory, SafeName(record.EncounterKey) + ".json");
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(summary, Json));
        File.Move(path + ".tmp", path, true);
        string fullPath = PathFor(record.EncounterKey);
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    private string PathFor(string key) => Path.Combine(_directory, SafeName(key) + ".json");
    private static string SafeName(string key) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..32];
    private static int Count(TimelineNode node) => 1 + node.Children.Values.Sum(Count);
    private static void Backup(string path, string suffix) => File.Move(path, path + suffix + "." + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true);
}
