using System.Reflection;
using System.Runtime.InteropServices;

namespace MdbToSql.AccessProbe;

internal static class Com
{
    public static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)
            ?? throw new InvalidOperationException($"No está registrado el ProgID '{progId}'.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"No se pudo crear una instancia de '{progId}'.");
    }

    public static object Get(object target, string property)
    {
        return target.GetType().InvokeMember(
            property,
            BindingFlags.GetProperty,
            binder: null,
            target,
            args: null)!;
    }

    public static object? TryGet(object target, string property)
    {
        try
        {
            return Get(target, property);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? GetString(object target, string property)
    {
        var value = TryGet(target, property);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public static void Set(object target, string property, object value)
    {
        target.GetType().InvokeMember(
            property,
            BindingFlags.SetProperty,
            binder: null,
            target,
            [value]);
    }

    public static object? Call(object target, string method, params object[] args)
    {
        return target.GetType().InvokeMember(
            method,
            BindingFlags.InvokeMethod,
            binder: null,
            target,
            args);
    }

    public static object Item(object collection, object index)
    {
        try
        {
            return collection.GetType().InvokeMember(
                "Item",
                BindingFlags.GetProperty,
                binder: null,
                collection,
                [index])!;
        }
        catch (Exception)
        {
            return Call(collection, "Item", index)!;
        }
    }

    public static int Count(object collection)
    {
        return Convert.ToInt32(Get(collection, "Count"));
    }

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }
}

internal sealed class ComLifetime : IDisposable
{
    private readonly Stack<object> _objects = new();
    private bool _disposed;

    public T Track<T>(T value)
        where T : class
    {
        _objects.Push(value);
        return value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        while (_objects.Count > 0)
        {
            try
            {
                Com.Release(_objects.Pop());
            }
            catch
            {
                // Continúa liberando el resto.
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _disposed = true;
    }
}
