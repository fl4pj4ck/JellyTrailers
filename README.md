# JellyTrailers

<img width="512" height="512" alt="image" src="https://github.com/user-attachments/assets/45284c89-3a34-4c64-b207-1f69502293ef" />

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

1. In Jellyfin go to **Dashboard → Plugins → Repositories** and add this manifest URL (use the **jsDelivr** one if the plugin never appears):
   - **https://cdn.jsdelivr.net/gh/fl4pj4ck/JellyTrailers@main/manifest.json**
   - Or: **https://raw.githubusercontent.com/fl4pj4ck/JellyTrailers/main/manifest.json**
2. Open **Dashboard → Plugins**, find **JellyTrailers** in the catalog, install it, then **Enable** the plugin.
3. **Configure** in **Dashboard → Plugins → JellyTrailers** (yt-dlp path, trailer filename, quality, etc.).

**Plugin not showing in the catalog?** Try: (1) Use the jsDelivr URL above—Jellyfin can fail with GitHub raw redirects. (2) Remove the repository and add it again, then refresh the Plugins page. (3) Check Jellyfin logs for errors about "manifest" or "plugin repository". (4) Under **Dashboard → Plugins**, look in the catalog list (not only "My Plugins").

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

2. **Build and release** from the repo root:
   ```bash
   ./build.sh -now
   ```
   This reads the version from the **last entry** in [manifest.json](manifest.json) `versions`, builds net8.0 + net9.0, creates zips in `releases/`, and **creates a GitHub release** for that version if it doesn’t exist yet (tag and release notes from manifest). Requires `jq` and `gh` (GitHub CLI). If the release already exists, it only rebuilds the zips.

   Or create the release manually: run the build/zip step, then Repo → **Releases** → **Create a new release**, use the tag (e.g. **v1.0.1**), attach the zips from `releases/`.

After that, the manifest URL in the README will point to a working catalog and the version’s `sourceUrl` will download this zip. If you add a new version, add an entry to `manifest.json` and publish a new release with a matching zip name.

## Repo layout

- `Jellyfin.Plugin.JellyTrailers/` – plugin source (C#).
- `assets/` – plugin tile/logo image; catalog manifest `imageUrl` points here (public URL).
- `releases/` – release zips from `./build.sh -now` (zips are gitignored).
- `build.sh` – build and optionally copy into a Podman container (`-remove` to uninstall only).
- `build.yaml` – plugin metadata for catalog/release tooling.

## License

- Source: [LICENSE](LICENSE) (MIT).
- The built plugin, when distributed against Jellyfin packages, is GPL-3.0-only.
