using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MdbToSql.AccessProbe;

internal static class ProbeUtil
{
    public static HashSet<int> CurrentAccessPids()
    {
        return Process.GetProcessesByName("MSACCESS")
            .Select(process => process.Id)
            .ToHashSet();
    }

    public static string? TryGetProcessPath(int? pid)
    {
        if (pid is null or 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsAccess2003(string? version, string? path)
    {
        var versionOk = version is not null && version.StartsWith("11.", StringComparison.Ordinal);
        var pathOk = path is null || path.Contains("OFFICE11", StringComparison.OrdinalIgnoreCase);
        var isOffice15 = path is not null && path.Contains("Office15", StringComparison.OrdinalIgnoreCase);
        return versionOk && pathOk && !isOffice15;
    }

    public static bool WaitUntilFileUnlocked(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }

    public static void WaitUntilAccessGone(IReadOnlySet<int> beforePids)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!CurrentAccessPids().Except(beforePids).Any())
            {
                return;
            }

            Thread.Sleep(400);
        }
    }

    public static void EnsureSafeLocalMdbPath(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Ruta vacía para {purpose}.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Rechazado {purpose}: no se abren rutas UNC ({path}).");
        }

        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Rechazado {purpose}: la ruta resuelta es UNC ({full}).");
        }
    }

    public static void TryCall(object? target, string method, params object[] args)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            Com.Call(target, method, args);
        }
        catch
        {
            // Cierre/Quit best-effort.
        }
    }

    public static bool TryDeleteDirectory(string path, List<string> warnings)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception exception)
        {
            warnings.Add($"No se pudo eliminar el temporal '{path}': {exception.Message}");
            return false;
        }
    }

    public static ComExceptionInfo Describe(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var info = new ComExceptionInfo
        {
            Message = current.Message,
            Source = current.Source,
            InnerException = exception.InnerException?.ToString()
        };

        if (current is COMException com)
        {
            var code = unchecked((uint)com.ErrorCode);
            info.HResult = com.ErrorCode;
            info.HResultHex = $"0x{code:X8}";
            if ((code & 0xFFFF0000) == 0x800A0000)
            {
                info.AccessError = (int)(code & 0xFFFF);
            }
        }
        else
        {
            info.HResult = current.HResult;
            info.HResultHex = $"0x{current.HResult:X8}";
        }

        return info;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void PrepareLocalWorkingCopy(string path)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch
        {
            // El ADS puede no existir.
        }
    }

    public static bool IsSystemObjectName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            || name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("~", StringComparison.Ordinal);
    }

    public static bool SamePath(string a, string b)
    {
        return string.Equals(
            Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ComExceptionInfo
{
    public int? HResult { get; set; }
    public string? HResultHex { get; set; }
    public int? AccessError { get; set; }
    public string? Message { get; set; }
    public string? Source { get; set; }
    public string? InnerException { get; set; }
}
