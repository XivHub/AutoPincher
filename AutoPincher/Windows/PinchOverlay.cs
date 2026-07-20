using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using AutoPincher.Bridge;

namespace AutoPincher.Windows;

/// <summary>
/// Renders the "Auto Pinch" button inline with AutoRetainer's RetainerList
/// controls by subscribing to the AR IPC AutoRetainer.OnMainControlsDraw. AR
/// fires that signal from inside its own retainer-list overlay's Draw, so the
/// button shows up next to its "Enable AutoRetainer" / "MultiMode" checkboxes.
/// No-op when AutoRetainer isn't installed (the subscribe just never fires).
/// </summary>
public sealed class PinchOverlay : IDisposable
{
    private const string IpcName = "AutoRetainer.OnMainControlsDraw";

    private readonly PinchDriver _driver;
    private readonly Action _handler;

    public PinchOverlay(PinchDriver driver)
    {
        _driver = driver;
        _handler = OnAutoRetainerControlsDraw;
        try
        {
            Svc.PluginInterface
                .GetIpcSubscriber<object>(IpcName)
                .Subscribe(_handler);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "PinchOverlay: AR OnMainControlsDraw subscribe failed (AR not loaded?)");
        }
    }

    public void Dispose()
    {
        try
        {
            Svc.PluginInterface
                .GetIpcSubscriber<object>(IpcName)
                .Unsubscribe(_handler);
        }
        catch { /* AR may already be gone */ }
    }

    private void OnAutoRetainerControlsDraw()
    {
        if (!Plugin.Configuration.EnablePinch) return;

        try
        {
            ImGui.SameLine();
            if (_driver.IsBusy)
            {
                if (ImGui.Button("Cancel##autopincher"))
                    _driver.AbortAll();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Cancel the AutoPincher session");
            }
            else
            {
                if (ImGui.Button("Auto Pinch##autopincher"))
                    _ = Task.Run(() => _driver.RunAllAsync(CancellationToken.None));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Undercut every retainer with active listings.\nDo not interact with the game while this runs.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "PinchOverlay draw failed");
        }
    }
}
