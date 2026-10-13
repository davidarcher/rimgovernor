using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using RimGovernor.BrainSpike.Contracts;

namespace RimGovernor.BrainSpike.Brain;

/// <summary>
/// One brain per unload hazard. The "-clean" variants release what they take in Dispose;
/// the "-leak" variants deliberately do not, as negative controls proving the harness can
/// tell a blocked unload from a successful one.
/// </summary>
public sealed class BrainFactory : IBrainFactory
{
    public IBrain Create(string scenario, IBrainHost host) => scenario switch
    {
        "baseline" or "retained-type" or "private-contracts" => new PlainBrain(),
        "protobuf" => new ProtobufBrain(),
        "json-default" => new JsonBrain(null, useDefault: true),
        "json-own-options" => new JsonBrain(new JsonSerializerOptions(), useDefault: false),
        "json-unique-options" => new JsonBrain(new JsonSerializerOptions { MaxDepth = 37, PropertyNameCaseInsensitive = true }, useDefault: false),
        "json-host-options" => new JsonBrain(host.SharedJson, useDefault: false),
        "json-serialize-only" => new JsonSerializeOnlyBrain(),
        "json-poco" => new JsonPocoBrain(),
        "json-sourcegen" => new JsonSourceGenBrain(),
        "task-clean" => new TaskBrain(clean: true),
        "task-leak" => new TaskBrain(clean: false),
        "channel-clean" => new ChannelBrain(),
        "timer-clean" => new TimerBrain(clean: true),
        "timer-leak" => new TimerBrain(clean: false),
        "thread-clean" => new ThreadBrain(clean: true),
        "thread-leak" => new ThreadBrain(clean: false),
        "event-clean" => new EventBrain(host, clean: true),
        "event-leak" => new EventBrain(host, clean: false),
        _ => throw new ArgumentException($"unknown scenario '{scenario}'")
    };
}

internal abstract class BrainBase : IBrain
{
    public virtual StringValue Tick(StringValue frame) => new() { Value = "ok:" + frame.Value };

    public string Describe() =>
        $"{GetType().Name} in ALC '{AssemblyLoadContext.GetLoadContext(GetType().Assembly)?.Name}'";

    public virtual void Dispose()
    {
    }
}

internal sealed class PlainBrain : BrainBase
{
    // Statics and thread statics die with the context; they are state loss, not a blocker.
    private static int _statics;
    [ThreadStatic] private static int _threadStatic;

    public override StringValue Tick(StringValue frame)
    {
        _statics++;
        _threadStatic++;
        return base.Tick(frame);
    }
}

internal sealed class ProtobufBrain : BrainBase
{
    public override StringValue Tick(StringValue frame)
    {
        // Round trip through Any, JSON, binary and the descriptor registry using shared types.
        var any = Any.Pack(frame);
        var back = any.Unpack<StringValue>();
        var json = JsonFormatter.Default.Format(back);
        var parsed = JsonParser.Default.Parse<StringValue>(json);
        var fromBytes = StringValue.Parser.ParseFrom(parsed.ToByteArray());
        _ = StringValue.Descriptor.Name;
        return new StringValue { Value = "ok:" + fromBytes.Value };
    }
}

internal sealed record Plan(string Name, int[] Steps);

internal sealed class JsonBrain(JsonSerializerOptions? options, bool useDefault) : BrainBase
{
    public override StringValue Tick(StringValue frame)
    {
        var plan = new Plan(frame.Value, [1, 2, 3]);
        var json = useDefault ? JsonSerializer.Serialize(plan) : JsonSerializer.Serialize(plan, options);
        var back = useDefault
            ? JsonSerializer.Deserialize<Plan>(json)
            : JsonSerializer.Deserialize<Plan>(json, options);
        return new StringValue { Value = "ok:" + back!.Name };
    }
}

internal sealed class JsonSerializeOnlyBrain : BrainBase
{
    public override StringValue Tick(StringValue frame)
    {
        _ = JsonSerializer.Serialize(new Plan(frame.Value, [1]));
        return base.Tick(frame);
    }
}

internal sealed class PocoPlan
{
    public string Name { get; set; } = "";
}

internal sealed class JsonPocoBrain : BrainBase
{
    public override StringValue Tick(StringValue frame)
    {
        var json = JsonSerializer.Serialize(new PocoPlan { Name = frame.Value });
        return new StringValue { Value = "ok:" + JsonSerializer.Deserialize<PocoPlan>(json)!.Name };
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Plan))]
internal sealed partial class BrainJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

internal sealed class JsonSourceGenBrain : BrainBase
{
    public override StringValue Tick(StringValue frame)
    {
        var json = JsonSerializer.Serialize(new Plan(frame.Value, [1, 2, 3]), BrainJsonContext.Default.Plan);
        return new StringValue { Value = "ok:" + JsonSerializer.Deserialize(json, BrainJsonContext.Default.Plan)!.Name };
    }
}

internal sealed class TaskBrain : BrainBase
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly bool _clean;
    private int _beats;

    public TaskBrain(bool clean)
    {
        _clean = clean;
        var token = clean ? _cts.Token : CancellationToken.None;
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _beats);
                    await Task.Delay(5, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public override void Dispose()
    {
        if (!_clean)
            return;
        _cts.Cancel();
        _loop.Wait(2000);
    }
}

internal sealed class ChannelBrain : BrainBase
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    private readonly Task _reader;
    private int _seen;

    public ChannelBrain()
    {
        _reader = Task.Run(async () =>
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                Interlocked.Increment(ref _seen);
        });
    }

    public override StringValue Tick(StringValue frame)
    {
        _channel.Writer.TryWrite(1);
        return base.Tick(frame);
    }

    public override void Dispose()
    {
        _channel.Writer.Complete();
        _reader.Wait(2000);
    }
}

internal sealed class TimerBrain : BrainBase
{
    private readonly Timer _timer;
    private readonly bool _clean;
    private int _fires;

    public TimerBrain(bool clean)
    {
        _clean = clean;
        _timer = new Timer(_ => Interlocked.Increment(ref _fires), null, 5, 5);
    }

    public override void Dispose()
    {
        if (!_clean)
            return;
        using var done = new ManualResetEvent(false);
        _timer.Dispose(done);
        done.WaitOne(2000);
    }
}

internal sealed class ThreadBrain : BrainBase
{
    private readonly Thread _thread;
    private readonly bool _clean;
    private volatile bool _stop;
    private int _beats;

    public ThreadBrain(bool clean)
    {
        _clean = clean;
        _thread = new Thread(() =>
        {
            while (!_stop)
            {
                Interlocked.Increment(ref _beats);
                Thread.Sleep(5);
            }
        })
        { IsBackground = true, Name = "brain-spike" };
        _thread.Start();
    }

    public override void Dispose()
    {
        if (!_clean)
            return;
        _stop = true;
        _thread.Join(2000);
    }
}

internal sealed class EventBrain : BrainBase
{
    private readonly IBrainHost _host;
    private readonly bool _clean;
    private int _seen;

    public EventBrain(IBrainHost host, bool clean)
    {
        _host = host;
        _clean = clean;
        _host.Observation += OnObservation;
    }

    private void OnObservation(string _) => Interlocked.Increment(ref _seen);

    public override void Dispose()
    {
        if (_clean)
            _host.Observation -= OnObservation;
    }
}
