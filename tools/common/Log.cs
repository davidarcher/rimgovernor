namespace RimGovernor.Tools.Common;

/// <summary>Shared console output for every subcommand.</summary>
public static class Log
{
    public static void Info(string line) => Console.Out.WriteLine(line);

    public static void Error(string tool, string message) => Console.Error.WriteLine($"{tool}: {message}");
}
