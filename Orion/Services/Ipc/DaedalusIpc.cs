using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Orion.Services.Ipc;

/// <summary>
/// What Orion asks of Daedalus. Fails open: a missing or older Daedalus is a degraded feature,
/// never an exception on our side.
/// <para>
/// The fighting itself needs no call from here. Daedalus polls <c>Orion.IsBusy</c> (its
/// <c>AutomationBusyBridge</c>) and holds its external-combat override while that is true.
/// </para>
/// </summary>
public sealed class DaedalusIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private ICallGateSubscriber<ulong, object>? _recordExternalTargetWrite;

    private ICallGateSubscriber<bool>? _isDisabledByUser;

    public DaedalusIpc(IDalamudPluginInterface pluginInterface) => _pluginInterface = pluginInterface;

    /// <summary>
    /// The user switched Daedalus OFF, so it will not fight for us. Not the same as "not enabled":
    /// Daedalus's switch starts off and still fights for automation until someone presses Disable.
    /// False when Daedalus is missing or too old to say — travel then holds for the fight as before.
    /// </summary>
    public bool IsDisabledByUser()
    {
        try
        {
            _isDisabledByUser ??= _pluginInterface.GetIpcSubscriber<bool>("Daedalus.IsDisabledByUser");
            return _isDisabledByUser.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Claim a hard-target write Orion is about to make, so Daedalus's manual-control grace does not
    /// take it for the user clicking something and hold its movement for four seconds. Call it
    /// immediately before writing the target: the claim only holds for a second.
    /// </summary>
    public void RecordTargetWrite(ulong targetId)
    {
        try
        {
            _recordExternalTargetWrite ??= _pluginInterface.GetIpcSubscriber<ulong, object>("Daedalus.Targeting.RecordExternalWrite");
            _recordExternalTargetWrite.InvokeAction(targetId);
        }
        catch
        {
            // Daedalus missing or too old to have the gate.
        }
    }
}
