# JellyTrailers

Jellyfin plugin: download movie and TV trailers with [yt-dlp](https://github.com/yt-dlp/yt-dlp) and place them next to your media. Trailers show on each item’s detail page. For pre-roll (trailer before movie), use Cinema Mode or Intros plugin.

**Configure:** Dashboard → Plugins → JellyTrailers (yt-dlp path, filename, quality, schedule). Uses Jellyfin movie/TV library roots; no separate paths.

See **[Jellyfin.Plugin.JellyTrailers/README.md](Jellyfin.Plugin.JellyTrailers/README.md)** for requirements, Docker/yt-dlp setup, and scheduled task details.

## Install

1. **Dashboard → Plugins → Repositories** → Add: `https://cdn.jsdelivr.net/gh/fl4pj4ck/JellyTrailers@main/manifest.json`
2. **Dashboard → Plugins** → Install and enable **JellyTrailers**, then configure.

Not in catalog? Use the jsDelivr URL; remove and re-add the repo; check logs. Manual: build (below), copy plugin folder into Jellyfin’s `plugins` directory, restart, enable. [build.sh](build.sh) can build and copy into a Podman container.

## Build

```bash
dotnet build Jellyfin.Plugin.JellyTrailers.sln -c Release
```

## Release

From repo root: `./build.sh -now` (reads version from last entry in [manifest.json](manifest.json), builds net8.0 + net9.0, zips in `releases/`, creates GitHub release if missing). Requires `jq` and `gh`.

## License

[LICENSE](LICENSE) (MIT). Built plugin distributed against Jellyfin is GPL-3.0-only.
