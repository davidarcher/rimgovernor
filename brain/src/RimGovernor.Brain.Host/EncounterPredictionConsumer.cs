using System.Security.Cryptography;
using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

/// <summary>Runtime consumer fence independent of the predictor implementation. No stale reply becomes an estimate.</summary>
public sealed class EncounterPredictionConsumer(IEncounterPredictor predictor, TimeSpan timeout, IReadOnlyList<EncounterQualification>? acceptedQualifications = null)
{
    private readonly TimeSpan _timeout = CheckTimeout(timeout);
    private readonly EncounterQualification[] _accepted = acceptedQualifications?.Select(q => q with { CoveredRequestFingerprints = q.CoveredRequestFingerprints.ToArray() }).ToArray() ?? [];
    private static readonly JsonSerializerOptions FingerprintJson = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public async Task<EncounterPrediction> PredictAsync(EncounterRequest request, Func<EncounterRequest> current, CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(request);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            var reply = await predictor.PredictAsync(request, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            if (fingerprint != Fingerprint(current()) || fingerprint != reply.RequestFingerprint)
                return Refused(EncounterStatus.Stale, fingerprint, "scenario_changed");
            if (!Enum.IsDefined(reply.Status) || reply.Status != EncounterStatus.Evidence && (reply.Samples.Count != 0 || reply.Qualification is not null))
                return Refused(EncounterStatus.Malformed, fingerprint, "invalid_result_status");
            if (reply.Status == EncounterStatus.Evidence && !ValidSamples(request, reply))
                return Refused(EncounterStatus.Malformed, fingerprint, "invalid_sample_payload");
            if (reply.Status == EncounterStatus.Evidence)
            {
                // Qualification belongs to the reviewing consumer, never to the provider's self-assertion.
                var approved = _accepted.FirstOrDefault(q => q.GameBuild == request.Identity.GameBuild &&
                    q.Mods == request.Identity.Mods && q.Tactic == request.Identity.Tactic &&
                    q.EngineFingerprint == reply.EngineFingerprint && q.SupportedDomain.Length != 0 &&
                    q.CriteriaRevision.Length != 0 && q.ReportReference.Length != 0 &&
                    q.CoveredRequestFingerprints.Contains(fingerprint, StringComparer.Ordinal));
                return approved is not null ? reply with { Qualification = approved } :
                    reply.Qualification is null ? reply : reply with { Qualification = null, Reason = "qualification_not_accepted_for_scenario" };
            }
            return reply;
        }
        catch (OperationCanceledException)
        {
            return Refused(cancellationToken.IsCancellationRequested ? EncounterStatus.Cancelled : EncounterStatus.TimedOut, fingerprint, "request_cancelled");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException)
        {
            return Refused(EncounterStatus.Unavailable, fingerprint, e.GetType().Name);
        }
    }

    private static bool ValidSamples(EncounterRequest request, EncounterPrediction reply) =>
        reply.Samples.Select(s => s.Seed).SequenceEqual(request.Seeds) && reply.Samples.All(s =>
            s.DurationTicks >= 0 && s.DurationTicks <= request.HorizonTicks &&
            s.HorizonReached == (s.DurationTicks == request.HorizonTicks) &&
            s.Actors.Select(a => a.Id).Order(StringComparer.Ordinal).SequenceEqual(request.Actors.Select(a => a.Id).Order(StringComparer.Ordinal)) &&
            s.Actors.All(a => ValidActor(request.Actors.Single(p => p.Id == a.Id), a, s.DurationTicks)));

    private static bool ValidActor(EncounterActor input, EncounterActorOutcome a, int duration) =>
        double.IsFinite(a.HealthFraction) && a.HealthFraction is >= 0 and <= 1 &&
        double.IsFinite(a.BleedRate) && a.BleedRate >= 0 &&
        a.Injuries.All(i => double.IsFinite(i.Severity) && i.Severity >= 0 && double.IsFinite(i.BleedRate) && i.BleedRate >= 0) &&
        (a.Products is not { } products || (a.Dead && double.IsFinite(products.Meat) && products.Meat >= 0 && double.IsFinite(products.Leather) && products.Leather >= 0)) &&
        ValidTick(a.FirstAttackTick, duration) && ValidTick(a.LastShotTick, duration) && ValidTick(a.DepartureTick, duration) && ValidTick(a.WithdrawalTick, duration) &&
        (a.LastShotTick is null || a.FirstAttackTick is not null && a.LastShotTick >= a.FirstAttackTick) &&
        (a.WithdrawalTick is null || !a.Dead && (a.LastShotTick is null || a.LastShotTick < a.WithdrawalTick)) &&
        (a.WithdrawalTick is null || a.FirstAttackTick is null || a.FirstAttackTick < a.WithdrawalTick) &&
        (a.DepartureTick is null || input.Departure is { } d && d.Tick == a.DepartureTick) &&
        (input.Departure is not { } departure || (a.LastShotTick is null || a.LastShotTick < departure.Tick) && (a.FirstAttackTick is null || a.FirstAttackTick < departure.Tick));

    private static bool ValidTick(int? tick, int duration) => tick is null || tick >= 0 && tick <= duration;

    private static TimeSpan CheckTimeout(TimeSpan value) => value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(2)
        ? value : throw new ArgumentOutOfRangeException(nameof(value), "consumer timeout must be positive and at most two minutes");

    public static string Fingerprint(EncounterRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, FingerprintJson)));
    internal static EncounterPrediction Refused(EncounterStatus status, string fingerprint, string reason) => new()
    {
        Status = status, RequestFingerprint = fingerprint, Reason = reason,
    };
}
