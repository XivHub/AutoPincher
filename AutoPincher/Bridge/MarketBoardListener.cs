using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;

namespace AutoPincher.Bridge;

/// <summary>
/// Listens for in-game market-board "current offerings" packets (the data that
/// arrives when the player clicks "Compare Prices" on a RetainerSell window)
/// and exposes the cheapest competitor price for the most recently requested
/// item. Used by Pinch's live-compare path to price items the server has no
/// Universalis data for (source="none") — typically raw materials the model
/// doesn't track.
///
/// FFXIV rate-limits market-board price requests server-side; the caller is
/// responsible for spacing requests (PinchMarketBoardDelayMs). This class only
/// records what comes back.
/// </summary>
public sealed class MarketBoardListener : IDisposable
{
    private readonly IMarketBoard _mb;
    private readonly IPluginLog _log;

    private readonly object _gate = new();
    // The item we're currently waiting on a result for (0 = not waiting).
    private uint _awaitingItemId;
    private bool _awaitingHq;
    // The player's own retainer CIDs, set per request so "is this my listing?"
    // is decided against the real roster, not the pinned-character filter.
    private HashSet<ulong> _ownRetainerCids = new();
    // Result for the awaited item, once offerings arrive. null = not yet.
    private uint? _cheapest;
    private bool _cheapestIsOwn;
    // History-window fallback: the most recent sale's unit price (HQ-matched) and
    // whether a history packet for the awaited item has arrived at all. The game
    // answers a "Compare Prices" lookup with BOTH an offerings packet and a
    // history packet; when nothing is currently listed it often sends only the
    // history packet, so this is what lets Pinch price an item nobody else is
    // selling instead of waiting out the offerings timeout.
    private uint? _mostRecentSale;
    private bool _historyArrived;

    public MarketBoardListener(IMarketBoard mb, IPluginLog log)
    {
        _mb = mb;
        _log = log;
        _mb.OfferingsReceived += OnOfferings;
        _mb.HistoryReceived += OnHistory;
    }

    /// <summary>
    /// Begin waiting for offerings for the given item. Clears any prior result.
    /// Call immediately before clicking "Compare Prices". <paramref name="ownRetainerCids"/>
    /// is the player's retainer CID set, used to flag self-listings.
    /// </summary>
    public void BeginRequest(uint itemId, bool hq, HashSet<ulong> ownRetainerCids)
    {
        lock (_gate)
        {
            _awaitingItemId = itemId;
            _awaitingHq = hq;
            _ownRetainerCids = ownRetainerCids;
            _cheapest = null;
            _cheapestIsOwn = false;
            _mostRecentSale = null;
            _historyArrived = false;
        }
    }

    /// <summary>
    /// The cheapest competitor unit price observed for the awaited item, or null
    /// if offerings haven't arrived yet. <paramref name="isOwn"/> is true when
    /// the cheapest listing belongs to one of the player's own retainers.
    /// </summary>
    public uint? TryGetCheapest(out bool isOwn)
    {
        lock (_gate)
        {
            isOwn = _cheapestIsOwn;
            return _cheapest;
        }
    }

    /// <summary>
    /// The most recent sale's unit price from the history window (HQ-matched),
    /// or null if no qualifying sale was seen. <paramref name="historyArrived"/>
    /// is true once a history packet for the awaited item has been processed
    /// (even an empty one) — that's the signal that the board genuinely has no
    /// current listings rather than the request still being in flight.
    /// </summary>
    public uint? TryGetHistory(out bool historyArrived)
    {
        lock (_gate)
        {
            historyArrived = _historyArrived;
            return _mostRecentSale;
        }
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        try
        {
            var listings = offerings.ItemListings;
            if (listings == null || listings.Count == 0)
            {
                lock (_gate)
                {
                    if (_awaitingItemId != 0)
                    {
                        // Empty board: record "no competitor" so the waiter can
                        // stop waiting rather than time out.
                        _cheapest ??= 0u;
                    }
                }
                return;
            }

            uint awaitId;
            bool wantHq;
            HashSet<ulong> own;
            lock (_gate)
            {
                awaitId = _awaitingItemId;
                wantHq = _awaitingHq;
                own = _ownRetainerCids;
            }
            if (awaitId == 0) return;

            // Offerings only ever concern one item; confirm it matches what we
            // asked for (offerings arrive in batches of ~10, possibly several
            // packets — keep the running minimum across them).
            if (listings[0].ItemId != awaitId) return;

            // Match HQ: when selling an HQ item that can be HQ, only HQ
            // competitors are comparable; otherwise compare against all.
            var candidates = listings.Where(l => !wantHq || l.IsHq).ToList();
            if (candidates.Count == 0) return; // wait for a batch with a match

            var best = candidates.OrderBy(l => l.PricePerUnit).First();
            bool isOwn = own.Contains(best.RetainerId);

            lock (_gate)
            {
                // Only the item id is matched (not a per-request token — offerings
                // packets carry none). On a Pinch live-compare retry the awaited
                // item is unchanged, so a late packet from the prior request cycle
                // is accepted: harmless, since it's the same item's current price.
                if (_awaitingItemId != awaitId) return; // a new request superseded us
                if (_cheapest is null || _cheapest == 0u || best.PricePerUnit < _cheapest)
                {
                    _cheapest = best.PricePerUnit;
                    _cheapestIsOwn = isOwn;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MarketBoardListener: error handling offerings");
        }
    }

    private void OnHistory(IMarketBoardHistory history)
    {
        try
        {
            uint awaitId;
            bool wantHq;
            lock (_gate)
            {
                awaitId = _awaitingItemId;
                wantHq = _awaitingHq;
            }
            if (awaitId == 0 || history.ItemId != awaitId) return;

            var listings = history.HistoryListings;
            // Match HQ the same way offerings do: when selling HQ, only HQ sales
            // are comparable; otherwise any sale counts.
            var sales = listings == null
                ? new List<IMarketBoardHistoryListing>()
                : listings.Where(l => !wantHq || l.IsHq).ToList();
            // Most recent qualifying sale (newest purchase time). SalePrice is the
            // per-unit price, matching offerings' PricePerUnit.
            var recent = sales.OrderByDescending(l => l.PurchaseTime).FirstOrDefault();

            lock (_gate)
            {
                if (_awaitingItemId != awaitId) return; // a new request superseded us
                _historyArrived = true;
                _mostRecentSale = recent is null ? null : recent.SalePrice;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MarketBoardListener: error handling history");
        }
    }

    public void Dispose()
    {
        try { _mb.OfferingsReceived -= OnOfferings; } catch { /* best effort */ }
        try { _mb.HistoryReceived -= OnHistory; } catch { /* best effort */ }
    }
}
