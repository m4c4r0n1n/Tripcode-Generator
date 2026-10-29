using System.Text.Json;
using System.Diagnostics;

namespace TripcodeCudaGui;

internal sealed class SearchHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; }
    public int ProcessId { get; set; }
    public string Targets { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Charset { get; set; } = "";
    public string Compute { get; set; } = "";
    public string Status { get; set; } = "Running";
    public double DurationSeconds { get; set; }
    public long CandidatesTried { get; set; }
    public long HitCount { get; set; }
    public List<SearchHistoryHit> Hits { get; set; } = [];
}

internal sealed class SearchHistoryHit
{
    public string Password { get; set; } = "";
    public string Tripcode { get; set; } = "";
    public string MatchedTarget { get; set; } = "";
}

internal sealed class SearchHistoryStore
{
    public const int MaximumSavedHitsPerSearch = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tripcode Generator", "search-history");

    public List<SearchHistoryEntry> Entries { get; }

    public SearchHistoryStore()
    {
        if (!Directory.Exists(_directory))
        {
            Entries = [];
            return;
        }

        Entries = [];
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            Entries.Add(JsonSerializer.Deserialize<SearchHistoryEntry>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException($"The saved search history entry '{Path.GetFileName(path)}' is invalid."));
        }

        foreach (var entry in Entries)
        {
            if (entry.Status != "Running" || IsProcessActive(entry.ProcessId)) continue;
            entry.Status = "Interrupted";
            Save(entry);
        }
    }

    private static bool IsProcessActive(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Add(SearchHistoryEntry entry)
    {
        Entries.Add(entry);
        Save(entry);
    }

    public void Save(SearchHistoryEntry entry)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, entry.Id.ToString("N") + ".json");
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
