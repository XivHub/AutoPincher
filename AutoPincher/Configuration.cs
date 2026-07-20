using Dalamud.Configuration;
using System;

namespace AutoPincher;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// Show the "Auto Pinch" button on AutoRetainer's retainer-list controls and
    /// allow the /autopinch command. When false the plugin stays loaded but inert.
    /// </summary>
    public bool EnablePinch { get; set; } = true;

    /// <summary>
    /// Delay in milliseconds between each item's price-edit UI steps. Lower is
    /// faster but more likely to outrun the game's addon transitions.
    /// </summary>
    public int PinchPerItemDelayMs { get; set; } = 100;

    /// <summary>
    /// Minimum delay in milliseconds between consecutive in-game market-board
    /// "Compare Prices" requests. FFXIV rate-limits these server-side; ~2s is
    /// conservative. Going below risks "please wait a short while" rejections.
    /// </summary>
    public int PinchMarketBoardDelayMs { get; set; } = 2000;

    /// <summary>
    /// What to do when an item has no live competitor on the board (nobody else
    /// is selling it). When false (default) Pinch falls back to the history
    /// window and matches the most recent sale price. When true Pinch leaves the
    /// listing unchanged and moves on — being the only seller is often a chance
    /// to raise the price by hand, so don't auto-reprice it.
    /// </summary>
    public bool PinchSkipIfNoCompetitor { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
