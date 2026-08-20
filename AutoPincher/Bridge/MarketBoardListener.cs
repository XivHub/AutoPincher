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
    // Cheapest genuine competitor for the awaited item; null until one is seen.
    // Our own retainers and housing mannequins are excluded (see OnOfferings).
    private uint? _cheapestCompetitor;
    // An offerings packet for the awaited item has arrived (with or without an
    // undercuttable listing in it); _boardEmpty additionally means the board
    // came back with no listings at all.
    private bool _offeringsArrived;
    private bool _boardEmpty;
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
            _cheapestCompetitor = null;
            _offeringsArrived = false;
            _boardEmpty = false;
            _mostRecentSale = null;
            _historyArrived = false;
        }
    }

    /// <summary>
    /// The cheapest unit price we may undercut for the awaited item, or null if
    /// no such listing has been seen. Our own retainers and housing mannequins
    /// are not competitors and never appear here.
    /// <paramref name="offeringsArrived"/> is true once any offerings packet for
    /// the item has been processed; <paramref name="boardEmpty"/> additionally
    /// means the board reported no listings at all.
    /// </summary>
    public uint? TryGetCheapestCompetitor(out bool offeringsArrived, out bool boardEmpty)
    {
        lock (_gate)
        {
            offeringsArrived = _offeringsArrived;
            boardEmpty = _boardEmpty;
            return _cheapestCompetitor;
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

            var listings = offerings.ItemListings;
            if (listings == null || listings.Count == 0)
            {
                lock (_gate)
                {
                    if (_awaitingItemId != awaitId) return;
                    _offeringsArrived = true;
                    _boardEmpty = true;
                }
                return;
            }

            // Offerings only ever concern one item; confirm it matches what we
            // asked for (offerings arrive in batches of ~10, possibly several
            // packets — keep the running minimum across them).
            if (listings[0].ItemId != awaitId) return;

            // Only genuine competitors anchor the price:
            //  - our own retainers are excluded, so a second retainer selling the
            //    same item can never make us undercut ourselves;
            //  - housing mannequins are display stock, not a market anchor;
            //  - HQ: when selling an HQ item, only HQ listings are comparable.
            var candidates = listings
                .Where(l => (!wantHq || l.IsHq) && !l.OnMannequin && !own.Contains(l.RetainerId))
                .ToList();
            uint? best = candidates.Count == 0 ? null : candidates.Min(l => l.PricePerUnit);

            lock (_gate)
            {
                // Only the item id is matched (not a per-request token — offerings
                // packets carry none). On a Pinch live-compare retry the awaited
                // item is unchanged, so a late packet from the prior request cycle
                // is accepted: harmless, since it's the same item's current price.
                if (_awaitingItemId != awaitId) return; // a new request superseded us
                _offeringsArrived = true;
                if (best is not null && (_cheapestCompetitor is null || best < _cheapestCompetitor))
                    _cheapestCompetitor = best;
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
