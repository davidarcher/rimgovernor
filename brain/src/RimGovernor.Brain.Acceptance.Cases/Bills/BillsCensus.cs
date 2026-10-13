using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>bills/census</c> (Go <c>census.go</c>): the typed bench census behind observations_read_bills and
/// observations_read_recipes on a loaded save: every player bench appears once with a CAS token, each bill names its
/// recipe, the census token agrees with the colony-facts production token for the same bench, and each bench's recipe
/// list carries the availability of every recipe (what a recipe is, costs and needs is read from the definition
/// catalog). A fixture build's test/routine_production_prepare seeds a fueled campfire with a food bill so a bench-less
/// save still exercises the census.
/// </summary>
internal static class BillsCensus
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "bills/census",
        Scope = "Typed bench/bill census and per-bench recipe catalog; read-only, no bill changes or gameplay orders.",
        Start = new FixtureStart { Op = "test/routine_production_prepare", On = new LabStart() },
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        foreach (var required in new[] { "rimgovernor/observations_read_bills", "rimgovernor/observations_read_recipes" })
        {
            if (!Dyn.Contains(s.Names, required))
            {
                throw new AcceptanceException($"missing {required} in discovery");
            }
        }

        var identityReply = await h.WireAsync("identity-before", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var before = Harness.Outcome(identityReply, "loaded").Value;
        var beforeContext = before["context"].AsMap();
        var identity = beforeContext?["identity"].AsMap();
        JsonObject Scope() => new() { ["expectedIdentity"] = identity?.DeepClone() };

        var billsReply = await h.WireAsync("typed-bills", "observations_read_bills", new JsonObject { ["scope"] = Scope() }, cancellationToken).ConfigureAwait(false);
        var observed = Harness.Outcome(billsReply, "observed").Value;
        if (!Dyn.DeepEqual(observed["context"], beforeContext))
        {
            throw new AcceptanceException("bills context drifted mid-run");
        }

        var benches = observed["benches"].AsSlice();
        if (benches.Count == 0)
        {
            throw new AcceptanceException("save has no player bench; the census assertion would be vacuous");
        }

        report["bench_count"] = benches.Count;
        var facts = await h.WireAsync("colony-facts", "observations_read_colony_facts", new JsonObject { ["scope"] = Scope() }, cancellationToken).ConfigureAwait(false);
        var factsObserved = Harness.Outcome(facts, "observed").Value;
        var productionTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in new[] { "cooking", "butchering" })
        {
            foreach (var raw in factsObserved[section].AsSlice())
            {
                var row = raw.AsMap();
                productionTokens[row?["bench"].RefId() ?? ""] = row?["benchSnapshot"].AsMap()?["token"].AsString() ?? "";
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokenAgreements = 0;
        var recipeRows = 0;
        foreach (var raw in benches)
        {
            var row = raw.AsMap();
            var id = row?["bench"].AsMap()?["id"].AsString() ?? "";
            if (id.Length == 0 || !seen.Add(id))
            {
                throw new AcceptanceException($"typed bench \"{id}\" missing an id or duplicated");
            }

            var snapshot = row!["snapshot"].AsMap();
            var token = snapshot?["token"].AsString() ?? "";
            if (token.Length == 0 || snapshot!["entityId"].AsString() != id)
            {
                throw new AcceptanceException($"bench {id}: missing CAS token or mismatched entity id");
            }

            if (productionTokens.TryGetValue(id, out var expected))
            {
                if (expected != token)
                {
                    throw new AcceptanceException($"bench {id}: census token {token} disagrees with colony-facts token {expected}");
                }

                tokenAgreements++;
            }

            var bills = row["bills"].AsSlice();
            for (var index = 0; index < bills.Count; index++)
            {
                var bill = bills[index].AsMap();
                if (bill?["recipe"].AsMap()?["defName"].AsString().Length is null or 0)
                {
                    throw new AcceptanceException($"bench {id} bill {index}: recipe def name missing");
                }

                if (!bill.ContainsKey("suspended"))
                {
                    throw new AcceptanceException($"bench {id} bill {index}: suspended fact missing");
                }
            }

            var recipesReply = await h.WireAsync(
                "typed-recipes-" + id,
                "observations_read_recipes",
                new JsonObject { ["scope"] = Scope(), ["benchId"] = id },
                cancellationToken).ConfigureAwait(false);
            JsonObject catalog;
            try
            {
                catalog = Harness.Outcome(recipesReply, "observed").Value;
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"bench {id} recipes: {e.Message}", e);
            }

            var catalogSnapshot = catalog["snapshot"].AsMap();
            if (catalogSnapshot?["token"].AsString() != token || catalogSnapshot["entityId"].AsString() != id)
            {
                throw new AcceptanceException($"bench {id}: recipe catalog snapshot disagrees with the bill census");
            }

            var recipes = catalog["recipes"].AsSlice();
            if (recipes.Count == 0)
            {
                throw new AcceptanceException($"bench {id}: empty recipe catalog");
            }

            foreach (var rawRecipe in recipes)
            {
                var recipe = rawRecipe.AsMap();
                var definition = recipe?["recipe"].AsMap();
                if (definition?["defName"].AsString().Length is null or 0)
                {
                    throw new AcceptanceException($"bench {id}: recipe without a def name");
                }

                foreach (var field in new[] { "availableNow", "availableOnBench" })
                {
                    if (!recipe!.ContainsKey(field))
                    {
                        throw new AcceptanceException($"bench {id} recipe {definition["defName"].ToJson()}: {field} missing");
                    }
                }

                recipeRows++;
            }
        }

        report["token_agreements_with_colony_facts"] = tokenAgreements;
        report["recipe_rows"] = recipeRows;
        foreach (var (label, request) in new[]
        {
            ("blank-bench", new JsonObject { ["scope"] = Scope(), ["benchId"] = " " }),
        })
        {
            var reply = await h.WireAsync(label, "observations_read_bills", request, cancellationToken).ConfigureAwait(false);
            var code = reply["failure"].AsMap()?["code"].AsString();
            if (code != "FAILURE_CODE_INVALID_REQUEST")
            {
                throw new AcceptanceException($"{label}: expected FAILURE_CODE_INVALID_REQUEST, got \"{code}\"");
            }
        }

        var unknownReply = await h.WireAsync(
            "unknown-bench-recipes",
            "observations_read_recipes",
            new JsonObject { ["scope"] = Scope(), ["benchId"] = "Thing_NoSuchBench0" },
            cancellationToken).ConfigureAwait(false);
        var unknownCode = unknownReply["failure"].AsMap()?["code"].AsString();
        if (unknownCode != "FAILURE_CODE_NOT_FOUND")
        {
            throw new AcceptanceException($"unknown bench: expected FAILURE_CODE_NOT_FOUND, got \"{unknownCode}\"");
        }

        var identityAfterReply = await h.WireAsync("identity-after", "lifecycle_read_identity", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var after = Harness.Outcome(identityAfterReply, "loaded").Value;
        if (!Dyn.DeepEqual(after["context"], beforeContext))
        {
            throw new AcceptanceException("identity/tick changed during a read-only pass");
        }
    }
}
