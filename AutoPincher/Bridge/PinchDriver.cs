using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XivHubPluginKit;
using XivHubPluginKit.Board;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.Pinch;

namespace AutoPincher.Bridge;

/// <summary>
/// AutoPincher's policy over the kit <see cref="PinchEngine"/>: every row is a
/// live market-board lookup, undercutting the cheapest competitor by 1 gil
/// down to the vendor profit floor, fully local. No plan delegate is needed;
/// the engine's own <see cref="PinchEngine.AllLive"/> is the whole policy.
/// </summary>
public sealed class PinchDriver : IDisposable
{
    private readonly IPluginLog _log;
    private readonly PinchEngine _engine;
    private string _lastResultText = "";

    public PinchDriver(IPluginLog log, MarketBoardListener mb)
    {
        _log = log;
        _engine = new PinchEngine(
            mb, log, "AutoPincher",
            settings: () => new PinchSettings(
                Plugin.Configuration.PinchPerItemDelayMs,
                Plugin.Configuration.PinchMarketBoardDelayMs,
                Plugin.Configuration.PinchSkipIfNoCompetitor,
                false),
            profitFloor: VendorPrice.Floor);
    }

    public bool IsBusy => _engine.IsBusy;
    public string LastResultText => Volatile.Read(ref _lastResultText);

    public bool CanPinchNow() => _engine.CanPinchNow();

    /// <summary>Pinch only the currently-open retainer (the /autopinch command).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var result = await _engine.RunOpenAsync(plan: null, dryRun: false, ct);
        if (result is not { } r) return;

        var summary = $"{r.Name}: {r.Reprices} reprice(s) ({r.Rows} rows)";
        Volatile.Write(ref _lastResultText, summary);
        KitServices.Chat.Print($"{KitServices.LogPrefix} {summary}");
    }

    /// <summary>Pinch every retainer with active listings (the Auto Pinch button).</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        if (!Plugin.Configuration.EnablePinch)
        {
            _log.Warning("Pinch session skipped: EnablePinch is false");
            return;
        }

        // Null means the engine's busy or RetainerList gate stopped it: no
        // session ran, so there is nothing to report.
        var result = await _engine.RunAllAsync(plan: null, ct);
        if (result is not { } r) return;

        var summary = $"Pinch session: {r.Retainers} retainers, {r.Reprices} reprices ({r.Rows} rows)";
        if (r.Cancelled) summary += " (cancelled)";
        Volatile.Write(ref _lastResultText, summary);
        KitServices.Chat.Print($"{KitServices.LogPrefix} {summary}");
    }

    public void AbortAll() => _engine.AbortAll();

    public void Dispose() => _engine.Dispose();
}
