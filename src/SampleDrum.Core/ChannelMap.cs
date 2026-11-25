namespace SampleDrum.Core;

/// <summary>
/// Converts between desk/USB channel numbers (1-based, as printed on the X32 / X-USB)
/// and API channel indices (0-based, as used by ASIO).
/// Example: USB 3 -> index 2, USB 1 -> index 0.
/// </summary>
public static class ChannelMap
{
    /// <summary>Number of channels on the X-USB card (32x32).</summary>
    public const int MaxChannels = 32;

    /// <summary>Desk channel (1..32) to 0-based API index. Throws when out of range.</summary>
    public static int DeskToApiIndex(int deskChannel)
    {
        if (deskChannel < 1 || deskChannel > MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(deskChannel), deskChannel,
                $"Canal da mesa deve estar entre 1 e {MaxChannels}.");
        return deskChannel - 1;
    }

    /// <summary>0-based API index (0..31) to desk channel (1..32). Throws when out of range.</summary>
    public static int ApiToDeskChannel(int apiIndex)
    {
        if (apiIndex < 0 || apiIndex >= MaxChannels)
            throw new ArgumentOutOfRangeException(nameof(apiIndex), apiIndex,
                $"Índice de API deve estar entre 0 e {MaxChannels - 1}.");
        return apiIndex + 1;
    }

    /// <summary>Non-throwing variant of <see cref="DeskToApiIndex"/>.</summary>
    public static bool TryDeskToApiIndex(int deskChannel, out int apiIndex)
    {
        apiIndex = deskChannel - 1;
        return deskChannel >= 1 && deskChannel <= MaxChannels;
    }

    /// <summary>Validates an API index against the number of channels the driver actually exposes.</summary>
    public static bool IsValidApiIndex(int apiIndex, int channelCount = MaxChannels)
        => apiIndex >= 0 && apiIndex < channelCount;
}
