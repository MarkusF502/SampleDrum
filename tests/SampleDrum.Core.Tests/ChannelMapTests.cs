using SampleDrum.Core;

namespace SampleDrum.Core.Tests;

public class ChannelMapTests
{
    [Theory]
    [InlineData(1, 0)]   // USB out/in 1 -> ASIO index 0 (default return)
    [InlineData(3, 2)]   // USB 3 -> ASIO index 2 (default trigger input)
    [InlineData(32, 31)]
    public void DeskToApi(int desk, int api)
    {
        Assert.Equal(api, ChannelMap.DeskToApiIndex(desk));
        Assert.Equal(desk, ChannelMap.ApiToDeskChannel(api));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    public void DeskOutOfRange_Rejected(int desk)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.DeskToApiIndex(desk));
        Assert.False(ChannelMap.TryDeskToApiIndex(desk, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public void ApiOutOfRange_Rejected(int api)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMap.ApiToDeskChannel(api));
        Assert.False(ChannelMap.IsValidApiIndex(api));
    }

    [Fact]
    public void IsValidApiIndex_RespectsDriverChannelCount()
    {
        Assert.True(ChannelMap.IsValidApiIndex(2, 8));
        Assert.False(ChannelMap.IsValidApiIndex(8, 8));
    }
}
