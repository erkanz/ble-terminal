namespace BLESerialTerminal;

internal static class AutoReconnectPolicy
{
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12)
    };

    public static int MaxAttempts => Delays.Length;

    public static TimeSpan DelayForAttempt(int attemptNumber)
    {
        if (attemptNumber < 1 || attemptNumber > Delays.Length)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber));
        return Delays[attemptNumber - 1];
    }
}
