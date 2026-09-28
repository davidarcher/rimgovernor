#nullable enable
using System;

internal static class NativeAttemptLedgerProbe
{
    internal static void Invoke()
    {
        var checks = ClockLedgerChecks.Run();
        Console.WriteLine("Native attempt ledger passed "+checks+" checks; pure production source, no native dispatch.");
    }
}
