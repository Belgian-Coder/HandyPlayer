using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Services;

namespace HandyPlaylistPlayer.Core.Features.PatternMode.GeneratePattern;

public record GeneratePatternCommand(
    PatternType Pattern,
    double FrequencyHz = 1.0,
    int AmplitudeMin = 0,
    int AmplitudeMax = 100,
    int DurationMs = 60000,
    int? Seed = null) : ICommand;
