using System.Text.Json;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

internal static class ForecastResidualsCommand
{
    public static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string>(), new HashSet<string> { "logs" });
        if (flags.HelpRequested)
        {
            Console.WriteLine("forecastresiduals -logs <logs.jsonl> (including rotated segments)");
            return Task.FromResult(0);
        }
        ct.ThrowIfCancellationRequested();
        if (flags.String("logs").Length == 0) throw new UsageException("Supply -logs <logs.jsonl>.");
        var evidence = ForecastEvidenceReader.Read(flags.String("logs"));
        Console.WriteLine(JsonSerializer.Serialize(ForecastResidualReport.Build(evidence)));
        return Task.FromResult(0);
    }
}


