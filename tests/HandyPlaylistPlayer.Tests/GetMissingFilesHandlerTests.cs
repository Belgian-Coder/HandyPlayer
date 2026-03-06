using HandyPlaylistPlayer.Core.Features.Library.GetMissingFiles;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using NSubstitute;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class GetMissingFilesHandlerTests
{
    private readonly IMediaFileRepository _mediaRepo = Substitute.For<IMediaFileRepository>();
    private readonly GetMissingFilesHandler _handler;

    public GetMissingFilesHandlerTests()
    {
        _handler = new GetMissingFilesHandler(_mediaRepo);
    }

    [Fact]
    public async Task HandleAsync_IncludesMissingScriptFiles()
    {
        var script = new MediaItem { Id = 1, FullPath = "/nonexistent/script.funscript", IsScript = true };
        _mediaRepo.GetAllAsync().Returns([script]);

        var result = await _handler.HandleAsync(new GetMissingFilesQuery());

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public async Task HandleAsync_IncludesMissingVideoFiles()
    {
        var missing = new MediaItem { Id = 2, FullPath = "/nonexistent/guid-" + Guid.NewGuid() + "/video.mp4", IsScript = false };
        _mediaRepo.GetAllAsync().Returns([missing]);

        var result = await _handler.HandleAsync(new GetMissingFilesQuery());

        Assert.Single(result);
        Assert.Equal(missing.Id, result[0].Id);
    }

    [Fact]
    public async Task HandleAsync_ExcludesExistingFiles()
    {
        // Use a path that actually exists (the test assembly's location)
        var existing = new MediaItem { Id = 3, FullPath = typeof(GetMissingFilesHandlerTests).Assembly.Location, IsScript = false };
        _mediaRepo.GetAllAsync().Returns([existing]);

        var result = await _handler.HandleAsync(new GetMissingFilesQuery());

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_MixedFiles_ReturnsAllMissing()
    {
        var existingVideo   = new MediaItem { Id = 1, FullPath = typeof(GetMissingFilesHandlerTests).Assembly.Location, IsScript = false };
        var missingVideo    = new MediaItem { Id = 2, FullPath = "/nonexistent/guid-" + Guid.NewGuid() + "/video.mp4", IsScript = false };
        var missingScript   = new MediaItem { Id = 3, FullPath = "/nonexistent/script.funscript", IsScript = true };

        _mediaRepo.GetAllAsync().Returns([existingVideo, missingVideo, missingScript]);

        var result = await _handler.HandleAsync(new GetMissingFilesQuery());

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == 2);
        Assert.Contains(result, r => r.Id == 3);
    }

    [Fact]
    public async Task HandleAsync_EmptyLibrary_ReturnsEmptyList()
    {
        _mediaRepo.GetAllAsync().Returns([]);

        var result = await _handler.HandleAsync(new GetMissingFilesQuery());

        Assert.Empty(result);
    }
}
