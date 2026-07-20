using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoPincher.Bridge;

/// <summary>
/// Reads the currently-open retainer's market listings directly from game memory.
/// No AllaganTools dependency: the InventoryManager RetainerMarket container and
/// its parallel RetainerMarketPrices span are loaded by the game whenever that
/// retainer's RetainerSellList addon is open.
/// </summary>
public static class RetainerMarketReader
{
    /// <summary>
    /// Read the open retainer's listings from the RetainerMarket container.
    /// Only valid while that retainer's RetainerSellList is open. Must be called
    /// on the framework thread. Returns slots in container order (== RetainerSellList
    /// row order); empty slots are skipped.
    /// </summary>
    public static unsafe List<RetainerMarketRow> GetSlotsLive(IPluginLog log)
    {
        var result = new List<RetainerMarketRow>();
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return result;
            var container = im->GetInventoryContainer(InventoryType.RetainerMarket);
            if (container == null) return result;
            // Per-slot unit prices, parallel to the container by slot index.
            var prices = im->RetainerMarketPrices;
            // The RetainerMarket container can be sparse (e.g. filled slots
            // 0,3,4,10). The RetainerSellList addon shows only filled slots,
            // PACKED into visual rows 0..N-1 in ascending slot order, and the
            // reprice click targets that visual row index — NOT the container
            // slot. So Slot here is the packed row index; price/item still come
            // from the real container slot i.
            int visualRow = 0;
            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                // RetainerMarketPrices is Span<ulong>; a listing price always
                // fits in uint (game cap is 999,999,999). Quantity is int.
                uint price = i < prices.Length ? (uint)prices[i] : 0u;
                bool hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                result.Add(new RetainerMarketRow((uint)visualRow, slot->ItemId, hq, (uint)slot->Quantity, price));
                visualRow++;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "RetainerMarketReader.GetSlotsLive failed");
        }
        return result;
    }
}

/// <summary>A single retainer market slot row read from game memory.</summary>
public readonly record struct RetainerMarketRow(uint Slot, uint ItemId, bool Hq, uint Qty, uint Price);
