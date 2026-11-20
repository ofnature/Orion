using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Orion.Services.Ipc;

/// <summary>
/// What Orion offers other plugins.
/// <list type="bullet">
/// <item><c>Orion.IsBusy</c> (returns bool): true while a run is driving the character. Daedalus
/// polls it and fights for Orion while it is true; that gate is the whole contract between the two.</item>
/// </list>
/// </summary>
public sealed class OrionIpc : IDisposable
{
    private readonly ICallGateProvider<bool> _isBusy;

    public OrionIpc(IDalamudPluginInterface pluginInterface, Func<bool> isBusy)
    {
        _isBusy = pluginInterface.GetIpcProvider<bool>("Orion.IsBusy");
        _isBusy.RegisterFunc(() =>
        {
            // Never throws into a caller's poll: a failure reads as idle.
            try { return isBusy(); }
            catch { return false; }
        });
    }

    public void Dispose() => _isBusy.UnregisterFunc();
}
