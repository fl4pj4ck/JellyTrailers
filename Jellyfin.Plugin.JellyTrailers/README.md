# Jellyfin.Plugin.JellyTrailers

Jellyfin plugin that downloads movie and TV show trailers with **yt-dlp** and places them next to your media (Option B: full C# implementation, no Python).

## Requirements

- **Jellyfin** server (tested with 10.10.x; net8.0).
- **yt-dlp** installed and on PATH, or leave the path empty to use the plugin-managed copy (auto-downloaded). Configure in Dashboard → Plugins → JellyTrailers.

## Installing yt-dlp inside Docker

### LinuxServer image (`linuxserver/jellyfin`)

Use the [universal-package-install](https://github.com/linuxserver/docker-mods/tree/universal-package-install) mod so the container installs **yt-dlp** at startup via pip:

1. Set the mod and packages in your run/compose:
   - **DOCKER_MODS** = `linuxserver/mods:universal-package-install`
   - **INSTALL_PIP_PACKAGES** = `yt-dlp`

2. **Docker Compose** example:

   ```yaml
   services:
     jellyfin:
       image: lscr.io/linuxserver/jellyfin:latest
       environment:
         - DOCKER_MODS=linuxserver/mods:universal-package-install
         - INSTALL_PIP_PACKAGES=yt-dlp
       # ... rest of your config (volumes, ports, etc.)
   ```

3. **Podman/Docker run** example:

   ```bash
   podman run -d \
     -e DOCKER_MODS=linuxserver/mods:universal-package-install \
     -e INSTALL_PIP_PACKAGES=yt-dlp \
     -v /path/to/config:/config \
     -v /path/to/media:/data \
     -p 8096:8096 \
     lscr.io/linuxserver/jellyfin:latest
   ```

4. Restart the container. After startup, **yt-dlp** will be on PATH; leave the plugin’s “yt-dlp path” empty or set `yt-dlp`.

### Official image (`jellyfin/jellyfin`)

The official image does not support mods. Options:

- **Option A:** Install **yt-dlp** once inside the running container (lost on container recreate unless you use a custom image):
  ```bash
  podman exec -it jellyfin bash -c "apt-get update && apt-get install -y curl && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && chmod +x /usr/local/bin/yt-dlp"
  ```
  (Or use the [static binary](https://github.com/yt-dlp/yt-dlp/releases) and place it in a path that’s in the container’s PATH.)

- **Option B:** Build a custom image that installs **yt-dlp** in the Dockerfile, or use a sidecar/separate container and set the plugin “yt-dlp path” to a path reachable from the Jellyfin container (e.g. bind-mount).

## Build

```bash
# From repo root; requires .NET 8 SDK
dotnet build Jellyfin.Plugin.JellyTrailers.sln -c Release
```

Output: `Jellyfin.Plugin.JellyTrailers/bin/Release/net8.0/`

## Install

1. Copy the plugin folder (e.g. `Jellyfin.Plugin.JellyTrailers`) including the DLL and dependencies into your Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/` or `$HOME/.local/share/jellyfin/plugins/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\`
2. Restart Jellyfin.
3. Enable the plugin in Dashboard → Plugins.
4. Configure in Dashboard → Plugins → JellyTrailers (yt-dlp path, trailer path, quality, delay, max per run).
5. The scheduled task **Download Trailers (JellyTrailers)** appears in Dashboard → Scheduled Tasks; set the schedule (default: daily at 02:00).

## Behavior

- **Libraries:** Uses Jellyfin library roots (movies and TV show libraries). No separate config for paths.
- **Scan:** Same logic as the standalone Python app: movies = direct subdirs of each movie library root; TV = per-season folders (ShowName/S01) or per-show folders.
- **Scan:** Each run rescans library roots; no persistent list. Entries needing a trailer are ordered by folder modification time (newest first).
- **Trailers:** Missing trailers are downloaded via yt-dlp (`ytsearch1:{title} {year} trailer` for movies, `{title} season {n} trailer` for TV). File is written to `trailer_path` (default `trailer.mp4`) next to each folder.
- **Refresh:** After each run, a library scan is queued so Jellyfin picks up the new files.

## Configuration

- **YtDlpPath:** Path to yt-dlp (e.g. `yt-dlp` or `/usr/bin/yt-dlp`). Empty = use `yt-dlp` from PATH.
- **TrailerPath:** Relative path under each media folder (e.g. `trailer.mp4` or `Trailer/trailer.mp4`).
- **Quality:** `best`, `1080p`, `720p`, `480p`.
- **DelaySeconds / RetryDelaySeconds:** Delay between downloads; delay before retry on failure.
- **MaxTrailersPerRun:** Cap per run (0 = no limit).
- **YtDlpOptionsJson:** Optional extra yt-dlp options as JSON.

## License

Plugin (C#) is distributed under GPL-3.0-only when built against Jellyfin packages. Standalone Python project in this repo remains MIT.
