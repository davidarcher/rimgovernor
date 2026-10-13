using System.Collections;

namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>clockWire</c>: clock fields are typed at this boundary, and the walk rejects unsupported enums (a
    /// populated enum field must name a specified, defined value) and nonfinite floats, including repeated
    /// evidence. Unknown wire fields never reach a record: the JSON decoder rejects them.
    /// </summary>
    internal static void ClockWire(object? value)
    {
        if (value is null)
        {
            throw Contract("missing clock message");
        }
        Walk(value);
    }

    private static void Walk(object message)
    {
        foreach (var field in WireMessage.Of(message).Fields)
        {
            if (field.IsMap)
            {
                foreach (var entry in ((IDictionary)field.Get(message)!).Values)
                {
                    CheckValue(field, entry);
                }
            }
            else if (field.IsRepeated)
            {
                foreach (var item in field.Elements(message))
                {
                    CheckValue(field, item);
                }
            }
            else if (field.Get(message) is { } present)
            {
                CheckValue(field, present);
            }
        }
    }

    private static void CheckValue(WireField field, object? value)
    {
        switch (field.Kind)
        {
            case WireKind.Message:
                if (value is not null)
                {
                    Walk(value);
                }
                break;
            case WireKind.Enum:
                var number = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                if (number == 0 || !Enum.IsDefined(field.ValueType, value!))
                {
                    throw Contract("unsupported clock enum");
                }
                break;
            default:
                if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d))
                {
                    throw Contract("nonfinite clock value");
                }
                break;
        }
    }
}
