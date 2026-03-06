using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class AutoPairingEngineTests
{
    private readonly IFilenameNormalizer _normalizer = new FilenameNormalizer();
    private readonly IMediaFileRepository _mediaRepo = Substitute.For<IMediaFileRepository>();
    private readonly IPairingRepository _pairingRepo = Substitute.For<IPairingRepository>();
    private readonly ILogger<AutoPairingEngine> _logger = Substitute.For<ILogger<AutoPairingEngine>>();
    private readonly AutoPairingEngine _engine;

    public AutoPairingEngineTests()
    {
        // Default: return empty dictionary for batch pairing lookup
        _pairingRepo.GetAllPairingsAsync().Returns(new Dictionary<int, Pairing>());
        _engine = new AutoPairingEngine(_normalizer, _mediaRepo, _pairingRepo, _logger);
    }

    private static MediaItem MakeVideo(int id, string folder, string name) => new()
    {
        Id = id, FullPath = Path.Combine(folder, name + ".mp4"),
        Filename = name, Extension = ".mp4", IsScript = false
    };

    private static MediaItem MakeScript(int id, string folder, string name) => new()
    {
        Id = id, FullPath = Path.Combine(folder, name + ".funscript"),
        Filename = name, Extension = ".funscript", IsScript = true
    };

    [Fact]
    public async Task RunPairing_ExactMatch_SameFolder_Confidence1()
    {
        var video = MakeVideo(1, "/videos", "TestVideo");
        var script = MakeScript(2, "/videos", "TestVideo");
        _mediaRepo.GetAllVideosAsync().Returns([video]);
        _mediaRepo.GetAllScriptsAsync().Returns([script]);

        await _engine.RunPairingAsync();

        await _pairingRepo.Received(1).UpsertAsync(1, 2, false, 1.0);
    }

    [Fact]
    public async Task RunPairing_NormalizedMatch_SameFolder_Confidence09()
    {
        var video = MakeVideo(1, "/videos", "My Video [1080p]");
        var script = MakeScript(2, "/videos", "My_Video");
        _mediaRepo.GetAllVideosAsync().Returns([video]);
        _mediaRepo.GetAllScriptsAsync().Returns([script]);

        await _engine.RunPairingAsync();

        await _pairingRepo.Received(1).UpsertAsync(1, 2, false, 0.9);
    }

    [Fact]
    public async Task RunPairing_CrossFolderMatch_Confidence07()
    {
        var video = MakeVideo(1, "/videos", "SomeVideo");
        var script = MakeScript(2, "/scripts", "SomeVideo");
        _mediaRepo.GetAllVideosAsync().Returns([video]);
        _mediaRepo.GetAllScriptsAsync().Returns([script]);

        await _engine.RunPairingAsync();

        await _pairingRepo.Received(1).UpsertAsync(1, 2, false, 0.7);
    }

    [Fact]
    public async Task RunPairing_NoMatch_NoPairing()
    {
        var video = MakeVideo(1, "/videos", "VideoA");
        var script = MakeScript(2, "/scripts", "CompletelyDifferent");
        _mediaRepo.GetAllVideosAsync().Returns([video]);
        _mediaRepo.GetAllScriptsAsync().Returns([script]);

        await _engine.RunPairingAsync();

        await _pairingRepo.DidNotReceive().UpsertAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<double>());
    }

    [Fact]
    public async Task RunPairing_ManualPairing_NotOverridden()
    {
        var video = MakeVideo(1, "/videos", "TestVideo");
        var script = MakeScript(2, "/videos", "TestVideo");
        _mediaRepo.GetAllVideosAsync().Returns([video]);
        _mediaRepo.GetAllScriptsAsync().Returns([script]);
        _pairingRepo.GetAllPairingsAsync().Returns(new Dictionary<int, Pairing>
        {
            [1] = new() { IsManual = true, VideoFileId = 1, ScriptFileId = 99 }
        });

        await _engine.RunPairingAsync();

        await _pairingRepo.DidNotReceive().UpsertAsync(1, 2, Arg.Any<bool>(), Arg.Any<double>());
    }

    [Fact]
    public async Task RunPairing_ExactMatchWinsOverNormalized()
    {
        var video = MakeVideo(1, "/videos", "TestVideo");
        var exactScript = MakeScript(2, "/videos", "TestVideo");
        var normScript = MakeScript(3, "/videos", "Test_Video");
        _mediaRepo.GetAllVideosAsync().Returns([video]);
        _mediaRepo.GetAllScriptsAsync().Returns([normScript, exactScript]);

        await _engine.RunPairingAsync();

        await _pairingRepo.Received(1).UpsertAsync(1, 2, false, 1.0);
    }

    [Fact]
    public async Task FindScriptForVideo_ReturnsMatchedScript()
    {
        var script = MakeScript(5, "/scripts", "MyScript");
        _pairingRepo.GetForVideoAsync(1).Returns(new Pairing { VideoFileId = 1, ScriptFileId = 5 });
        _mediaRepo.GetByIdAsync(5).Returns(script);

        var result = await _engine.FindScriptForVideoAsync(1);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
    }

    [Fact]
    public async Task FindScriptForVideo_NoPairing_ReturnsNull()
    {
        _pairingRepo.GetForVideoAsync(1).Returns((Pairing?)null);

        var result = await _engine.FindScriptForVideoAsync(1);

        Assert.Null(result);
    }
}
