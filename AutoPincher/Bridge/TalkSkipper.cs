using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoPincher.Bridge;

internal static class TalkSkipper
{
    private static bool _active;

    public static void Register()
    {
        if (_active) return;
        _active = true;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", HandleTalk);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", HandleTalk);
    }

    public static void Unregister()
    {
        if (!_active) return;
        _active = false;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", HandleTalk);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", HandleTalk);
    }

    private static unsafe void HandleTalk(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!_active) return;
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsVisible) return;
            new AddonMaster.Talk(args.Addon).Click();
            Plugin.Log.Debug("TalkSkipper: clicked Talk ({Event})", type);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "TalkSkipper.HandleTalk failed");
        }
    }
}
