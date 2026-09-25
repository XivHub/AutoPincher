# AutoPincher

A lightweight, fully-local Dalamud plugin that undercuts your FFXIV retainer
market listings. It reads each listing straight from game memory, uses the
in-game market board's **Compare Prices** to find the current cheapest
competitor, and sets your asking price to **1 gil below it**.

- Your own retainers and housing mannequins are never competitors. A listing
  never sits above your own cheapest copy of the item, so two retainers
  selling the same thing land on the same price instead of chasing each other
  down.
- With nobody else selling, the listing is raised to the last sale price,
  never cut. Turn on *Skip when no live competitor* to leave those for you to
  price by hand.
- If the board does not answer in time, the listing keeps its current price.
- A listing never goes below its vendor floor: the lowest asking price that
  still clears what a vendor pays or charges after the 5% market tax. Below
  that the listing holds, and one already under it is raised back.

No server, and nothing leaves your client unless you turn on the developer log
(see Usage), which sends log lines only to a URL you set. This is the pinch
engine from
[FFMarketConnector](https://github.com/edg-l/FFMarketConnector), shared through
[XivHubPluginKit](https://github.com/XivHub/XivHubPluginKit), without the data
streaming.

## Usage

- **All retainers:** with AutoRetainer installed, an **Auto Pinch** button appears
  next to its retainer-list controls. Open your retainer list (the bell) and click
  it to walk every retainer with active listings.
- **One retainer:** open a retainer's sell list and run `/autopinch` (or the
  *Pinch open retainer now* button in the config window).
- **Config:** `/autopincher` opens the window — toggle the plugin, and tune the
  per-item delay and the market-board request delay.
- **Developer:** an opt-in log mirror for debugging. Tick *Send my log lines to
  a local server* and save a URL such as `http://<host>:9999/log`; it stays
  inert unless both are set.

Do not touch the keyboard/mouse while a pinch session runs; it drives the game UI.

## Dependencies

- **ECommons** (UI automation, throttling, IPC).
- **AutoRetainer** — *optional*. Used only for the inline Auto Pinch button and to
  pause AR's own automation during a run. The plugin works without it via
  `/autopinch`.

## Build / deploy

```bash
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build AutoPincher/AutoPincher.csproj -c Release -p:Platform=x64
./publish.sh   # builds Release and merges into the combined XivHub plugin repo
```

Bump `<Version>` in `AutoPincher/AutoPincher.csproj` before every publish.

## Disclaimer

Automates gameplay actions, which is against the FFXIV Terms of Service. Use at
your own risk.
