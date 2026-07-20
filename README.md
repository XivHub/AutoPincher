# AutoPincher

A lightweight, fully-local Dalamud plugin that undercuts your FFXIV retainer
market listings. It reads each listing straight from game memory, uses the
in-game market board's **Compare Prices** to find the current cheapest
competitor, and lowers your asking price to **1 gil below it** (holding when
your own retainer is already the cheapest, or when the board is empty).

No server, no telemetry, no Universalis upload — nothing leaves your client.
This is the pinch engine from
[FFMarketConnector](https://github.com/edg-l/FFMarketConnector) extracted into a
standalone plugin with all the data-streaming stripped out.

## Usage

- **All retainers:** with AutoRetainer installed, an **Auto Pinch** button appears
  next to its retainer-list controls. Open your retainer list (the bell) and click
  it to walk every retainer with active listings.
- **One retainer:** open a retainer's sell list and run `/autopinch` (or the
  *Pinch open retainer now* button in the config window).
- **Config:** `/autopincher` opens the window — toggle the plugin, and tune the
  per-item delay and the market-board request delay.

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
