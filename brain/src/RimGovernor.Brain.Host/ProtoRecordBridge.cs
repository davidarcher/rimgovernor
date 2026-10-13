// TEMPORARY (#3100 increment 2 only): INativeFacade still speaks the generated protobuf messages until increment 3
// moves it onto the records. The host and everything under it already speak the records, so this converts a
// request to its protobuf twin and a reply back, by reflection over the descriptors. Deleted with the facade move.
using System.Collections;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;

namespace RimGovernor.Brain.Host;

internal static class ProtoRecordBridge
{
    private static readonly Assembly Contracts = typeof(WireJson).Assembly;

    private static readonly Dictionary<string, string> Renames = new() { ["Letters.letters"] = "Items", ["Messages.messages"] = "Items", ["Alerts.alerts"] = "Items", ["Amount.amount"] = "Value", ["Thing.thing"] = "Entity", ["Trader.trader"] = "Entity" };

    private static readonly Dictionary<string, string> TypeRenames = new() { ["Event"] = "ClockEvent", ["ThingFlag"] = "ThingFlagBit", ["BillStack"] = "BillStackRow" };

    /// <summary>The protobuf twin of a record request.</summary>
    public static TProto ToProto<TProto>(object record)
        where TProto : class, IMessage<TProto>, new() =>
        (TProto)ToProto(record, new TProto().Descriptor);

    /// <summary>The record twin of a protobuf reply.</summary>
    public static TRecord ToRecord<TRecord>(IMessage message)
        where TRecord : class =>
        (TRecord)ToRecord(message);

    private static string NsOf(FileDescriptor f)
    {
        var seg = f.Package.Split('.')[1];
        return char.ToUpperInvariant(seg[0]) + seg[1..];
    }

    private static bool IsDefs(FileDescriptor f) => f.Package.Contains(".defs.", StringComparison.Ordinal);

    private static string TypeName(string name) => TypeRenames.TryGetValue(name, out var renamed) ? renamed : name;

    private static Type RecordType(MessageDescriptor m) => Contracts.GetType($"RimGovernor.Contracts.Wire.{NsOf(m.File)}.{TypeName(m.ClrType.Name)}", true)!;

    private static Type EnumType(EnumDescriptor e) => Contracts.GetType($"RimGovernor.Contracts.Wire.{(IsDefs(e.File) ? "Defs" : NsOf(e.File))}.{TypeName(e.ClrType.Name)}", true)!;

    private static string Prop(MessageDescriptor m, FieldDescriptor f) => Renames.TryGetValue(m.Name + "." + f.Name, out var renamed) ? renamed : f.PropertyName;

    private static bool Dropped(FieldDescriptor f) => f.FieldType == FieldType.Message && !f.IsMap && IsDefs(f.MessageType.File);

    private static string OneofProp(MessageDescriptor m, OneofDescriptor o)
    {
        var name = char.ToUpperInvariant(o.Name[0]) + o.Name[1..];
        return m.Fields.InDeclarationOrder().Any(x => x.PropertyName == name) || name == TypeName(m.ClrType.Name) ? name + "Case" : name;
    }

    private static object ValueToRecord(FieldDescriptor f, object v) =>
        f.FieldType switch
        {
            FieldType.Message => ToRecord((IMessage)v),
            FieldType.Enum => Enum.ToObject(EnumType(f.EnumType), (int)v),
            FieldType.Bytes => ((ByteString)v).ToByteArray(),
            _ => v,
        };

    private static object ToRecord(IMessage msg)
    {
        var d = msg.Descriptor;
        var type = RecordType(d);
        var rec = Activator.CreateInstance(type)!;
        var seenOneof = new HashSet<int>();
        foreach (var f in d.Fields.InDeclarationOrder())
        {
            if (Dropped(f))
            {
                continue;
            }
            if (f.RealContainingOneof is { } o)
            {
                if (!seenOneof.Add(o.Index))
                {
                    continue;
                }
                var cf = o.Accessor.GetCaseFieldDescriptor(msg);
                if (cf is null)
                {
                    continue;
                }
                var payload = ValueToRecord(cf, cf.Accessor.GetValue(msg));
                var baseName = char.ToUpperInvariant(o.Name[0]) + o.Name[1..];
                var caseType = type.Assembly.GetType($"{type.Namespace}.{type.Name}{baseName}+{cf.PropertyName}Case", true)!;
                type.GetProperty(OneofProp(d, o))!.SetValue(rec, Activator.CreateInstance(caseType, payload));
                continue;
            }
            var prop = type.GetProperty(Prop(d, f))!;
            var raw = f.Accessor.GetValue(msg);
            if (f.IsMap)
            {
                var concrete = typeof(Dictionary<,>).MakeGenericType(prop.PropertyType.GetGenericArguments());
                var dict = (IDictionary)Activator.CreateInstance(concrete)!;
                foreach (DictionaryEntry e in (IDictionary)raw)
                {
                    dict[e.Key] = e.Value;
                }
                prop.SetValue(rec, dict);
            }
            else if (f.IsRepeated)
            {
                var elementType = prop.PropertyType.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                foreach (var item in (IEnumerable)raw)
                {
                    list.Add(ValueToRecord(f, item));
                }
                prop.SetValue(rec, typeof(EquatableArray).GetMethod("From")!.MakeGenericMethod(elementType).Invoke(null, [list]));
            }
            else if (f.FieldType == FieldType.Message)
            {
                if (raw is not null)
                {
                    prop.SetValue(rec, ValueToRecord(f, raw));
                }
            }
            else if (!f.HasPresence || f.Accessor.HasValue(msg))
            {
                prop.SetValue(rec, ValueToRecord(f, raw));
            }
        }
        return rec;
    }

    private static IMessage ToProto(object rec, MessageDescriptor d)
    {
        var msg = (IMessage)Activator.CreateInstance(d.ClrType)!;
        var type = rec.GetType();
        var seenOneof = new HashSet<int>();
        foreach (var f in d.Fields.InDeclarationOrder())
        {
            if (Dropped(f))
            {
                continue;
            }
            if (f.RealContainingOneof is { } o)
            {
                if (!seenOneof.Add(o.Index))
                {
                    continue;
                }
                var inst = type.GetProperty(OneofProp(d, o))!.GetValue(rec);
                if (inst is null)
                {
                    continue;
                }
                var cf = o.Fields.First(x => x.PropertyName + "Case" == inst.GetType().Name);
                cf.Accessor.SetValue(msg, ValueToProto(cf, inst.GetType().GetProperty("Value")!.GetValue(inst)!));
                continue;
            }
            var val = type.GetProperty(Prop(d, f))!.GetValue(rec);
            if (f.IsMap)
            {
                var target = (IDictionary)f.Accessor.GetValue(msg);
                foreach (var kv in (IEnumerable)val!)
                {
                    var t = kv.GetType();
                    target[t.GetProperty("Key")!.GetValue(kv)!] = t.GetProperty("Value")!.GetValue(kv)!;
                }
            }
            else if (f.IsRepeated)
            {
                var target = (IList)f.Accessor.GetValue(msg);
                foreach (var item in (IEnumerable)val!)
                {
                    target.Add(ValueToProto(f, item));
                }
            }
            else if (val is not null)
            {
                f.Accessor.SetValue(msg, ValueToProto(f, val));
            }
        }
        return msg;
    }

    private static object ValueToProto(FieldDescriptor f, object v) =>
        f.FieldType switch
        {
            FieldType.Message => ToProto(v, f.MessageType),
            FieldType.Enum => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture),
            FieldType.Bytes => ByteString.CopyFrom((byte[])v),
            _ => v,
        };
}
