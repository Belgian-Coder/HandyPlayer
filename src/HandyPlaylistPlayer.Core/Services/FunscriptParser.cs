using System.Text.Json;
using System.Text.Json.Serialization;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Services;

public class FunscriptParser : IFunscriptParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<FunscriptDocument> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        var raw = await JsonSerializer.DeserializeAsync<RawFunscript>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("Failed to deserialize funscript");

        var rawActions = raw.Actions ?? [];
        var actions = new List<FunscriptAction>(rawActions.Count);
        foreach (var a in rawActions)
        {
            if (a.At >= 0)
                actions.Add(FunscriptAction.Clamped((long)Math.Round(a.At), (int)Math.Round(a.Pos)));
        }
        actions.Sort((x, y) => x.At.CompareTo(y.At));

        return new FunscriptDocument
        {
            Version = raw.Version ?? "1.0",
            Inverted = raw.Inverted,
            Range = Math.Clamp(raw.Range ?? 100, 0, 100),
            Actions = actions
        };
    }

    public async Task<FunscriptDocument> ParseFileAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ParseAsync(stream, ct);
    }

    private class RawFunscript
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("inverted")]
        public bool Inverted { get; set; }

        [JsonPropertyName("range")]
        public int? Range { get; set; }

        [JsonPropertyName("actions")]
        public List<RawAction>? Actions { get; set; }
    }

    private class RawAction
    {
        [JsonPropertyName("at")]
        public double At { get; set; }

        [JsonPropertyName("pos")]
        public double Pos { get; set; }
    }
}
