using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MdbToSql.AccessApplication.Com;

internal readonly record struct ComPropertyRead(bool Available, object? Value, string? Error);

internal sealed class ComPropertyReader
{
    private static readonly HashSet<int> UnavailableErrorCodes =
    [
        unchecked((int)0x80020003), // DISP_E_MEMBERNOTFOUND
        unchecked((int)0x80020005), // DISP_E_TYPEMISMATCH
        unchecked((int)0x80020006), // DISP_E_UNKNOWNNAME
        438,   // Object doesn't support this property or method
        2455,  // Invalid reference
        2465,  // Application-defined or object-defined error / can't find field
        2186,
        2220
    ];

    public ComPropertyRead Read(object target, string property)
    {
        try
        {
            var value = ComInterop.Get(target, property);
            return new ComPropertyRead(true, value is DBNull ? null : value, null);
        }
        catch (Exception exception)
        {
            return FromException(property, exception);
        }
    }

    public string? ReadString(object target, string property, List<string>? warnings)
    {
        var read = Read(target, property);
        Record(warnings, read);
        if (!read.Available || read.Value is null)
        {
            return null;
        }

        return read.Value switch
        {
            string text => string.IsNullOrEmpty(text) ? null : text,
            bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(read.Value, CultureInfo.InvariantCulture),
            _ => Convert.ToString(read.Value, CultureInfo.InvariantCulture)
        };
    }

    public bool? ReadBool(object target, string property, List<string>? warnings)
    {
        var read = Read(target, property);
        Record(warnings, read);
        if (!read.Available || read.Value is null)
        {
            return null;
        }

        return read.Value switch
        {
            bool flag => flag,
            sbyte number => number != 0,
            byte number => number != 0,
            short number => number != 0,
            ushort number => number != 0,
            int number => number != 0,
            uint number => number != 0,
            long number => number != 0,
            ulong number => number != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    public int? ReadInt(object target, string property, List<string>? warnings)
    {
        var read = Read(target, property);
        Record(warnings, read);
        if (!read.Available || read.Value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(read.Value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            warnings?.Add($"{property}: no se pudo convertir a int ({exception.Message}).");
            return null;
        }
    }

    public object? ReadObject(object target, string property, List<string>? warnings)
    {
        var read = Read(target, property);
        Record(warnings, read);
        return read.Available ? read.Value : null;
    }

    private static void Record(List<string>? warnings, ComPropertyRead read)
    {
        if (read.Error is not null)
        {
            warnings?.Add(read.Error);
        }
    }

    private static ComPropertyRead FromException(string property, Exception exception)
    {
        var inner = exception is TargetInvocationException invocation
            ? invocation.InnerException ?? exception
            : exception;
        if (inner is COMException com && IsUnavailable(com))
        {
            return new ComPropertyRead(false, null, null);
        }

        return new ComPropertyRead(false, null, $"{property}: {inner.Message}");
    }

    private static bool IsUnavailable(COMException exception)
    {
        if (UnavailableErrorCodes.Contains(exception.ErrorCode))
        {
            return true;
        }

        var lowWord = exception.ErrorCode & 0xFFFF;
        return UnavailableErrorCodes.Contains(lowWord);
    }
}
