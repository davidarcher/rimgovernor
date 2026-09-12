using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;

namespace HomeBridge.BridgeTools
{
    internal static class ProtoBoundary
    {
        internal const int MaximumEnvelopeBytes = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool TryParse<T>(IRimBridgeContext ctx, string toolName, object request,
            MessageParser<T> parser, out T value, out Common.Failure failure) where T : IMessage<T>
        {
            value = default(T);
            string unavailable;
            var arguments = BridgeCommon.RawArguments(ctx, out unavailable);
            if (arguments == null)
            {
                failure = Fail(Common.FailureCode.Unavailable, "Original invocation arguments are unavailable.");
                return false;
            }
            foreach (var key in arguments.Keys)
            {
                if (key != "request" && key != "_rimBridgeTimeoutMs")
                {
                    failure = Fail(Common.FailureCode.InvalidRequest, "The sole caller argument must be request.");
                    return false;
                }
            }
            object raw;
            string json;
            if (!arguments.TryGetValue("request", out raw) || !TryString(raw, out json)
                || !(request is string) || !string.Equals(json, (string)request, StringComparison.Ordinal))
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request must be a ProtoJSON string.");
                return false;
            }
            try
            {
                if (Utf8.GetByteCount(json) > MaximumEnvelopeBytes)
                {
                    failure = Fail(Common.FailureCode.InvalidRequest, "request exceeds the one MiB control envelope limit.");
                    return false;
                }
                value = parser.ParseJson(json);
                failure = null;
                return true;
            }
            catch (InvalidProtocolBufferException)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request is not valid ProtoJSON for this method.");
                return false;
            }
            catch (InvalidJsonException)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request is not valid JSON.");
                return false;
            }
            catch (EncoderFallbackException)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "request must contain valid Unicode.");
                return false;
            }
        }

        private static bool TryString(object raw, out string value)
        {
            value = raw as string;
            if (value != null) return true;
            var token = raw as JValue;
            if (token == null || token.Type != JTokenType.String) return false;
            value = token.Value as string;
            return value != null;
        }

        // The SDK recognizes this concrete envelope; generated CLR properties are not its wire format.
        internal static string Format(IMessage reply, bool compact = false)
        {
            var payload = JsonFormatter.Default.Format(reply);
            if (!compact) return payload;
            // Keep official ProtoJSON values and field names; only omit formatting.
            // Identifier strings must never be interpreted as dates.
            using (var input = new StringReader(payload))
            using (var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None })
            using (var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(output) { Formatting = Formatting.None }) {
                writer.WriteToken(reader);
                writer.Flush();
                return output.ToString();
            }
        }

        internal static Dictionary<string, object> Encode(IMessage reply, bool compact = false)
        {
            var payload = Format(reply, compact);
            if (Utf8.GetByteCount(payload) > MaximumEnvelopeBytes)
                throw new InvalidOperationException("Reply exceeds the one MiB control envelope limit.");
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["payload"] = payload };
        }

        internal static Common.Failure Fail(Common.FailureCode code, string detail)
        {
            return new Common.Failure { Code = code, Detail = detail };
        }

        internal static bool IsIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) return false;
            try { return Utf8.GetByteCount(value) <= 256; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static bool ValidateIdentity(Common.Identity expected, Map map,
            out Common.ObservationContext context, out Common.Failure failure)
        {
            context = null;
            if (expected == null || !expected.HasColonyId || !expected.HasLoadToken || !expected.HasMapId
                || !IsIdentifier(expected.ColonyId) || !IsIdentifier(expected.LoadToken) || expected.MapId < 0)
            {
                failure = Fail(Common.FailureCode.InvalidRequest, "A complete valid colony, load and map identity is required.");
                return false;
            }
            Common.Unavailable unavailable;
            if (!TryReadContext(map, out context, out unavailable))
            {
                failure = Fail(Common.FailureCode.Unavailable, unavailable.Detail);
                return false;
            }
            if (!expected.Equals(context.Identity))
            {
                failure = Fail(Common.FailureCode.StaleIdentity, "The colony, load or map identity has changed.");
                failure.ObservedContext = context;
                return false;
            }
            failure = null;
            return true;
        }

        // Call only on the game thread. Reading never attaches or repairs game components.
        internal static bool TryReadContext(Map map, out Common.ObservationContext context,
            out Common.Unavailable unavailable)
        {
            context = null;
            if (Current.Game == null || map == null || !ReferenceEquals(map, Find.CurrentMap) || Find.TickManager == null)
            {
                unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NotLoaded, Detail = "No current colony map is loaded." };
                return false;
            }
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            if (identity == null || !IsIdentifier(identity.ColonyId) || !IsIdentifier(identity.LoadToken)
                || map.uniqueID < 0 || Find.TickManager.TicksGame < 0)
            {
                unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing,
                    Detail = "The native identity component is missing or invalid." };
                return false;
            }
            context = new Common.ObservationContext
            {
                Identity = new Common.Identity { ColonyId = identity.ColonyId, LoadToken = identity.LoadToken, MapId = map.uniqueID },
                Tick = Find.TickManager.TicksGame
            };
            NativeControlAuthority authority;
            if (NativeControlAuthority.TryGetForGame(Current.Game, out authority))
                context.NativeGeneration = authority.Status().Generation;
            unavailable = null;
            return true;
        }
    }
}
