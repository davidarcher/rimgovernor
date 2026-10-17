using System.Globalization;
using System.Text.Json;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The game-side reads the out-of-process acceptance harness cannot decode itself (#3117): the decoded colony facts,
/// the blight census, the first layout plan and map survey, and two catalog tables. Each read runs the in-process
/// observation layer over the published native facade (the same decode the brain's reviews use) and returns the
/// small JSON the harness's host seams parse. The mod registers one read-only tool per method; nothing here writes.
/// </summary>
public sealed class AcceptanceReads
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private readonly FacadeNativeClient _client;

    private AcceptanceReads(FacadeNativeClient client) => _client = client;

    /// <summary>Reads over <paramref name="facade"/>; <paramref name="catalogFactory"/> builds the definition catalog of a load (the mod's NativeCatalogFactory).</summary>
    public static AcceptanceReads Create(INativeFacade facade, Func<RimGovernor.Contracts.Wire.Observations.DefinitionCatalog, RimGovernor.Contracts.Wire.Common.Identity, DefinitionCatalog> catalogFactory)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(catalogFactory);
        return new AcceptanceReads(FacadeNativeClient.Create(facade, catalogFactory));
    }

    /// <summary>The sleeping temperature band and power coverage of the loaded colony (<c>null</c> members are unread facts).</summary>
    public async Task<string> ColonyFactsAsync(CancellationToken cancellationToken)
    {
        var (_, _, reading) = await ReadColonyAsync(cancellationToken).ConfigureAwait(false);
        var f = reading.Projection.Facts;
        return Write(w =>
        {
            Optional(w, "sleepingMin", f.SleepingMin.TryGetValue(out var min), min);
            Optional(w, "sleepingMax", f.SleepingMax.TryGetValue(out var max), max);
            Optional(w, "powerRequired", f.PowerRequired.TryGetValue(out var required), required);
            Optional(w, "powerHeadroom", f.PowerHeadroom.TryGetValue(out var headroom), headroom);
            Optional(w, "disabledConsumers", f.DisabledConsumers.TryGetValue(out var disabled), disabled);
        });
    }

    /// <summary>The blighted-plant census of the planning window and the tick it was read at (<c>unavailable</c> when the census is unread).</summary>
    public async Task<string> BlightAsync(CancellationToken cancellationToken)
    {
        var (_, _, reading) = await ReadColonyAsync(cancellationToken).ConfigureAwait(false);
        var projection = reading.Projection;
        return Write(w =>
        {
            w.WriteNumber("tick", projection.Identity.Tick.Value);
            if (!projection.Facts.Blight.TryGetValue(out var census))
            {
                w.WriteBoolean("unavailable", true);
                return;
            }

            w.WriteStartArray("plants");
            foreach (var plant in census)
            {
                w.WriteStartObject();
                w.WriteString("id", plant.ID);
                w.WriteString("def", plant.Definition);
                w.WriteNumber("x", plant.Cell.X);
                w.WriteNumber("z", plant.Cell.Z);
                w.WriteString("zone", plant.Zone);
                w.WriteBoolean("designated", plant.Designated);
                w.WriteBoolean("eligible", plant.Eligible);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        });
    }

    /// <summary>
    /// The first layout plan of the loaded game (the seed rooms and the outskirts: no ledger, no stock), the map bounds,
    /// the tick and colonist count, and, with <paramref name="cells"/>, the scored map survey.
    /// </summary>
    public async Task<string> StarterSiteAsync(bool cells, CancellationToken cancellationToken)
    {
        var (_, catalog, reading) = await ReadColonyAsync(cancellationToken).ConfigureAwait(false);
        var projection = reading.Projection;
        var survey = catalog.SurveyFromCells(projection.Cells, projection.Bounds) with
        {
            Cold = projection.ColdMap.TryGetValue(out var cold) && cold,
            Hot = projection.HotMap.TryGetValue(out var hot) && hot,
        };
        var facts = StationFactsBuilder.FirstPlanFacts(projection, survey, new Dictionary<Resource, long>(), PolicyFunctions.NewLedgerView(PolicyFunctions.LedgerViewNone, 0));
        var (fact, _) = PolicyFunctions.DeriveLayoutPlan(survey, facts, []);
        var planJson = fact.TryGetValue(out var plan) ? StoreJson.Serialize(plan) : null;
        projection.Facts.Colonists.TryGetValue(out var colonists);
        return Write(w =>
        {
            w.WriteNumber("tick", projection.Identity.Tick.Value);
            w.WriteNumber("colonists", colonists);
            w.WriteNumber("width", projection.Bounds.Width);
            w.WriteNumber("height", projection.Bounds.Height);
            if (planJson is not null)
            {
                w.WritePropertyName("plan");
                w.WriteRawValue(planJson);
            }

            if (cells)
            {
                WriteSurvey(w, survey);
            }
        });
    }

    /// <summary>
    /// Measures the brain's decode of one captured frame for the capture profile probe: the cell grid decode, the
    /// held tables and the whole-map planning window, each <paramref name="iterations"/> times on the calling thread.
    /// Returns (span, ms, allocated bytes) samples; nothing is kept.
    /// </summary>
    public static IReadOnlyList<(string Span, double Ms, long Bytes)> FrameDecodeProfile(RimGovernor.Contracts.Wire.Observations.BundleSnapshot frame, int iterations)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var samples = new List<(string, double, long)>();
        T Time<T>(string span, Func<T> body)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = body();
            samples.Add((span, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes));
            return result;
        }
        for (var i = 0; i < iterations; i++)
        {
            var view = Time("brain.gridDecode", () => FrameGridView.Of(frame));
            if (frame.Context is { } context)
            {
                Time("brain.heldTables", () => FrameHeld.Of(frame, context));
            }
            if (view.Grid is { } grid)
            {
                Time("brain.windowWholeMap", () => grid.Window(grid.Rect));
            }
        }
        return samples;
    }

    /// <summary>The Impressiveness room stat's score stages of the loaded game.</summary>
    public async Task<string> ImpressivenessAsync(CancellationToken cancellationToken)
    {
        var (_, catalog) = await ReadIdentityAndCatalogAsync(cancellationToken).ConfigureAwait(false);
        var levels = catalog.ImpressivenessLevels();
        return Write(w =>
        {
            w.WriteNumber("dull", levels.Dull);
            w.WriteNumber("mediocre", levels.Mediocre);
            w.WriteNumber("decent", levels.Decent);
            w.WriteNumber("slightlyImpressive", levels.SlightlyImpressive);
            w.WriteStartArray("stages");
            foreach (var stage in levels.Stages)
            {
                w.WriteNumberValue(stage);
            }

            w.WriteEndArray();
        });
    }

    /// <summary>One row per animal race of the loaded game's catalog: wildness, body size, minimum handling skill and the milk product.</summary>
    public async Task<string> AnimalRacesAsync(CancellationToken cancellationToken)
    {
        var (_, catalog) = await ReadIdentityAndCatalogAsync(cancellationToken).ConfigureAwait(false);
        var races = catalog.AnimalRaces().Races;
        return Write(w =>
        {
            w.WriteStartArray("races");
            foreach (var (def, race) in races.OrderBy(r => r.Key.Value, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("def", def.Value);
                Optional(w, "wildness", race.Wildness.TryGetValue(out var wildness), wildness);
                Optional(w, "bodySize", race.BodySize.TryGetValue(out var size), size);
                Optional(w, "minimumHandlingSkill", race.MinimumHandlingSkill.TryGetValue(out var skill), skill);
                foreach (var product in race.Products)
                {
                    if (product.Kind == "milk" && product.Amount.TryGetValue(out var amount) && product.IntervalDays.TryGetValue(out var interval) && interval > 0)
                    {
                        w.WriteStartObject("milk");
                        w.WriteNumber("amount", amount);
                        w.WriteNumber("intervalDays", interval);
                        w.WriteEndObject();
                        break;
                    }
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        });
    }

    private async Task<(PbCommon.Identity Identity, DefinitionCatalog Catalog, ColonyReading Reading)> ReadColonyAsync(CancellationToken cancellationToken)
    {
        var expected = await ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
        var identity = IdentityOf(expected);
        var catalog = await _client.DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        // The colony read fills its planning window straight from the native read (no scheduler step attaches one here).
        using var window = ClockPlanning.StandaloneWindow(_client);
        var reading = await ObservationPackage.ObserveColony(_client, SystemClock.Instance, expected, MaxAge, true, cancellationToken).ConfigureAwait(false);
        return (identity, catalog, reading);
    }

    private async Task<(PbCommon.Identity Identity, DefinitionCatalog Catalog)> ReadIdentityAndCatalogAsync(CancellationToken cancellationToken)
    {
        var identity = IdentityOf(await ReadIdentityAsync(cancellationToken).ConfigureAwait(false));
        var catalog = await _client.DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        return (identity, catalog);
    }

    private async Task<RimGovernor.Brain.Observation.Identity> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        var (reply, _) = await ((IColonySource)_client).IdentityAsync(cancellationToken).ConfigureAwait(false);
        return ObservationPackage.DecodeIdentity(reply);
    }

    private static PbCommon.Identity IdentityOf(RimGovernor.Brain.Observation.Identity expected) =>
        new() { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };

    // Cells are packed [x, z, flags, footing, fertility, terrain index] to keep a 250x250 map near two megabytes.
    private static void WriteSurvey(Utf8JsonWriter w, MapSurvey survey)
    {
        var terrains = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        w.WriteStartObject("survey");
        w.WriteBoolean("cold", survey.Cold);
        w.WriteBoolean("hot", survey.Hot);
        w.WriteStartArray("cells");
        foreach (var c in survey.Cells)
        {
            if (!index.TryGetValue(c.Terrain, out var terrain))
            {
                terrain = terrains.Count;
                index[c.Terrain] = terrain;
                terrains.Add(c.Terrain);
            }

            var flags = (c.Walkable ? 1 : 0) | (c.Rock ? 2 : 0) | (c.Built ? 4 : 0) | (c.Bridgeable ? 8 : 0) | (c.Dries ? 16 : 0)
                | (c.Hazard ? 32 : 0) | (c.ThickRoof ? 64 : 0) | (c.Ore ? 128 : 0) | (c.Tree ? 256 : 0) | (c.Prop ? 512 : 0) | (c.Ruin ? 1024 : 0);
            w.WriteStartArray();
            w.WriteNumberValue(c.Cell.X);
            w.WriteNumberValue(c.Cell.Z);
            w.WriteNumberValue(flags);
            w.WriteNumberValue((int)c.Footing);
            w.WriteNumberValue(c.Fertility);
            w.WriteNumberValue(terrain);
            w.WriteEndArray();
        }

        w.WriteEndArray();
        w.WriteStartArray("terrains");
        foreach (var t in terrains)
        {
            w.WriteStringValue(t);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void Optional(Utf8JsonWriter w, string name, bool known, double value)
    {
        if (known)
        {
            w.WriteNumber(name, value);
        }
    }

    private static void Optional(Utf8JsonWriter w, string name, bool known, bool value)
    {
        if (known)
        {
            w.WriteBoolean(name, value);
        }
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
