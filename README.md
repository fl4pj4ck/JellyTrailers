# JellyTrailers

Search and download movie and TV show trailers using [yt-dlp](https://github.com/yt-dlp/yt-dlp).

## Jellyfin plugin

This repo contains a **Jellyfin plugin** (C#) that runs inside Jellyfin: it uses your server’s library roots, shells out to **yt-dlp**, and places trailer files next to your media.

- **No config file in this repo.** Configuration is done in Jellyfin: **Dashboard → Plugins → JellyTrailers** (yt-dlp path, trailer filename, quality, delay, max per run). Jellyfin stores plugin config in its data directory (e.g. `plugins/configurations/`).
- **Libraries:** Uses Jellyfin’s movie and TV show libraries; no separate paths config.

See **[Jellyfin.Plugin.JellyTrailers/README.md](Jellyfin.Plugin.JellyTrailers/README.md)** for:

- Requirements (Jellyfin, yt-dlp)
- Installing yt-dlp in Docker (LinuxServer mod, official image)
- Configuration (all settings in the plugin UI)
- Scheduled task **Download Trailers (JellyTrailers)** (default daily at 02:00)

## How to install the plugin in Jellyfin

1. In Jellyfin go to **Dashboard → Plugins → Repositories** and add the plugin manifest (catalog) URL.
2. Open **Dashboard → Plugins**, find **JellyTrailers** in the catalog, install it, then **Enable** the plugin.
3. **Configure** in **Dashboard → Plugins → JellyTrailers** (yt-dlp path, trailer filename, quality, etc.).

To install manually (e.g. without a catalog): build the plugin (see [Build](#build) below), copy the plugin folder into Jellyfin’s plugins directory (e.g. `/var/lib/jellyfin/plugins/` on Linux, `%ProgramData%\Jellyfin\Server\plugins\` on Windows), restart Jellyfin, then enable the plugin in Dashboard → Plugins. You can use [build.sh](build.sh) to build and copy into a Podman Jellyfin container.

## Build

```bash
dotnet build Jellyfin.Plugin.JellyTrailers.sln -c Release
```

## Publishing a release (so the catalog install works)

1. **Build** the plugin:
   ```bash
   dotnet build Jellyfin.Plugin.JellyTrailers.sln -c Release
   ```

2. **Zip** the build output. The zip must be named exactly as in [manifest.json](manifest.json) (e.g. `JellyTrailers_1.0.0.0.zip`). From the repo root:
   ```bash
   cd Jellyfin.Plugin.JellyTrailers/bin/Release/net8.0
   zip -r ../../../../../JellyTrailers_1.0.0.0.zip .
   cd ../../../../..
   ```
   Or on Windows (PowerShell): zip the *contents* of `Jellyfin.Plugin.JellyTrailers\bin\Release\net8.0\` into `JellyTrailers_1.0.0.0.zip`.

3. **Create a GitHub release**: Repo → **Releases** → **Create a new release**. Choose tag **v1.0.0** (create the tag if it doesn’t exist), add release notes, then **Attach** the zip file `JellyTrailers_1.0.0.0.zip`. Publish.

After that, the manifest URL in the README will point to a working catalog and the version’s `sourceUrl` will download this zip. If you add a new version, add an entry to `manifest.json` and publish a new release with a matching zip name.

## Repo layout

- `Jellyfin.Plugin.JellyTrailers/` – plugin source (C#).
- `assets/` – plugin tile/logo image; catalog manifest `imageUrl` points here (public URL).
- `build.sh` – build and optionally copy into a Podman container (`-remove` to uninstall only).
- `build.yaml` – plugin metadata for catalog/release tooling.

## License

- Source: [LICENSE](LICENSE) (MIT).
- The built plugin, when distributed against Jellyfin packages, is GPL-3.0-only.
