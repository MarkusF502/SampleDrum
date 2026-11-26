using SampleDrum.Core.Kit;

namespace SampleDrum.Core.Tests;

public class RoutingTests
{
    [Fact]
    public void ChurchProfile_KickRunnable_OthersPending_NoErrors()
    {
        var v = RoutingValidator.Validate(RoutingProfiles.Church);
        Assert.False(v.HasErrors);
        Assert.Equal(new[] { DrumPiece.Kick }, v.RunnablePieces);
        Assert.Equal(3, v.Issues.Count(i => i.Kind == RoutingIssueKind.Pending));
        Assert.All(v.Issues, i => Assert.False(i.IsError));
        Assert.Contains(v.Issues, i => i.Piece == DrumPiece.Snare && i.Message.Contains("pendentes"));

        var r = v.Resolve();
        Assert.Equal(2, r.InputIndex(DrumPiece.Kick));     // USB 3 -> ASIO input 2
        Assert.Equal(0, r.OutputIndex(DrumPiece.Kick));    // USB 1 -> ASIO output 0
        Assert.Null(r.InputIndex(DrumPiece.Snare));
        Assert.Null(r.OutputIndex(DrumPiece.FloorTom));
        Assert.Equal(new[] { DrumPiece.Kick }, r.RoutedPieces);
    }

    [Fact]
    public void ChurchProfile_ReservesObsInputs()
    {
        Assert.Contains(RoutingProfiles.Church.Reserved, r => r.Direction == ChannelDirection.Input && r.DeskChannel == 1);
        Assert.Contains(RoutingProfiles.Church.Reserved, r => r.Direction == ChannelDirection.Input && r.DeskChannel == 2);
        Assert.DoesNotContain(RoutingProfiles.Church.Reserved, r => r.Direction == ChannelDirection.Output);
    }

    [Fact]
    public void SimulationProfile_FourPiecesRunnable()
    {
        var v = RoutingValidator.Validate(RoutingProfiles.Simulation);
        Assert.Empty(v.Issues);
        Assert.Equal(DrumPieces.All, v.RunnablePieces);
        var r = v.Resolve();
        foreach (var p in DrumPieces.All)
        {
            Assert.Equal(RoutingProfiles.Simulation[p].InputDeskChannel - 1, r.InputIndex(p));
            Assert.Equal(RoutingProfiles.Simulation[p].OutputDeskChannel - 1, r.OutputIndex(p));
        }
    }

    [Fact]
    public void HalfPendingRoute_IsPending_NotAnError()
    {
        var prof = RoutingProfiles.Church.WithRoute(DrumPiece.Snare, new PieceRoute(4, null));
        var v = RoutingValidator.Validate(prof);
        Assert.False(v.HasErrors);
        Assert.False(v.IsRunnable(DrumPiece.Snare));
        Assert.Contains(v.IssuesOf(DrumPiece.Snare), i => i.Kind == RoutingIssueKind.Pending && i.Message.Contains("saída pendente"));
    }

    [Fact]
    public void DuplicateInput_BlocksBothPieces()
    {
        var prof = RoutingProfiles.Church.WithRoute(DrumPiece.Snare, new PieceRoute(3, 2));
        var v = RoutingValidator.Validate(prof);
        Assert.True(v.HasErrors);
        Assert.False(v.IsRunnable(DrumPiece.Kick));
        Assert.False(v.IsRunnable(DrumPiece.Snare));
        var dup = v.Issues.Where(i => i.Kind == RoutingIssueKind.DuplicateInput).ToList();
        Assert.Equal(2, dup.Count);
        Assert.All(dup, i => Assert.Equal(ChannelDirection.Input, i.Direction));
        Assert.Contains("Bumbo e Caixa leem a mesma entrada USB 3", dup[0].Message);
    }

    [Fact]
    public void DuplicateOutput_BlocksBothPieces()
    {
        var prof = RoutingProfiles.Simulation.WithRoute(DrumPiece.FloorTom, new PieceRoute(6, 2));
        var v = RoutingValidator.Validate(prof);
        Assert.True(v.HasErrors);
        Assert.Equal(new[] { DrumPiece.Kick, DrumPiece.Tom }, v.RunnablePieces);
        Assert.Equal(2, v.Issues.Count(i => i.Kind == RoutingIssueKind.DuplicateOutput));
        Assert.Contains(v.Issues, i => i.Message.Contains("Caixa e Surdo escrevem na mesma saída USB 2"));
    }

    [Fact]
    public void SameNumberAsInputAndOutput_IsNotAConflict()
    {
        // Kick reads input USB 3; Snare writes output USB 3 and Tom reads 4/writes 4: different physical paths.
        var prof = RoutingProfiles.Church
            .WithRoute(DrumPiece.Snare, new PieceRoute(5, 3))
            .WithRoute(DrumPiece.Tom, new PieceRoute(4, 4));
        var v = RoutingValidator.Validate(prof);
        Assert.False(v.HasErrors);
        Assert.Equal(new[] { DrumPiece.Kick, DrumPiece.Snare, DrumPiece.Tom }, v.RunnablePieces);
    }

    [Fact]
    public void ReservedObsInput_RejectedButSameNumberAsOutputAllowed()
    {
        var bad = RoutingProfiles.Church.WithRoute(DrumPiece.Snare, new PieceRoute(2, 3));
        var v = RoutingValidator.Validate(bad);
        Assert.True(v.HasErrors);
        var issue = Assert.Single(v.Issues, i => i.Kind == RoutingIssueKind.Reserved);
        Assert.Equal(DrumPiece.Snare, issue.Piece);
        Assert.Contains("OBS MixBus 13/14", issue.Message);
        Assert.True(v.IsRunnable(DrumPiece.Kick));                     // the other pieces are not affected

        var ok = RoutingProfiles.Church.WithRoute(DrumPiece.Snare, new PieceRoute(4, 2)); // output USB 2 is fine
        Assert.False(RoutingValidator.Validate(ok).HasErrors);
    }

    [Theory]
    [InlineData(33, 1, 32, 32)]
    [InlineData(0, 1, 32, 32)]
    [InlineData(3, 9, 32, 8)]
    [InlineData(9, 1, 8, 32)]
    public void OutOfRange_Rejected(int input, int output, int inCount, int outCount)
    {
        var prof = RoutingProfiles.Church.WithRoute(DrumPiece.Kick, new PieceRoute(input, output));
        var v = RoutingValidator.Validate(prof, inCount, outCount);
        Assert.True(v.HasErrors);
        Assert.False(v.IsRunnable(DrumPiece.Kick));
        var issue = Assert.Single(v.Issues, i => i.Kind == RoutingIssueKind.OutOfRange);
        Assert.Contains("fora da faixa", issue.Message);
    }

    [Fact]
    public void ResolvedRouting_RejectsHalfRoutedPiece()
    {
        Assert.Throws<ArgumentException>(() => new ResolvedRouting("x", new int?[] { 2, null, null, null }, new int?[4]));
    }
}
