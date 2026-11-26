using SampleDrum.Core;
using SampleDrum.Core.Kit;
using static SampleDrum.Core.Tests.TestUtil;

namespace SampleDrum.Core.Tests;

public sealed class PanelLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sd-panel-" + Guid.NewGuid().ToString("N"));
    public PanelLibraryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("../outside.wav")]
    [InlineData("a/../../outside.wav")]
    [InlineData("a\\..\\x.wav")]
    [InlineData("/tmp/a.wav")]
    [InlineData("\\server\\share.wav")]
    [InlineData("C:\\a.wav")]
    [InlineData("C:a.wav")]
    [InlineData("a.wav:stream")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/./b.wav")]
    [InlineData("a//b.wav")]
    [InlineData("a/.. /b.wav")]
    [InlineData("a./b.wav")]
    [InlineData("NUL.wav")]
    [InlineData("COM1.wav")]
    [InlineData("a\0b")]
    public void RemoteIds_RejectUnsafePaths(string id)
    {
        Assert.False(new SampleLibrary(_dir).TryResolveInLibrary(id, out var full));
        Assert.Equal("", full);
    }

    [Fact]
    public void List_HasRelativePortableIds_AndSafeMissingPathsResolve()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "caixas"));
        string path = Path.Combine(_dir, "caixas", "seco.wav");
        WavFile.WriteMono(path, [0.1f, 0.2f], Fs);
        var library = new SampleLibrary(_dir);
        var sample = Assert.Single(library.List());
        Assert.Equal("caixas/seco.wav", sample.Id);
        Assert.True(library.TryResolveInLibrary(sample.Id, out var resolved));
        Assert.Equal(path, resolved);
        Assert.True(library.TryResolveInLibrary("caixas\\novo.wav", out resolved));
        Assert.Equal(Path.Combine(_dir, "caixas", "novo.wav"), resolved);
        Assert.Equal(path, library.ResolvePath(path));
    }

    [Fact]
    public void ReparsePointBelowRoot_IsRejected()
    {
        string link = Path.Combine(_dir, "link");
        // Directory junctions require no developer mode/admin on Windows.
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{Path.GetTempPath().TrimEnd('\\')}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, Path.GetTempPath());
        try { Assert.False(new SampleLibrary(_dir).TryResolveInLibrary("link/escape.wav", out _)); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ErrorsHaveStableCodes_AndPresetIdentityUsesFileName()
    {
        var library = new SampleLibrary(_dir);
        async Task<PieceLoadError> Error(string name) => Assert.Single((await library.PrepareAsync(new KitPreset
        { Pieces = new Dictionary<DrumPiece, PiecePreset> { [DrumPiece.Kick] = new(name) } }, Fs)).Errors);
        Assert.Equal(PieceLoadErrorCode.FileNotFound, (await Error("missing.wav")).Code);
        File.WriteAllText(Path.Combine(_dir, "invalid.wav"), "invalid wav");
        Assert.Equal(PieceLoadErrorCode.UnsupportedFormat, (await Error("invalid.wav")).Code);
        WavFile.WriteMono(Path.Combine(_dir, "long.wav"), new float[Fs * 21], Fs);
        Assert.Equal(PieceLoadErrorCode.TooLong, (await Error("long.wav")).Code);
        Assert.Equal(PieceLoadErrorCode.IoError, (await Error("bad\0path")).Code);
        var preset = new KitPreset { Name = "Mesmo nome", Id = "untrusted-json-id" };
        preset.Save(Path.Combine(_dir, "first.json"));
        preset.Save(Path.Combine(_dir, "second.json"));
        var all = KitPreset.LoadFolder(_dir, out var errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "first", "second" }, all.Select(p => p.Id));
        Assert.All(all, p => Assert.Equal("Mesmo nome", p.Name));
        var prepared = (await library.PrepareAsync(all[1], Fs)).Kit!;
        Assert.Equal("second", prepared.PresetId);
        Assert.Equal("second", prepared.SamplesOnly().WithPiece(PreparedPiece.Empty(DrumPiece.Kick)).PresetId);
    }
}
