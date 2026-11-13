using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandyPlaylistPlayer.Devices.HandyApi.Models;

/// <summary>
/// Source-generated serializer metadata for every Handy API payload: no reflection at first use
/// (faster connect), trimming/AOT friendly. Uses the same Web defaults as the HttpClient JSON extensions had.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HandyConnectedResponse))]
[JsonSerializable(typeof(HandyInfoResponse))]
[JsonSerializable(typeof(HandyModeRequest))]
[JsonSerializable(typeof(HandyModeResponse))]
[JsonSerializable(typeof(HandyHsspSetupRequest))]
[JsonSerializable(typeof(HandyHsspPlayRequest))]
[JsonSerializable(typeof(HandyHsspSyncTimeRequest))]
[JsonSerializable(typeof(HandyHstpOffsetRequest))]
[JsonSerializable(typeof(HandyServerTimeResponse))]
[JsonSerializable(typeof(HandySlideResponse))]
[JsonSerializable(typeof(HandySlideRequest))]
[JsonSerializable(typeof(HandyStatusResponse))]
[JsonSerializable(typeof(HandyHdspXptRequest))]
[JsonSerializable(typeof(HandyHdspXpvaRequest))]
[JsonSerializable(typeof(HandyHdspXavaRequest))]
[JsonSerializable(typeof(HandyHdspResponse))]
[JsonSerializable(typeof(HandyErrorResponse))]
[JsonSerializable(typeof(HandyHostingUploadResponse))]
internal sealed partial class HandyApiJsonContext : JsonSerializerContext;

/// <summary>Cached, read-only serializer options backed by <see cref="HandyApiJsonContext"/>.</summary>
internal static class HandyApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = HandyApiJsonContext.Default
    };
}
