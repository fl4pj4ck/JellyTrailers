# JellyTrailers

Jellyfin plugin: download movie and TV trailers with [yt-dlp](https://github.com/yt-dlp/yt-dlp) and place them next to your media. Trailers show on each item’s detail page. For pre-roll (trailer before movie), use Cinema Mode or Intros plugin.

**Configure:** Dashboard → Plugins → JellyTrailers (yt-dlp path, filename, quality, schedule). Uses Jellyfin movie/TV library roots; no separate paths.

See **[Jellyfin.Plugin.JellyTrailers/README.md](Jellyfin.Plugin.JellyTrailers/README.md)** for requirements, Docker/yt-dlp setup, and scheduled task details.

## Install

1. **Dashboard → Plugins → Repositories** → Add: `https://raw.githubusercontent.com/fl4pj4ck/JellyTrailers/main/manifest.json`
2. **Dashboard → Plugins** → Install and enable **JellyTrailers**, then configure.

If the plugin doesn’t appear, remove and re-add the repository (same URL) and check server logs. **Manual install:** build (below), copy the plugin folder into Jellyfin’s `plugins` directory, restart, then enable. [build.sh](build.sh) can build and copy into a Podman container.

## Build

```bash
dotnet build Jellyfin.Plugin.JellyTrailers.sln -c Release
```

## Release

From repo root: `./build.sh -now` — reads the latest version from the last entry in [manifest.json](manifest.json), builds net8.0 and net9.0, creates zips in `releases/`, and creates a GitHub release if it doesn’t exist. Requires `jq` and `gh`. In manifest.json, keep `versions` **oldest first** so the Jellyfin catalog offers the latest version for install.

## License

[LICENSE](LICENSE) (MIT). Built plugin distributed against Jellyfin is GPL-3.0-only.
