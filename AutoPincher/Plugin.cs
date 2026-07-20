using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using AutoPincher.Bridge;
using AutoPincher.Windows;

namespace AutoPincher;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;

    private const string CommandName = "/autopincher";
    private const string PinchCommandName = "/ffmpinch";

    public static Configuration Configuration { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("AutoPincher");
    private ConfigWindow ConfigWindow { get; init; }
    private readonly PinchOverlay _pinchOverlay;

    private readonly MarketBoardListener _mbListener;
    private readonly PinchDriver _pinchDriver;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ECommonsMain.Init(PluginInterface, this);

        _mbListener = new MarketBoardListener(MarketBoard, Log);
        _pinchDriver = new PinchDriver(Log, ChatGui, _mbListener);

        ConfigWindow = new ConfigWindow(_pinchDriver);
        _pinchOverlay = new PinchOverlay(_pinchDriver);

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the AutoPincher config window",
        });
        CommandManager.AddHandler(PinchCommandName, new CommandInfo(OnPinchCommand)
        {
            HelpMessage = "Undercut the currently-open retainer's listings (requires its sell list open)",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;

        Log.Information("AutoPincher loaded");
    }

    public void Dispose()
    {
        ECommonsMain.Dispose();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        _pinchOverlay.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(PinchCommandName);

        _pinchDriver.Dispose();
        _mbListener.Dispose();
    }

    private void OnCommand(string command, string args) => ConfigWindow.Toggle();

    private void OnPinchCommand(string command, string args)
    {
        if (!Configuration.EnablePinch)
        {
            ChatGui.PrintError("[autopincher] Disabled in config.");
            return;
        }
        if (_pinchDriver.IsBusy)
        {
            ChatGui.PrintError("[autopincher] Already running.");
            return;
        }
        if (!_pinchDriver.CanPinchNow())
        {
            ChatGui.PrintError("[autopincher] Open a retainer's sell list first.");
            return;
        }
        _ = Task.Run(() => _pinchDriver.RunAsync(CancellationToken.None));
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
