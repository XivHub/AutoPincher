using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AutoPincher.Bridge;

namespace AutoPincher.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly PinchDriver _driver;

    public ConfigWindow(PinchDriver driver) : base("AutoPincher###autopincher-config")
    {
        _driver = driver;
        Size = new Vector2(420, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = Plugin.Configuration;

        ImGui.TextWrapped(
            "Undercuts every retainer listing by 1 gil below the cheapest live " +
            "competitor on the market board. Fully local — nothing is sent anywhere.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool enable = cfg.EnablePinch;
        if (ImGui.Checkbox("Enable AutoPincher", ref enable))
        {
            cfg.EnablePinch = enable;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When off, the Auto Pinch button and /autopinch are inert.");

        int delayMs = cfg.PinchPerItemDelayMs;
        if (ImGui.SliderInt("Per-item delay (ms)##itemdelay", ref delayMs, 50, 2000))
        {
            cfg.PinchPerItemDelayMs = delayMs;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Delay between price-edit UI steps. Lower is faster but riskier.");

        int mbDelay = cfg.PinchMarketBoardDelayMs;
        if (ImGui.SliderInt("Market-board request delay (ms)##mbdelay", ref mbDelay, 500, 5000))
        {
            cfg.PinchMarketBoardDelayMs = mbDelay;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Minimum spacing between 'Compare Prices' requests.\nFFXIV rate-limits these; ~2s is safe.");

        bool skipNoComp = cfg.PinchSkipIfNoCompetitor;
        if (ImGui.Checkbox("Skip when no live competitor", ref skipNoComp))
        {
            cfg.PinchSkipIfNoCompetitor = skipNoComp;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When nobody else is selling the item:\n" +
                "  off (default) — match the most recent sale from the history window.\n" +
                "  on            — leave the price unchanged (chance to raise it by hand).");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Open a retainer's sell list and press the button below, or use the " +
            "'Auto Pinch' button next to AutoRetainer's retainer-list controls to " +
            "run every retainer in one pass.");
        ImGui.Spacing();

        if (_driver.IsBusy)
        {
            if (ImGui.Button("Cancel##cfgcancel"))
                _driver.AbortAll();
        }
        else
        {
            bool canPinch = cfg.EnablePinch && _driver.CanPinchNow();
            if (!canPinch) ImGui.BeginDisabled();
            if (ImGui.Button("Pinch open retainer now"))
                _ = Task.Run(() => _driver.RunAsync(CancellationToken.None));
            if (!canPinch) ImGui.EndDisabled();
            if (!_driver.CanPinchNow() && ImGui.IsItemHovered())
                ImGui.SetTooltip("Open a retainer's sell list first.");
        }

        string last = _driver.LastResultText;
        if (!string.IsNullOrEmpty(last))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Last run: {last}");
        }
    }

    public void Dispose() { }
}
