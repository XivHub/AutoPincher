# Development

## Build

Requires the .NET SDK and an extracted Dalamud dev bundle. Shared helpers come from
[XivHubPluginKit](https://github.com/XivHub/XivHubPluginKit), linked by source: clone it next to this
repo, so `../XivHubPluginKit` exists.

```bash
DALAMUD_HOME=~/.cache/dalamud-dev DOTNET_ROOT=~/.dotnet \
  dotnet build AutoPincher/AutoPincher.csproj -c Release -p:Platform=x64
```

The build writes `AutoPincher.dll` and a packaged `latest.zip` under `AutoPincher/bin/x64/Release/`.

## Publish

Bump `<Version>` in `AutoPincher/AutoPincher.csproj` so Dalamud sees an update, then run `publish-plugin` from the repo
root. It builds Release and replaces AutoPincher's entry in the XIV Hub `pluginmaster.json`, leaving the
other plugins alone.
