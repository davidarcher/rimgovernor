using System.Net;
using System.Text;
using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.HttpChannel;
using Xunit;
using PbCommon = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3761: a facade read that throws crosses <c>POST /native/&lt;op&gt;</c> as an HTTP status with a JSON error body
/// (<see cref="NativeRoutes"/>), and <see cref="HttpNativeTransport"/> throws the same exception back with its code
/// and detail.
/// </summary>
public sealed class NativeRouteFaultTests
{
    private const string Tool = "clock_read_status";

    /// <summary>An op table whose one op throws the given exception.</summary>
    private sealed class Throwing(Exception error) : INativeOpInvoker
    {
        public IReadOnlyCollection<string> Names { get; } = [Tool];

        public Task<string> InvokeAsync(string op, string body, CancellationToken cancellationToken) => Task.FromException<string>(error);
    }

    private static async Task<(int Status, string Body)> SendAsync(Exception error)
    {
        var routes = new RouteTable();
        using var registered = NativeRoutes.Register(routes, new Throwing(error));
        var bytes = Encoding.UTF8.GetBytes("{}");
        var head = new RequestHead
        {
            Method = "POST",
            Target = NativeRoutes.Prefix + Tool,
            Host = "127.0.0.1:1",
            ContentType = "application/json",
            ContentLength = bytes.Length,
        };
        var response = await new Dispatcher(routes, 1 << 20).DispatchAsync(head, new Dictionary<string, string>(), (_, _) => Task.FromResult(bytes), CancellationToken.None);
        return (response.Status, Encoding.UTF8.GetString(response.Body));
    }

    private static Exception RoundTrip(int status, string body) =>
        Assert.ThrowsAny<Exception>(() => HttpNativeTransport.Interpret(Tool, (HttpStatusCode)status, body, requestForm: true));

    [Fact]
    public async Task ARefusalIsA422AndComesBackAsTheSameFailure()
    {
        var (status, body) = await SendAsync(NativeFaults.Refused(new PbCommon.Failure { RefusalClass = RimGovernor.Contracts.Wire.Common.RefusalClass.Unclassified, Code = PbCommon.FailureCode.StaleIdentity, Detail = "the colony changed" }));

        Assert.Equal(NativeRoutes.RefusedStatus, status);
        var back = Assert.IsType<NativeFailureException>(RoundTrip(status, body));
        Assert.Equal(PbCommon.FailureCode.StaleIdentity, back.Value.Code);
        Assert.Equal("the colony changed", back.Value.Detail);
    }

    [Fact]
    public async Task AnUnavailableGameIsA503AndComesBackWithItsReason()
    {
        var (status, body) = await SendAsync(NativeFaults.Unavailable(new PbCommon.Unavailable { Reason = PbCommon.UnavailableReason.ReadFailed, Detail = "the map is generating" }));

        Assert.Equal(NativeRoutes.UnavailableStatus, status);
        var back = Assert.IsType<NativeUnavailableException>(RoundTrip(status, body));
        Assert.Equal(PbCommon.UnavailableReason.ReadFailed, back.Value.Reason);
        Assert.Equal("the map is generating", back.Value.Detail);
    }

    [Fact]
    public async Task NoLoadedGameIsA503AndComesBackAsNotLoaded()
    {
        var (status, body) = await SendAsync(NativeFaults.Unavailable(new PbCommon.Unavailable { Reason = PbCommon.UnavailableReason.NotLoaded, Detail = "No game is loaded." }));

        Assert.Equal(NativeRoutes.UnavailableStatus, status);
        var back = Assert.IsType<NotLoadedException>(RoundTrip(status, body));
        Assert.Contains("No game is loaded.", back.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyOtherFailureStaysTheChannelsOwnRefusal()
    {
        var (status, body) = await SendAsync(new InvalidOperationException("boom"));

        Assert.Equal(500, status);
        Assert.IsType<NativeRefusedException>(RoundTrip(status, body));
    }
}
