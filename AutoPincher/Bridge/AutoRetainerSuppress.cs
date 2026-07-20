using System;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;

namespace AutoPincher.Bridge;

internal static class AutoRetainerSuppress
{
    public static void Set(bool value)
    {
        try
        {
            Svc.PluginInterface
                .GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed")
                .InvokeAction(value);
        }
        catch (IpcNotReadyError) { /* AR not loaded */ }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "AutoRetainerSuppress.Set({V}) failed", value);
        }
    }

    public static bool TryGet(out bool current)
    {
        current = false;
        try
        {
            current = Svc.PluginInterface
                .GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed")
                .InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
