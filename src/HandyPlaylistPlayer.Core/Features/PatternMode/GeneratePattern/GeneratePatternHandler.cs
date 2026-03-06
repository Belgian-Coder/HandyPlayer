using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Services;

namespace HandyPlaylistPlayer.Core.Features.PatternMode.GeneratePattern;

public class GeneratePatternHandler(IPlaybackCoordinator coordinator)
    : ICommandHandler<GeneratePatternCommand, Unit>
{
    public async Task<Unit> HandleAsync(GeneratePatternCommand cmd, CancellationToken ct = default)
    {
        var doc = PatternGenerator.Generate(
            cmd.Pattern, cmd.FrequencyHz, cmd.AmplitudeMin,
            cmd.AmplitudeMax, cmd.DurationMs, cmd.Seed);
        await coordinator.LoadPatternAsync(doc, ct);
        await coordinator.PlayAsync();
        return Unit.Value;
    }
}
