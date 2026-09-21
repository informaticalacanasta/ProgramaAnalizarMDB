using MdbToSql.AccessApplication.Com;
using MdbToSql.AccessApplication.Workspace;
using MdbToSql.Core.Exceptions;

namespace MdbToSql.AccessApplication;

internal sealed class SafeAccessRuntime : IDisposable
{
    private readonly IReadOnlySet<int> _beforePids;
    private bool _disposed;

    private SafeAccessRuntime(
        ComLifetime lifetime,
        object app,
        AccessDialogGuard dialogs,
        IReadOnlySet<int> beforePids,
        bool databaseOpened)
    {
        Lifetime = lifetime;
        App = app;
        Dialogs = dialogs;
        DatabaseOpened = databaseOpened;
        _beforePids = beforePids;
    }

    public ComLifetime Lifetime { get; }

    public object App { get; }

    public AccessDialogGuard Dialogs { get; }

    public bool DatabaseOpened { get; }

    public static SafeAccessRuntime Open(string analysisSafePath, IReadOnlySet<int> beforePids)
    {
        AnalysisWorkspace.EnsureLocal(analysisSafePath, "analysis-safe.mdb");
        var lifetime = new ComLifetime();
        AccessDialogGuard? dialogs = null;
        object? app = null;
        var opened = false;
        SafeAccessRuntime? runtime = null;
        try
        {
            app = lifetime.Track(ComInterop.Create(AccessConstants.AccessProgId));
            var pid = AccessProcessTracker.CurrentPids().Except(beforePids).FirstOrDefault();
            var version = ComInterop.GetString(app, "Version");
            var path = AccessProcessTracker.TryGetPath(pid == 0 ? null : pid);
            if (!AccessProcessTracker.IsAccess2003(version, path))
            {
                throw new AccessApplicationAnalysisException(
                    $"Se automatizó Access '{version}' en '{path}'. Se esperaba Access 2003 (11.0 / OFFICE11).");
            }

            ComInterop.Set(app, "Visible", false);
            ComInterop.Set(app, "AutomationSecurity", AccessConstants.AutomationSecurityLow);
            dialogs = new AccessDialogGuard(pid);
            using (var bypass = new StartupBypassGuard())
            {
                bypass.Press();
                ComInterop.Call(app, "OpenCurrentDatabase", analysisSafePath, false);
                opened = true;
            }

            dialogs.ThrowIfDetected();
            runtime = new SafeAccessRuntime(lifetime, app, dialogs, beforePids, opened);
            dialogs = null;
            var loadedForms = runtime.LoadedNames("Forms");
            var loadedReports = runtime.LoadedNames("Reports");
            if (loadedForms.Count > 0 || loadedReports.Count > 0)
            {
                throw new UnsafeAccessStartupException(loadedForms, loadedReports);
            }

            return runtime;
        }
        catch
        {
            if (runtime is not null)
            {
                runtime.Dispose();
            }
            else
            {
                dialogs?.Dispose();
                if (opened)
                {
                    ComInterop.TryCall(app, "CloseCurrentDatabase");
                }

                ComInterop.TryCall(app, "Quit", AccessConstants.AcQuitSaveNone);
                AccessProcessTracker.WaitUntilGone(beforePids);
                lifetime.Dispose();
            }

            throw;
        }
    }

    public List<string> LoadedNames(string collectionName)
    {
        var names = new List<string>();
        try
        {
            var collection = Lifetime.Track(ComInterop.Get(App, collectionName));
            var count = ComInterop.Count(collection);
            for (var index = 0; index < count; index++)
            {
                var item = Lifetime.Track(ComInterop.Item(collection, index));
                var name = ComInterop.GetString(item, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }
        catch (Exception)
        {
            return names;
        }

        return names;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Dialogs.Dispose();
        if (DatabaseOpened)
        {
            ComInterop.TryCall(App, "CloseCurrentDatabase");
        }

        ComInterop.TryCall(App, "Quit", AccessConstants.AcQuitSaveNone);
        AccessProcessTracker.WaitUntilGone(_beforePids);
        Lifetime.Dispose();
        _disposed = true;
    }
}
