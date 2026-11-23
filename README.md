# AutoPincher

Reprices everything your retainers are selling to 1 gil under the cheapest other seller, in one
click.

> **Warning:** this drives the game's menus for you, which is automation under the FFXIV User
> Agreement. Use it at your own risk.

## How it works

For each listing, AutoPincher opens the market board's Compare Prices, finds the cheapest seller who
isn't you, and sets your price 1 gil below.

It follows a few rules so you don't lose money:

- Your other retainers and housing mannequins don't count as competitors, so your own listings
  never undercut each other.
- With nobody else selling, the price goes up to the last sale price instead of down. You can tell
  it to skip those instead.
- It never prices below what a vendor would pay you.
- If the market board doesn't answer in time, the price stays as it was.

Everything runs on your PC. Nothing is sent anywhere unless you turn on the developer log in
settings and give it your own address.

## How to use it

- All retainers: with AutoRetainer installed, open your retainer list at a bell and press the
  Auto Pinch button next to it.
- One retainer: open its sell list and type `/autopinch`.

Leave the keyboard and mouse alone while it runs, since it is clicking through the game's menus.

AutoRetainer is optional. If you have it, its own automation pauses while AutoPincher runs.

## Install

In `/xlsettings`, under Experimental, add this to Custom Plugin Repositories:

```
https://plugins.xivhub.net/pluginmaster.json
```

Then install AutoPincher from the plugin installer.

## Commands

| Command | What it does |
|---|---|
| `/autopinch` | Reprice the retainer whose sell list is open |
| `/autopincher` | Open settings: turn it on or off and adjust the delays |

AutoPincher's pricing comes from
[FFMarketConnector](https://github.com/edg-l/FFMarketConnector), shared through
[XivHubPluginKit](https://github.com/XivHub/XivHubPluginKit).

Building from source: see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
