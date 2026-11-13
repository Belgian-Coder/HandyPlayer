using System.Text.Json;
using System.Text.Json.Serialization;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Services;

public class FunscriptParser : IFunscriptParser
{
    public async Task<FunscriptDocument> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        var raw = await JsonSerializer.DeserializeAsync(stream, FunscriptJsonContext.Default.RawFunscript, ct)
            ?? throw new InvalidDataException("Failed to deserialize funscript");

        var rawActions = raw.Actions;
        var actions = new List<FunscriptAction>(rawActions?.Count ?? 0);
        if (rawActions != null)
        {
            bool sorted = true;
            long previousAt = long.MinValue;
            foreach (var a in rawActions)
            {
                if (a.At < 0) continue; // negative timestamps are dropped (NaN is not representable in JSON)

                var action = FunscriptAction.Clamped((long)Math.Round(a.At), (int)Math.Round(a.Pos));
                if (action.At < previousAt) sorted = false;
                previousAt = action.At;
                actions.Add(action);
            }

            // Scripts are almost always already in time order; only pay for the sort when they are not.
            if (!sorted)
                actions.Sort(static (x, y) => x.At.CompareTo(y.At));
        }

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
        // (Measured: async FileStream + unbuffered reads was ~1.5x slower than the default for the serializer's 16 KB reads.)
        await using var stream = File.OpenRead(filePath);
        return await ParseAsync(stream, ct);
    }
}

internal sealed class RawFunscript
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

// A struct: List<RawAction> is one contiguous array instead of one heap object per action.
internal struct RawAction
{
    [JsonPropertyName("at")]
    public double At { get; set; }

    [JsonPropertyName("pos")]
    public double Pos { get; set; }
}

/// <summary>Source-generated metadata (no reflection / startup cost, trimming and AOT friendly).</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RawFunscript))]
internal sealed partial class FunscriptJsonContext : JsonSerializerContext;
