using BLESerialTerminal;

int checks = 0;

void Check(bool condition, string name)
{
    checks++;
    if (!condition)
        throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS  {name}");
}

try
{
    Check(AutoReconnectPolicy.MaxAttempts == 5, "auto reconnect attempt count is bounded");

    TimeSpan[] expected =
    {
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12)
    };

    for (int attempt = 1; attempt <= expected.Length; attempt++)
        Check(AutoReconnectPolicy.DelayForAttempt(attempt) == expected[attempt - 1], $"attempt {attempt} backoff is deterministic");

    for (int attempt = 2; attempt <= expected.Length; attempt++)
        Check(AutoReconnectPolicy.DelayForAttempt(attempt) > AutoReconnectPolicy.DelayForAttempt(attempt - 1), $"attempt {attempt} delay increases");

    bool zeroRejected = false;
    try { _ = AutoReconnectPolicy.DelayForAttempt(0); }
    catch (ArgumentOutOfRangeException) { zeroRejected = true; }
    Check(zeroRejected, "attempt zero rejected");

    bool overflowRejected = false;
    try { _ = AutoReconnectPolicy.DelayForAttempt(AutoReconnectPolicy.MaxAttempts + 1); }
    catch (ArgumentOutOfRangeException) { overflowRejected = true; }
    Check(overflowRejected, "attempt above maximum rejected");

    Check(AutoReconnectPolicy.DelayForAttempt(AutoReconnectPolicy.MaxAttempts) <= TimeSpan.FromSeconds(15),
        "maximum reconnect delay remains bounded");

    Console.WriteLine($"\nHARDENING TESTS: PASS ({checks} checks)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("HARDENING TESTS: FAIL");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
