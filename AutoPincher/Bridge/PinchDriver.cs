using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoPincher.Bridge;

/// <summary>
/// Drives the in-game UI to reprice retainer listings by undercutting the
/// cheapest live market-board competitor by 1 gil. Fully local: every price is
/// read from the game's own "Compare Prices" market-board lookup; nothing leaves
/// the client. Our own retainers and housing mannequins are not competitors, so
/// the price never chases our own listings down; with nobody to undercut the
/// listing is skipped or priced from sale history.
///
/// The UI automation mirrors AutoRetainer's pipeline idioms (self-throttling
/// fire-until-observed steps) and was verified in-game in the FFMarketConnector
/// "always-live undercut" mode this is extracted from.
/// </summary>
public sealed class PinchDriver : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IChatGui _chat;
    private readonly MarketBoardListener _mb;
    private readonly TaskManager _tasks;
    private string _lastResultText = "";

    private CancellationTokenSource? _cts;
    private int _sessionRetainersProcessed;
    private int _sessionReprices;
    private int _sessionRowsAttempted;

    public PinchDriver(IPluginLog log, IChatGui chat, MarketBoardListener mb)
    {
        _log = log;
        _chat = chat;
        _mb = mb;
        _tasks = new TaskManager { TimeLimitMS = 15000, AbortOnTimeout = true };
    }

    // --- Live market-board compare state ---
    // Resolved compare result per (itemId, hq), cached so the same item across
    // retainers costs one market-board request per session. The result holds only
    // board state, never anything about the listing being edited, so it stays
    // valid for a second retainer selling the same item — our own reprices cannot
    // move a competitor's price. Competitor is the cheapest undercuttable unit
    // price (0 = none); HistoryPrice is the most recent sale's unit price from the
    // history window (0 = none), used as the fallback when nobody else is listing.
    private readonly record struct CompareResult(uint Competitor, uint HistoryPrice);
    private readonly Dictionary<(uint, bool), CompareResult> _liveCompareCache = new();
    // The (name, hq) we've fired a Compare request for and are polling on; null
    // when not mid-request. Deadline is wall-clock ms (Environment.TickCount64).
    private (string Name, bool Hq)? _lcAwaiting;
    private long _lcDeadlineMs;
    private const string MbThrottleName = "AutoPincherMBThrottle";
    private const long LiveCompareTimeoutMs = 5000;
    // Retry budget for the current live-compare item. The market board returns NO
    // offerings packet when rate-limited ("Please wait a short while and try
    // again") or when a request is dropped, which surfaces here as a deadline
    // timeout (a genuinely empty board instead fires an empty offerings event).
    // Re-fire the Compare a few times, backed off by the MB throttle,
    // before giving up on the item. Reset when a new item's request is fired.
    private int _lcRetries;
    private const int LiveCompareMaxRetries = 3;

    // Active session's retainer CIDs, read from RetainerManager at session start.
    // Passed to the market-board listener so our own listings are excluded from
    // the competitor set.
    private HashSet<ulong> _sessionOwnCids = new();

    public bool IsBusy => _tasks.IsBusy;
    public string LastResultText => Volatile.Read(ref _lastResultText);

    // AR-mirror throttle (Utils.cs:1425-1432 in AutoRetainer). Every UI helper
    // checks `GenericThrottle` before firing its action and calls
    // `RethrottleGeneric()` when the prerequisite addon isn't ready yet — that
    // keeps the throttle fresh during the wait so the click only fires N ms
    // *after* the addon becomes ready, never on the first-ready frame.
    private const string ThrottleName = "AutoPincherGenericThrottle";
    private static int DelayMs => Plugin.Configuration.PinchPerItemDelayMs;
    private static bool GenericThrottle => EzThrottler.Throttle(ThrottleName, DelayMs);
    private static void RethrottleGeneric() => EzThrottler.Throttle(ThrottleName, DelayMs, true);

    // Wait task: return true only when the named addon is visible + ready.
    private static unsafe bool? WaitForAddon(string name)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
            && GenericHelpers.IsAddonReady(addon))
            return true;
        RethrottleGeneric();
        return false;
    }

    // Early-exit: number of rows on the current retainer still expected to need a
    // window opened. Counts ROWS, not distinct items, so duplicate stacks of the
    // same item can't consume the budget meant for other rows. Decremented as
    // each compare finishes.
    private int _rowsRemaining = int.MaxValue;

    // Set when the current row can't be repriced — its context menu offers no
    // "Adjust Price" (a mannequin listing), or the window never opened — so the
    // later steps advance instead of waiting on a window that isn't coming.
    private bool _rowUnavailable;
    // Wall-clock deadline for getting this row's RetainerSell window open, stamped
    // by BeginRow. The TaskManager's own time limit aborts the ENTIRE queue on
    // expiry, which turns one bad row into a dead session, so each row gives up on
    // itself first. Covers only the two click steps; the market-board compare that
    // follows has its own, longer, retry budget.
    private long _rowDeadlineMs;
    private const long RowOpenTimeoutMs = 10000;

    public unsafe bool CanPinchNow()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
           && GenericHelpers.IsAddonReady(addon);

    /// <summary>Pinch only the currently-open retainer (the /autopinch command).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        (ulong retainerCid, string retainerName) = await Svc.Framework.RunOnFrameworkThread(ReadActiveRetainer);

        if (retainerCid == 0 || string.IsNullOrEmpty(retainerName))
        {
            _log.Warning("Pinch: no active retainer");
            return;
        }

        if (!CanPinchNow())
        {
            _log.Warning("Pinch: RetainerSellList not open");
            return;
        }

        ResetSessionState();
        _sessionOwnCids = await Svc.Framework.RunOnFrameworkThread(ReadActiveRetainerCids);

        // Read listings live from game memory (incl. items listed earlier this
        // session). RetainerSellList is open (CanPinchNow gate above).
        List<RetainerMarketRow> rows = await Svc.Framework.RunOnFrameworkThread(
            () => RetainerMarketReader.GetSlotsLive(_log).Where(r => r.Price > 0).ToList());

        if (rows.Count == 0)
        {
            _log.Information("Pinch: no priced slots for retainer {Name}", retainerName);
            return;
        }

        int candidates;
        try
        {
            EnqueuePinchTasks(rows, closeOuterList: true, out candidates);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Pinch: exception building UI task queue; aborting");
            _tasks.Abort();
            EnqueueTeardown(true);
            return;
        }

        // Queued (not awaited here) — report the planned count, not completed writes.
        var summary = $"{retainerName}: undercutting {candidates} item(s) ({rows.Count} rows)";
        Volatile.Write(ref _lastResultText, summary);
        _chat.Print($"[autopincher] {summary}");
    }

    /// <summary>Pinch every retainer with active listings (the Auto Pinch button).</summary>
    public async Task RunAllAsync(CancellationToken ct)
    {
        if (!Plugin.Configuration.EnablePinch)
        {
            _log.Warning("Pinch session skipped: EnablePinch is false");
            return;
        }
        if (_tasks.IsBusy)
        {
            _log.Warning("Pinch session skipped: already busy");
            return;
        }

        if (!await Svc.Framework.RunOnFrameworkThread(IsRetainerListReady))
        {
            _log.Warning("Pinch session skipped: RetainerList not open");
            return;
        }

        _sessionRetainersProcessed = 0;
        _sessionReprices = 0;
        _sessionRowsAttempted = 0;
        ResetSessionState();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        bool wasCancelled = false;
        try
        {
            AutoRetainerSuppress.Set(true);
            TalkSkipper.Register();

            // Snapshot retainer roster from RetainerManager by sorted index so
            // each entry carries CID, name, and market-listing count in one
            // framework-thread pass. MarketItemCount is readable without opening
            // the retainer, so we can skip empties (no UI round-trip).
            var snapshot = await Svc.Framework.RunOnFrameworkThread(() =>
            {
                var list = new List<(ulong cid, string name, int index, int marketCount)>();
                unsafe
                {
                    var mgr = RetainerManager.Instance();
                    if (mgr == null) return list;
                    int count = (int)mgr->GetRetainerCount();
                    for (int i = 0; i < count; i++)
                    {
                        var entry = mgr->GetRetainerBySortedIndex((uint)i);
                        if (entry == null) continue;
                        list.Add((entry->RetainerId, entry->NameString, i, entry->MarketItemCount));
                    }
                }
                return list;
            });

            _sessionOwnCids = snapshot.Select(s => s.cid).ToHashSet();

            foreach (var (cid, name, sortedIdx, marketCount) in snapshot)
            {
                if (_cts.Token.IsCancellationRequested) break;

                // Skip retainers with nothing listed without opening them.
                if (marketCount <= 0)
                {
                    _chat.Print($"[autopincher] Skip {name}: nothing listed");
                    continue;
                }

                // Phase 1: open this retainer's sell list so the game loads its
                // RetainerMarket container. Each helper self-throttles and retries
                // until its prerequisite addon is ready.
                _tasks.Enqueue(() => OpenRetainerRow(sortedIdx));
                _tasks.Enqueue(ClickSellItems);
                _tasks.Enqueue(() => WaitForAddon("RetainerSellList"));
                await DrainTasks();
                if (_cts.Token.IsCancellationRequested) break;

                // Phase 2: read listings live from game memory now that this
                // retainer's RetainerSellList is open.
                var rows = await Svc.Framework.RunOnFrameworkThread(
                    () => RetainerMarketReader.GetSlotsLive(_log).Where(r => r.Price > 0).ToList());
                if (rows.Count == 0)
                {
                    _chat.Print($"[autopincher] Skip {name}: no listings");
                    _tasks.Enqueue(CloseRetainerSellList);
                    _tasks.Enqueue(CloseSelectStringBack);
                    await DrainTasks();
                    continue;
                }

                // Phase 3: reprice via live compare, then close back to RetainerList.
                // Actual writes are counted by ApplyCompareResult into _sessionReprices;
                // the out-param here is the planned candidate count, unused per-retainer.
                EnqueuePinchTasks(rows, closeOuterList: false, out _);
                _tasks.Enqueue(CloseRetainerSellList);
                _tasks.Enqueue(CloseSelectStringBack);
                await DrainTasks();

                _sessionRetainersProcessed++;
                _sessionRowsAttempted += rows.Count;
            }

            _tasks.Enqueue(() => { TalkSkipper.Unregister(); return (bool?)true; });
            _tasks.Enqueue(() => { AutoRetainerSuppress.Set(false); return (bool?)true; });
            await DrainTasks();
            wasCancelled = _cts.Token.IsCancellationRequested;
        }
        finally
        {
            // Cleanup direct (not via _tasks) because Abort() would discard
            // enqueued cleanup. Both helpers are idempotent.
            TalkSkipper.Unregister();
            AutoRetainerSuppress.Set(false);

            var summary = $"{_sessionRetainersProcessed} retainers, {_sessionReprices} reprices ({_sessionRowsAttempted} rows)";
            if (wasCancelled) summary += " (cancelled)";
            Volatile.Write(ref _lastResultText, summary);
            _chat.Print($"[autopincher] Pinch session: {summary}");
        }
    }

    public void AbortAll()
    {
        _cts?.Cancel();
        _tasks.Abort();
        // Raw, throttle-bypassing closes: abort must fire immediately.
        Svc.Framework.RunOnTick(() =>
        {
            unsafe
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var s) && s->IsVisible)
                    s->Close(true);
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var c) && c->IsVisible)
                    c->Close(true);
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var l) && l->IsVisible)
                    l->Close(true);
            }
        });
        TalkSkipper.Unregister();
        AutoRetainerSuppress.Set(false);
    }

    private void ResetSessionState()
    {
        _liveCompareCache.Clear();
        _lcAwaiting = null;
        _lcRetries = 0;
    }

    private static unsafe bool IsRetainerListReady()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
           && GenericHelpers.IsAddonReady(addon);

    // Await the currently-enqueued task batch before proceeding.
    private async Task DrainTasks()
    {
        while (_tasks.IsBusy && !(_cts?.Token.IsCancellationRequested ?? true))
            await Task.Delay(250, CancellationToken.None);
    }

    private static unsafe bool? OpenRetainerRow(int sortedIdx)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        var rl = new AddonMaster.RetainerList(addon);
        if (sortedIdx < 0 || sortedIdx >= rl.Retainers.Length) return true;
        if (!GenericThrottle) return false;
        rl.Retainers[sortedIdx].Select();
        return true;
    }

    private static unsafe bool? ClickSellItems()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        var ss = new AddonMaster.SelectString(addon);
        if (ss.Entries.Length <= 2) return false;
        if (!GenericThrottle) return false;
        ss.Entries[2].Select();
        return true;
    }

    // Build the (name,hq) -> itemId live-compare map for every resolvable priced
    // row, then visit each visual row once and let ApplyRepriceFromWindow run the
    // market-board compare for the item the price window actually opens.
    // Order-independent (the sell list is category-sorted, so the row index is NOT
    // a reliable item key) and misprice-proof. The current price is read from the
    // open window rather than carried in this map, so duplicate stacks of one item
    // — which collapse to a single map entry — are each compared against their own
    // asking price.
    private void EnqueuePinchTasks(List<RetainerMarketRow> rows, bool closeOuterList, out int intended)
    {
        intended = 0;
        var liveCompare = new Dictionary<(string Name, bool Hq), uint>();

        foreach (var row in rows)
        {
            string name = NormalizeItemName(ResolveItemName(row.ItemId));
            if (name.Length == 0)
            {
                _log.Warning("Pinch: skip item {Id} — could not resolve item name", row.ItemId);
                continue;
            }
            liveCompare[(name, row.Hq)] = row.ItemId;
            intended++;
        }

        // Budget in rows (== intended), not distinct map entries: two stacks of
        // the same item are two windows to open.
        _rowsRemaining = intended;

        if (liveCompare.Count > 0)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                int rowIndex = i;
                _tasks.Enqueue(BeginRow);
                _tasks.Enqueue(() => OpenItemContextMenu(rowIndex));
                _tasks.Enqueue(ClickAdjustPrice);
                _tasks.Enqueue(() => ApplyRepriceFromWindow(liveCompare));
            }
        }

        EnqueueTeardown(closeOuterList);
    }

    private void EnqueueTeardown(bool closeOuterList = true)
    {
        _tasks.Enqueue(CloseItemSearchResult);
        _tasks.Enqueue(CloseRetainerSell);
        _tasks.Enqueue(CloseContextMenu);
        if (closeOuterList) _tasks.Enqueue(CloseRetainerSellList);
    }

    // Close the market-board compare window if a live-compare left it open.
    private static unsafe bool? CloseItemSearchResult()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var a) || !a->IsVisible)
            return true;
        if (!GenericThrottle) return false;
        a->Close(true);
        return false;
    }

    // Close-and-verify-gone pattern (mirror of AR's SelectQuit): if the addon is
    // still visible we throttle a Close call and return false (retry to verify
    // next tick); only when the addon has disappeared do we return true. This is
    // what keeps the pipeline from racing — the next task always sees post-close.
    private static unsafe bool? CloseRetainerSell()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var s) || !s->IsVisible)
            return true;
        if (!GenericThrottle) return false;
        s->Close(true);
        return false;
    }

    private static unsafe bool? CloseContextMenu()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var c) || !c->IsVisible)
            return true;
        if (!GenericThrottle) return false;
        c->Close(true);
        return false;
    }

    private static unsafe bool? CloseRetainerSellList()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var l) || !l->IsVisible)
            return true;
        if (!GenericThrottle) return false;
        l->Close(true);
        return false;
    }

    // Click the localised "Quit" entry on the retainer SelectString. Mirror of
    // AR's SelectQuit (Excel Addon row 2383 so this works in every client
    // locale). Closing the widget via Close(true) would hide it but the game
    // wouldn't return control to RetainerList — only selecting Quit exits cleanly.
    private static unsafe bool? CloseSelectStringBack()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        var quitText = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
            .GetRow(2383).Text.ToDalamudString().GetText();
        var ss = new AddonMaster.SelectString(addon);
        int quitIdx = -1;
        for (int i = 0; i < ss.Entries.Length; i++)
        {
            if (ss.Entries[i].Text == quitText) { quitIdx = i; break; }
        }
        if (quitIdx < 0) return false;
        if (!GenericThrottle) return false;
        ss.Entries[quitIdx].Select();
        return true;
    }

    // --- Per-row UI helpers (mirrored from Dagobert/AutoPinch.cs) ---

    // Starts a row: clears the previous row's verdict and stamps its deadline.
    private bool? BeginRow()
    {
        _rowUnavailable = false;
        _rowDeadlineMs = Environment.TickCount64 + RowOpenTimeoutMs;
        return true;
    }

    // Give up on the current row instead of letting the step time out and take
    // the whole queue with it. Returns the terminal value for the calling step.
    private bool? SkipRow(string reason)
    {
        _log.Warning("Pinch: {Reason}; skipping row", reason);
        _rowUnavailable = true;
        if (_rowsRemaining != int.MaxValue && _rowsRemaining > 0) _rowsRemaining--;
        return true;
    }

    // Click slot in RetainerSellList → opens ContextMenu.
    //
    // Fire-until-observed: the game can silently drop our callback when
    // RetainerSellList is in a transitional state, so we keep firing the click on
    // every throttle window until ContextMenu actually shows up.
    private unsafe bool? OpenItemContextMenu(int slot)
    {
        // Early-exit: all expected reprices done — don't open this row's window.
        if (_rowsRemaining <= 0)
            return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cm) && cm->IsVisible)
            return true;
        if (Environment.TickCount64 >= _rowDeadlineMs)
            return SkipRow($"no context menu for row {slot}");
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        if (!GenericThrottle) return false;
        Callback.Fire(addon, true, 0, slot, 1);
        return false;
    }

    // Click "Adjust Price" on ContextMenu → opens RetainerSell popup.
    private unsafe bool? ClickAdjustPrice()
    {
        if (_rowUnavailable) return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var rs) && rs->IsVisible)
            return true;
        if (_rowsRemaining <= 0
            && !(GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cmOpen) && cmOpen->IsVisible))
            return true;
        if (Environment.TickCount64 >= _rowDeadlineMs)
        {
            // Leave nothing open for the next row to trip over.
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var stale) && stale->IsVisible)
                stale->Close(true);
            return SkipRow("price window never opened");
        }
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
            || !GenericHelpers.IsAddonReady(addon))
        {
            RethrottleGeneric();
            return false;
        }
        var reader = new ReaderContextMenu(addon);
        bool hasAdjust = reader.Entries.Any(e =>
            e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
            || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));

        if (!hasAdjust)
        {
            if (!GenericThrottle) return false;
            addon->Close(true);
            return SkipRow("context menu has no 'Adjust Price' entry (mannequin)");
        }
        if (!GenericThrottle) return false;
        // Index 0 = "Adjust Price" (Dagobert's assumption).
        Callback.Fire(addon, true, 0, 0, 0, 0, 0);
        return false;
    }

    // Reprice the item the RetainerSell window has ACTUALLY opened, looked up by
    // its name+hq. We never trust the row index to tell us which item this is (the
    // list is category-sorted), so reading the window and matching by name is what
    // makes this misprice-proof. Resolution:
    //   1. eligible for live compare -> compare sub-machine
    //   2. otherwise                 -> cancel, leave unchanged
    private unsafe bool? ApplyRepriceFromWindow(
        Dictionary<(string Name, bool Hq), uint> liveCompare)
    {
        // The row had no "Adjust Price" (mannequin): no window will ever open.
        if (_rowUnavailable) return true;

        if (!GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon)
            || !GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
        {
            if (_rowsRemaining <= 0) return true;
            RethrottleGeneric();
            return false;
        }

        string raw = addon->ItemName->NodeText.GetText();
        bool hq = HasHqGlyph(raw);
        string name = NormalizeItemName(raw);
        var key = (name, hq);

        if (name.Length != 0 && liveCompare.TryGetValue(key, out uint itemId))
        {
            // This stack's own asking price, not a sibling stack's.
            uint curPrice = (uint)addon->AskingPrice->Value;
            bool? r = LiveCompareStep(addon, name, hq, curPrice, itemId);
            // Decrement the early-exit budget once, when the compare finishes
            // (terminal true), not on the false retries while waiting.
            if (r == true && _rowsRemaining != int.MaxValue) _rowsRemaining--;
            return r;
        }

        // Nothing to do — cancel out without touching the price.
        if (!GenericThrottle) return false;
        _log.Debug("Pinch: no target for window item '{Item}' (hq={Hq}); leaving unchanged", name, hq);
        Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel
        return true;
    }

    // Live market-board compare state machine for one open RetainerSell window.
    // Returns false (retry next tick) while a market-board request is in flight,
    // true once the price is set or the item is cancelled/skipped. FFXIV
    // rate-limits price requests, so requests are paced by an EzThrottler keyed
    // to PinchMarketBoardDelayMs.
    private unsafe bool? LiveCompareStep(
        AddonRetainerSell* addon, string name, bool hq, uint curPrice, uint itemId)
    {
        // Cached from a prior retainer this session? Apply immediately.
        if (_liveCompareCache.TryGetValue((itemId, hq), out var cached))
        {
            ApplyCompareResult(addon, name, hq, curPrice, cached);
            return true;
        }

        long now = Environment.TickCount64;

        // Not currently waiting on this item: fire a Compare Prices request,
        // paced by the MB throttle so we never exceed the server rate limit.
        if (_lcAwaiting is null || _lcAwaiting.Value != (name, hq))
        {
            if (!EzThrottler.Throttle(MbThrottleName, Plugin.Configuration.PinchMarketBoardDelayMs))
                return false; // wait out the inter-request delay
            _mb.BeginRequest(itemId, hq, _sessionOwnCids);
            // Compare Prices button on RetainerSell (ECommons: ClickButtonById 4).
            Callback.Fire(&addon->AtkUnitBase, true, 4);
            _lcAwaiting = (name, hq);
            _lcRetries = 0;
            _lcDeadlineMs = now + LiveCompareTimeoutMs;
            _log.Debug("Pinch: live-compare requested for {Item} (hq={Hq})", name, hq);
            return false;
        }

        // Waiting: poll the listener for offerings + history.
        uint? competitor = _mb.TryGetCheapestCompetitor(out bool offeringsArrived, out bool boardEmpty);
        uint? history = _mb.TryGetHistory(out bool historyArrived);

        // A live competitor exists — resolve immediately and undercut. Offerings
        // arrive price-ascending, so the first one we accept is the cheapest.
        if (competitor is not null)
            return Resolve(addon, name, hq, curPrice, itemId, new CompareResult(competitor.Value, history ?? 0u));

        // An empty offerings packet arrived (explicit "nothing listed"): no live
        // competitor, resolve now using the history fallback.
        if (boardEmpty)
            return Resolve(addon, name, hq, curPrice, itemId, new CompareResult(0u, history ?? 0u));

        // Listings exist but none of them is undercuttable so far (all ours, all
        // mannequins) — or no offerings packet has arrived yet. Both wait out the
        // deadline: offerings come in batches of ~10, so a competitor sitting
        // behind our own cheap listings can still be in a later packet.
        if (now >= _lcDeadlineMs)
        {
            // Something came back, just nothing to undercut. Treat it as "no
            // competitor" and use the history fallback instead of timing out.
            if (offeringsArrived || historyArrived)
                return Resolve(addon, name, hq, curPrice, itemId, new CompareResult(0u, history ?? 0u));

            // Nothing arrived at all: rate-limited ("please wait a short while")
            // or a dropped request. Re-fire the Compare, backed off by the MB
            // throttle, until the retry budget is spent.
            if (_lcRetries < LiveCompareMaxRetries)
            {
                if (!EzThrottler.Throttle(MbThrottleName, Plugin.Configuration.PinchMarketBoardDelayMs))
                    return false; // wait out the back-off before re-firing
                _lcRetries++;
                _mb.BeginRequest(itemId, hq, _sessionOwnCids);
                Callback.Fire(&addon->AtkUnitBase, true, 4); // Compare Prices
                _lcDeadlineMs = now + LiveCompareTimeoutMs;
                _log.Information("Pinch: live-compare no response for {Item}; retry {N}/{Max}",
                    name, _lcRetries, LiveCompareMaxRetries);
                return false;
            }
            _log.Warning("Pinch: live-compare timed out for {Item} after {Max} retries; leaving unchanged",
                name, LiveCompareMaxRetries);
            return Resolve(addon, name, hq, curPrice, itemId, new CompareResult(0u, 0u));
        }
        return false; // keep waiting
    }

    // Cache the resolved compare result, clear the await/retry state, and apply it.
    private unsafe bool? Resolve(
        AddonRetainerSell* addon, string name, bool hq, uint curPrice, uint itemId, CompareResult result)
    {
        _liveCompareCache[(itemId, hq)] = result;
        _lcAwaiting = null;
        _lcRetries = 0;
        ApplyCompareResult(addon, name, hq, curPrice, result);
        return true;
    }

    // Decide and write the price from a resolved compare result.
    //   Competitor > 0  -> take the highest price still under the cheapest
    //                      competitor. Our own retainers and mannequins were
    //                      never in that number, so two retainers holding the
    //                      same item land on the same price instead of racing
    //                      each other down a gil per pass.
    //   Competitor == 0 -> nobody to undercut: either skip (config) or fall back
    //                      to the history window's most recent sale price.
    private unsafe void ApplyCompareResult(
        AddonRetainerSell* addon, string name, bool hq, uint curPrice, CompareResult result)
    {
        // Compare Prices opened ItemSearchResult on top of RetainerSell; close it
        // before we confirm/cancel so it doesn't stack across items.
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var isr) && isr->IsVisible)
            isr->Close(true);

        if (result.Competitor == 0)
        {
            ApplyNoCompetitor(addon, name, hq, curPrice, result.HistoryPrice);
            return;
        }
        uint target = result.Competitor > 1 ? result.Competitor - 1 : 1;
        if (target == curPrice)
        {
            _log.Debug("Pinch: {Item} (hq={Hq}) — already at live undercut {Price}", name, hq, target);
            Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel (no change)
            return;
        }
        _log.Information("Pinch: live-undercut {Item} (hq={Hq}) {Old} -> {New} (competitor {Comp})",
            name, hq, curPrice, target, result.Competitor);
        addon->AskingPrice->SetValue((int)target);
        Callback.Fire(&addon->AtkUnitBase, true, 0); // confirm
        _sessionReprices++;
    }

    // No live competitor for this item. Either skip it (PinchSkipIfNoCompetitor —
    // being the only seller is a chance to raise the price by hand) or match the
    // most recent sale from the history window. Always advances; never blocks.
    private unsafe void ApplyNoCompetitor(
        AddonRetainerSell* addon, string name, bool hq, uint curPrice, uint historyPrice)
    {
        if (Plugin.Configuration.PinchSkipIfNoCompetitor)
        {
            _log.Information("Pinch: {Item} (hq={Hq}) — no live competitor; skipping (raise-price opportunity)",
                name, hq);
            Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel
            return;
        }
        if (historyPrice == 0)
        {
            _log.Information("Pinch: {Item} (hq={Hq}) — no live competitor and no sale history; leaving unchanged",
                name, hq);
            Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel
            return;
        }
        if (historyPrice == curPrice)
        {
            _log.Debug("Pinch: {Item} (hq={Hq}) — already at last-sale price {Price}", name, hq, historyPrice);
            Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel (no change)
            return;
        }
        _log.Information("Pinch: history-priced {Item} (hq={Hq}) {Old} -> {New} (last sale, no live competitor)",
            name, hq, curPrice, historyPrice);
        addon->AskingPrice->SetValue((int)historyPrice);
        Callback.Fire(&addon->AtkUnitBase, true, 0); // confirm
        _sessionReprices++;
    }

    // Item display name from the Item sheet (empty on failure).
    private static string ResolveItemName(uint itemId)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            return sheet?.GetRow(itemId).Name.ToDalamudString().GetText() ?? "";
        }
        catch { return ""; }
    }

    // The sell window appends the HQ glyph (U+E03C) to the item name.
    private const char HqGlyph = '\uE03C';
    // FFXIV UI glyphs live in the Unicode private-use area (U+E000–U+F8FF).
    private const char PuaStart = '\uE000';
    private const char PuaEnd = '\uF8FF';

    private static bool HasHqGlyph(string s)
        => !string.IsNullOrEmpty(s) && s.IndexOf(HqGlyph) >= 0;

    // Normalize a sell-window / Item-sheet name for comparison: strip the whole
    // private-use glyph range (HQ marker and friends never appear in the Item
    // sheet name) plus surrounding whitespace, so HQ/NQ names compare cleanly.
    private static string NormalizeItemName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (c < PuaStart || c > PuaEnd) sb.Append(c);
        return sb.ToString().Trim();
    }

    private static unsafe (ulong cid, string name) ReadActiveRetainer()
    {
        try
        {
            var mgr = RetainerManager.Instance();
            if (mgr == null) return (0, string.Empty);
            var active = mgr->GetActiveRetainer();
            if (active == null) return (0, string.Empty);
            return (active->RetainerId, active->NameString);
        }
        catch { return (0, string.Empty); }
    }

    private static unsafe HashSet<ulong> ReadActiveRetainerCids()
    {
        var set = new HashSet<ulong>();
        try
        {
            var mgr = RetainerManager.Instance();
            if (mgr == null) return set;
            int count = (int)mgr->GetRetainerCount();
            for (int i = 0; i < count; i++)
            {
                var entry = mgr->GetRetainerBySortedIndex((uint)i);
                if (entry == null) continue;
                set.Add(entry->RetainerId);
            }
        }
        catch { }
        return set;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* best effort */ }
        try { _cts?.Dispose(); } catch { /* best effort */ }
        _cts = null;
        try { _tasks.Abort(); } catch { /* best effort */ }
        TalkSkipper.Unregister();
        AutoRetainerSuppress.Set(false);
    }
}
