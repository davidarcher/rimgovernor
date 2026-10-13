using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>clockWire</c>: clock fields are typed at this boundary, and the walk only rejects unknown wire
    /// fields, unsupported enums (a populated enum field must name a specified, defined value) and nonfinite
    /// floats, including repeated evidence.
    /// </summary>
    internal static void ClockWire(IMessage? value)
    {
        if (value is null)
        {
            throw Contract("missing clock message");
        }
        if (UnknownFields.Present(value))
        {
            throw Contract("unknown clock fields");
        }
        Walk(value);
    }

    private static void Walk(IMessage message)
    {
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            var accessor = field.Accessor;
            if (field.IsMap)
            {
                var map = (IDictionary)accessor.GetValue(message);
                foreach (var entry in map.Values)
                {
                    CheckValue(field, entry);
                }
            }
            else if (field.IsRepeated)
            {
                foreach (var item in (IList)accessor.GetValue(message))
                {
                    CheckValue(field, item);
                }
            }
            else if (field.HasPresence ? accessor.HasValue(message) : IsPopulated(field, accessor.GetValue(message)))
            {
                CheckValue(field, accessor.GetValue(message));
            }
        }
    }

    private static bool IsPopulated(FieldDescriptor field, object? value) =>
        value switch
        {
            null => false,
            int i => i != 0,
            uint u => u != 0,
            long l => l != 0,
            ulong ul => ul != 0,
            float f => !f.Equals(0f),
            double d => !d.Equals(0d),
            bool b => b,
            string s => s.Length != 0,
            ByteString bytes => bytes.Length != 0,
            Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture) != 0,
            _ => field.FieldType == FieldType.Message,
        };

    private static void CheckValue(FieldDescriptor field, object? value)
    {
        switch (field.FieldType)
        {
            case FieldType.Message:
                if (value is IMessage inner)
                {
                    Walk(inner);
                }
                break;
            case FieldType.Enum:
                var number = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                if (number == 0 || field.EnumType.FindValueByNumber(number) is null)
                {
                    throw Contract("unsupported clock enum");
                }
                break;
            case FieldType.Float:
                if (!float.IsFinite((float)value!))
                {
                    throw Contract("nonfinite clock value");
                }
                break;
            case FieldType.Double:
                if (!double.IsFinite((double)value!))
                {
                    throw Contract("nonfinite clock value");
                }
                break;
            default:
                break;
        }
    }
}
