using YtAgent;

namespace Youtube;

internal readonly record struct YoutubeCommand(WinApi.ScanCodes ScanCode, int Repetitions)
{
    public static bool TryParse(ReadOnlySpan<byte> body, out YoutubeCommand command)
    {
        if (body.SequenceEqual("youtube: back6"u8))
        {
            command = new YoutubeCommand(WinApi.ScanCodes.J, 6);
            return true;
        }

        if (body.SequenceEqual("youtube: back"u8))
        {
            command = new YoutubeCommand(WinApi.ScanCodes.J, 1);
            return true;
        }

        if (body.SequenceEqual("youtube: playpause"u8))
        {
            command = new YoutubeCommand(WinApi.ScanCodes.K, 1);
            return true;
        }

        if (body.SequenceEqual("youtube: forward"u8))
        {
            command = new YoutubeCommand(WinApi.ScanCodes.L, 1);
            return true;
        }

        if (body.SequenceEqual("youtube: forward6"u8))
        {
            command = new YoutubeCommand(WinApi.ScanCodes.L, 6);
            return true;
        }

        command = default;
        return false;
    }
}
