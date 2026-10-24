using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dialvert.Core.Jobs;

/// <summary>A finished job as it is kept in the history across restarts.</summary>
public sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Title { get; set; } = "";
    public JobState State { get; set; }
    public string? Message { get; set; }
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public long BytesBefore { get; set; }
    public long BytesAfter { get; set; }

    public static HistoryEntry From(Job job)
    {
        var done = job.State == JobState.Done;
        return new HistoryEntry
        {
            Time = DateTime.Now,
            Title = job.Title,
            State = job.State,
            Message = job.Message,
            Inputs = [.. job.Inputs],
            Outputs = [.. job.Outputs],
            BytesBefore = done ? job.Inputs.Where(File.Exists).Sum(p => new FileInfo(p).Length) : 0,
            BytesAfter = done ? job.Outputs.Where(File.Exists).Sum(p => new FileInfo(p).Length) : 0,
        };
    }

    [JsonIgnore] public string? FirstOutput => Outputs.FirstOrDefault(File.Exists);

    // Display text for the history list ------------------------------------

    /// <summary>Result file name(s), or the error.</summary>
    [JsonIgnore]
    public string SubText => State switch
    {
        JobState.Failed => Message ?? "Failed",
        JobState.Canceled => "Canceled",
        _ => Outputs.Count switch { 0 => Message ?? "Done", 1 => Path.GetFileName(Outputs[0]), var n => $"{n} files" },
    };

    [JsonIgnore]
    public string SizeText => BytesBefore > 0 && BytesAfter > 0
        ? $"{CompressSpec.Describe(BytesBefore)} → {CompressSpec.Describe(BytesAfter)}" : "";

    [JsonIgnore]
    public string DeltaText => BytesBefore > 0 && BytesAfter > 0
        ? $"{(BytesAfter - BytesBefore) * 100.0 / BytesBefore:+0;−0;±0} %" : "";

    [JsonIgnore] public bool Grew => BytesAfter > BytesBefore;

    [JsonIgnore]
    public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm")
        : Time.Date == DateTime.Today.AddDays(-1) ? "yesterday" : Time.ToString("MMM d", CultureInfo.InvariantCulture);
}

/// <summary>JSON list of the most recent entries, newest first, in %LOCALAPPDATA%\Dialvert\history.json.</summary>
public sealed class HistoryStore
{
    const int Limit = 500;
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string FilePath { get; }

    public HistoryStore(string? filePath = null) =>
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dialvert", "history.json");

    public List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException) { /* start over with an empty history */ }
        return [];
    }

    public void Save(IEnumerable<HistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries.Take(Limit), Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
