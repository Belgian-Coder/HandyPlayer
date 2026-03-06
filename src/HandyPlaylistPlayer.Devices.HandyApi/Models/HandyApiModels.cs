using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandyPlaylistPlayer.Devices.HandyApi.Models;

public class HandyConnectedResponse
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
}

public class HandyInfoResponse
{
    [JsonPropertyName("fwVersion")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string FwVersion { get; set; } = string.Empty;

    [JsonPropertyName("fwStatus")]
    public int FwStatus { get; set; }

    [JsonPropertyName("hwVersion")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string HwVersion { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Branch { get; set; } = string.Empty;
}

/// <summary>
/// Reads JSON values as strings regardless of whether the JSON token is a string, number, or boolean.
/// Handles Handy API responses where field types can vary between firmware versions.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(),
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

public class HandyModeRequest
{
    [JsonPropertyName("mode")]
    public int Mode { get; set; }
}

public class HandyModeResponse
{
    [JsonPropertyName("mode")]
    public int Mode { get; set; }
}

public class HandyHsspSetupRequest
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

public class HandyHsspPlayRequest
{
    [JsonPropertyName("estimatedServerTime")]
    public long EstimatedServerTime { get; set; }

    [JsonPropertyName("startTime")]
    public long StartTime { get; set; }

    [JsonPropertyName("loop")]
    public bool Loop { get; set; }

    [JsonPropertyName("playbackRate")]
    public double PlaybackRate { get; set; } = 1.0;
}

public class HandyHsspSyncTimeRequest
{
    [JsonPropertyName("currentTime")]
    public long CurrentTime { get; set; }

    [JsonPropertyName("serverTime")]
    public long ServerTime { get; set; }

    [JsonPropertyName("filter")]
    public double Filter { get; set; }
}

public class HandyHstpOffsetRequest
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}

public class HandyServerTimeResponse
{
    [JsonPropertyName("time")]
    public long ServerTime { get; set; }
}

public class HandySlideResponse
{
    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }
}

public class HandySlideRequest
{
    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }
}

public class HandyStatusResponse
{
    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("state")]
    public int State { get; set; }
}

public class HandyHdspXptRequest
{
    [JsonPropertyName("position")]
    public double Position { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("stopOnTarget")]
    public bool StopOnTarget { get; set; }

    [JsonPropertyName("immediateResponse")]
    public bool ImmediateResponse { get; set; }
}

public class HandyHdspXpvaRequest
{
    [JsonPropertyName("position")]
    public double Position { get; set; }

    [JsonPropertyName("velocity")]
    public int Velocity { get; set; }

    [JsonPropertyName("stopOnTarget")]
    public bool StopOnTarget { get; set; }

    [JsonPropertyName("immediateResponse")]
    public bool ImmediateResponse { get; set; }
}

public class HandyHdspXavaRequest
{
    [JsonPropertyName("position")]
    public double Position { get; set; }

    [JsonPropertyName("velocity")]
    public int Velocity { get; set; }

    [JsonPropertyName("stopOnTarget")]
    public bool StopOnTarget { get; set; }

    [JsonPropertyName("immediateResponse")]
    public bool ImmediateResponse { get; set; }
}

public class HandyHdspResponse
{
    [JsonPropertyName("result")]
    public int Result { get; set; }
}

/// <summary>
/// Error response returned in 200 body when device encounters an error.
/// The API returns oneOf(ErrorResponse, HDSPResponse) at HTTP 200.
/// </summary>
public class HandyErrorResponse
{
    [JsonPropertyName("error")]
    public HandyErrorDetail? Error { get; set; }
}

public class HandyErrorDetail
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class HandyHostingUploadResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
