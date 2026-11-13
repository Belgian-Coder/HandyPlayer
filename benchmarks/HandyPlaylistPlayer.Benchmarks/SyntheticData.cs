using System.Text;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Benchmarks;

/// <summary>Deterministic synthetic inputs shared by the benchmarks.</summary>
public static class SyntheticData
{
    public static string FindDemoDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "demo");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.funscript").Length > 0)
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the demo/ folder with *.funscript files");
    }

    public static byte[][] LoadDemoScripts()
        => Directory.GetFiles(FindDemoDirectory(), "*.funscript").OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllBytes).ToArray();

    /// <summary>Funscript JSON text formatted like a typical editor export (compact objects, unsorted-free).</summary>
    public static byte[] BuildFunscriptJson(int actionCount, int seed = 42)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder(actionCount * 22 + 128);
        sb.Append("{\"version\":\"1.0\",\"inverted\":false,\"range\":90,\"actions\":[");
        long at = 0;
        for (int i = 0; i < actionCount; i++)
        {
            if (i > 0) sb.Append(',');
            at += rng.Next(40, 600);
            sb.Append("{\"at\":").Append(at).Append(",\"pos\":").Append(rng.Next(0, 101)).Append('}');
        }
        sb.Append("],\"metadata\":{\"title\":\"synthetic\",\"creator\":\"bench\"}}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static List<FunscriptAction> BuildActions(int count, int seed = 7)
    {
        var rng = new Random(seed);
        var list = new List<FunscriptAction>(count);
        long at = 0;
        for (int i = 0; i < count; i++)
        {
            at += rng.Next(40, 600);
            list.Add(new FunscriptAction(at, rng.Next(0, 101)));
        }
        return list;
    }

    private static readonly string[] Studios = ["Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliet"];
    private static readonly string[] Words = ["red", "night", "river", "soft", "storm", "city", "gold", "ocean", "velvet", "ember", "quiet", "neon", "stone", "dream", "lunar", "wild", "silk", "amber", "frost", "spark"];
    private static readonly string[] Tags = ["1080p", "4K", "x264", "HEVC", "VR", "180", "SBS", "WEBRip", "AAC", "60fps", "uncut", "remastered", "[Studio]", "(2023)", "[Scene 2]", "preview"];

    /// <summary>Realistic-ish base names with release-style noise tags and separators (no extension).</summary>
    public static string BuildBaseName(Random rng)
    {
        var sb = new StringBuilder(64);
        sb.Append(Studios[rng.Next(Studios.Length)]);
        int words = rng.Next(2, 5);
        for (int i = 0; i < words; i++)
        {
            sb.Append(rng.Next(3) switch { 0 => '_', 1 => '.', _ => ' ' });
            sb.Append(Words[rng.Next(Words.Length)]);
        }
        sb.Append('-').Append(rng.Next(1, 4000));
        return sb.ToString();
    }

    public static string AddNoise(string baseName, Random rng)
    {
        var sb = new StringBuilder(baseName);
        int n = rng.Next(1, 4);
        for (int i = 0; i < n; i++)
            sb.Append(rng.Next(2) == 0 ? '_' : ' ').Append(Tags[rng.Next(Tags.Length)]);
        return sb.ToString();
    }

    /// <summary>~N distinct names with noise tags, as raw file names (with extension) for the normalizer.</summary>
    public static string[] BuildFilenames(int count, int seed = 1)
    {
        var rng = new Random(seed);
        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = AddNoise(BuildBaseName(rng), rng) + (rng.Next(4) == 0 ? ".funscript" : ".mp4");
        return names;
    }

    /// <summary>
    /// Pairing workload: <paramref name="count"/> videos and ~<paramref name="count"/> scripts spread over folders.
    /// 55% exact same-folder match, 20% same-folder match after normalization (script has tag noise),
    /// 10% cross-folder normalized match, 15% videos without any script.
    /// </summary>
    public static (List<MediaItem> Videos, List<MediaItem> Scripts) BuildPairingWorkload(int count, int seed = 3)
    {
        var rng = new Random(seed);
        var videos = new List<MediaItem>(count);
        var scripts = new List<MediaItem>(count);
        int folders = Math.Max(4, count / 50);
        int id = 1;

        for (int i = 0; i < count; i++)
        {
            var baseName = BuildBaseName(rng) + "_" + i; // unique per index
            var folder = Path.Combine("/library", "f" + rng.Next(folders));
            videos.Add(new MediaItem
            {
                Id = id++, FullPath = Path.Combine(folder, baseName + ".mp4"),
                Filename = baseName, Extension = ".mp4", IsScript = false
            });

            int bucket = rng.Next(100);
            if (bucket < 55)
            {
                scripts.Add(Script(ref id, folder, baseName));
            }
            else if (bucket < 75)
            {
                scripts.Add(Script(ref id, folder, AddNoise(baseName, rng)));
            }
            else if (bucket < 85)
            {
                var otherFolder = Path.Combine("/library", "f" + (folders + rng.Next(folders)));
                scripts.Add(Script(ref id, otherFolder, AddNoise(baseName, rng)));
            }
            // else: no script
        }

        // scripts are loaded ordered by filename by the repository
        scripts.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));
        videos.Sort((a, b) => string.CompareOrdinal(a.Filename, b.Filename));
        return (videos, scripts);

        static MediaItem Script(ref int id, string folder, string name) => new()
        {
            Id = id++, FullPath = Path.Combine(folder, name + ".funscript"),
            Filename = name, Extension = ".funscript", IsScript = true
        };
    }
}
