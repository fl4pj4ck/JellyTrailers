#!/usr/bin/env bash
# Build Jellyfin.Plugin.JellyTrailers and copy into a Podman Jellyfin container.
#
# Where Jellyfin keeps plugins (data dir + /plugins):
#   - Official (jellyfin/jellyfin): JELLYFIN_DATA_DIR=/config → /config/plugins/
#   - LinuxServer (linuxserver/jellyfin): JELLYFIN_DATA_DIR=/config/data → /config/data/plugins/
#   - Custom: JELLYFIN_DATA_DIR or --datadir can be /data, /var/lib/jellyfin, etc.
#   Detection order: JELLYFIN_PLUGINS_PATH env → container JELLYFIN_DATA_DIR → process --datadir
#   → known paths (/config/data/plugins, /config/plugins, /data/plugins, /var/lib/jellyfin/plugins)
#   → default /config/plugins. Override with JELLYFIN_PLUGINS_PATH if needed.
#
# Usage:
#   ./build.sh [-remove | -now] [CONTAINER]
#
#   -remove   Remove both Jellyfin.Plugin.Trailers and Jellyfin.Plugin.JellyTrailers from
#             the container's plugins folder and exit (no build, no install).
#   -now      Build the plugin, create zips in releases/, and create a GitHub release if the
#             version (from manifest.json first .versions[] entry, latest-first) does not yet have a release.
#             Requires: jq, gh (GitHub CLI). No container copy.
#   CONTAINER: Podman container name or ID (default: $JELLYFIN_CONTAINER or "jellyfin")
#
#   Optional env:
#     JELLYFIN_PLUGINS_PATH  Plugins dir inside container (e.g. /config/plugins or /data/plugins)
#     JELLYFIN_BASE_URL     Base URL for reachability check (default http://localhost:8096)
#
# Restart the container after copying for the plugin to load.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

REMOVE=0
NOW=0
if [[ "${1:-}" == "-remove" ]]; then
  REMOVE=1
  shift
elif [[ "${1:-}" == "-now" ]]; then
  NOW=1
  shift
fi
CONTAINER="${1:-${JELLYFIN_CONTAINER:-jellyfin}}"
PLUGIN_NAME="Jellyfin.Plugin.JellyTrailers"
OLD_PLUGIN_NAME="Jellyfin.Plugin.Trailers"
BUILD_DIR="Jellyfin.Plugin.JellyTrailers/bin/Release"
DOTNET_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"

# --- Prerequisites: dotnet (8.0 + 9.0) and podman ---
# Uses DOTNET_DIR for installs so we have both SDKs regardless of system dotnet.
ensure_dotnet() {
  local need_install=0
  local dotnet_exe="$DOTNET_DIR/dotnet"

  # Prefer dotnet from DOTNET_DIR so we use a consistent install (with both SDKs)
  if [[ -x "$dotnet_exe" ]]; then
    export PATH="$DOTNET_DIR:$PATH"
    local sdks
    sdks=$("$dotnet_exe" --list-sdks 2>/dev/null || true)
    if echo "$sdks" | grep -qE '^8\.' && echo "$sdks" | grep -qE '^9\.'; then
      return 0
    fi
    need_install=1
  else
    # No install yet, or system dotnet: check if system has both 8 and 9
    export PATH="$DOTNET_DIR:$PATH"
    local sdks
    sdks=$(dotnet --list-sdks 2>/dev/null || true)
    if command -v dotnet &>/dev/null && echo "$sdks" | grep -qE '^8\.' && echo "$sdks" | grep -qE '^9\.'; then
      return 0
    fi
    need_install=1
  fi

  if [[ "$need_install" -eq 0 ]]; then return 0; fi

  echo " .NET 8 and/or 9 SDK not found; downloading and installing to $DOTNET_DIR ..."
  if ! command -v curl &>/dev/null; then
    echo "Error: curl required to install .NET SDK. Install curl or install .NET 8+9 SDK manually." >&2
    exit 1
  fi
  mkdir -p "$DOTNET_DIR"

  has_sdk() { [[ -x "$dotnet_exe" ]] && "$dotnet_exe" --list-sdks 2>/dev/null | grep -qE "^$1\."; }
  if ! has_sdk 8; then
    echo " Installing .NET 8 SDK..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0 --install-dir "$DOTNET_DIR" --no-path
  fi
  export PATH="$DOTNET_DIR:$PATH"

  if ! has_sdk 9; then
    echo " Installing .NET 9 SDK..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0 --install-dir "$DOTNET_DIR" --no-path
  fi
  export PATH="$DOTNET_DIR:$PATH"

  if [[ ! -x "$dotnet_exe" ]]; then
    echo "Error: dotnet not available after install at $dotnet_exe" >&2
    exit 1
  fi
  local sdks_after
  sdks_after=$("$dotnet_exe" --list-sdks 2>/dev/null || true)
  if ! echo "$sdks_after" | grep -qE '^8\.'; then
    echo "Error: .NET 8 SDK still not available after install." >&2
    exit 1
  fi
  if ! echo "$sdks_after" | grep -qE '^9\.'; then
    echo "Error: .NET 9 SDK still not available after install. Install manually or check network." >&2
    exit 1
  fi
}

ensure_podman() {
  if command -v podman &>/dev/null; then
    return 0
  fi
  echo " podman not found; attempting install (may need sudo)..."
  if command -v apt-get &>/dev/null; then
    sudo apt-get update -qq && sudo apt-get install -y podman
  elif command -v dnf &>/dev/null; then
    sudo dnf install -y podman
  elif command -v pacman &>/dev/null; then
    sudo pacman -S --noconfirm podman
  else
    echo "Error: podman not found. Install it (e.g. apt install podman, dnf install podman)." >&2
    exit 1
  fi
}

# --- MD5 checksum (portable: md5sum, openssl, md5) ---
get_md5() {
  local f="$1"
  if [[ ! -f "$f" ]]; then echo ""; return; fi
  if command -v md5sum &>/dev/null; then
    md5sum "$f" | cut -d' ' -f1
  elif command -v openssl &>/dev/null; then
    openssl dgst -md5 -r "$f" 2>/dev/null | cut -d' ' -f1
  elif command -v md5 &>/dev/null; then
    md5 -r "$f" 2>/dev/null | awk '{print $1}'
  else
    echo ""
  fi
}

# --- Update manifest.json checksums for current version (10.10 = zip1, 10.11/10.12 = zip2) ---
update_manifest_checksums() {
  local manifest="$1" pv="$2" c10="$3" c12="$4"
  [[ ! -f "$manifest" || -z "$pv" ]] && return
  if ! command -v jq &>/dev/null; then return; fi
  jq --arg pv "$pv" --arg c10 "${c10:-}" --arg c12 "${c12:-}" \
    '.[0].versions |= map(
      if .version == $pv then
        if .targetAbi == "10.10.0.0" then .checksum = $c10 else .checksum = $c12 end
      else . end
    )' "$manifest" > "${manifest}.tmp" && mv "${manifest}.tmp" "$manifest"
}

# --- Sort manifest.json .versions from latest down (by version number descending) ---
sort_manifest_versions() {
  local manifest="$1"
  [[ ! -f "$manifest" ]] && return
  if ! command -v jq &>/dev/null; then return; fi
  jq '.[0].versions |= (sort_by(.version | split(".") | map(tonumber? // 0)) | reverse)' "$manifest" > "${manifest}.tmp" && mv "${manifest}.tmp" "$manifest"
}

echo "Checking prerequisites..."
ensure_dotnet
# Use installed dotnet (DOTNET_DIR) for all builds so both 8 and 9 are available
export PATH="$DOTNET_DIR:$PATH"
if [[ "$NOW" -eq 0 ]]; then
  ensure_podman
fi
echo "Prerequisites OK (dotnet: $(dotnet --version)$( [[ "$NOW" -eq 0 ]] && echo ", podman: $(podman --version 2>/dev/null || echo 'ok')" ))."

# --- -now: build and create release zips (net8.0 + net9.0 for 10.10 and 10.12) ---
if [[ "$NOW" -eq 1 ]]; then
  MANIFEST_JSON="$SCRIPT_DIR/manifest.json"
  if [[ ! -f "$MANIFEST_JSON" ]]; then
    echo "Error: manifest.json not found at $MANIFEST_JSON" >&2
    exit 1
  fi
  sort_manifest_versions "$MANIFEST_JSON"
  # Manifest sorted latest-first; latest = first entry
  PLUGIN_VERSION=$(jq -r '.[0].versions[0].version' "$MANIFEST_JSON" 2>/dev/null)
  if [[ -z "$PLUGIN_VERSION" || "$PLUGIN_VERSION" == "null" ]]; then
    echo "Error: Could not read version from manifest.json (first entry = latest in .versions). Install jq or fix manifest." >&2
    exit 1
  fi
  # Git tag: 1.0.1.0 -> v1.0.1 (drop trailing .0)
  RELEASE_TAG="v${PLUGIN_VERSION%.0}"

  echo "Building $PLUGIN_NAME for release (net8.0 + net9.0) — version $PLUGIN_VERSION from manifest..."
  # Build each framework separately so restore/assets work for both (avoids NETSDK1005 on net9.0)
  dotnet build Jellyfin.Plugin.JellyTrailers/Jellyfin.Plugin.JellyTrailers.csproj -p:Configuration=Release -v q -f net8.0
  dotnet build Jellyfin.Plugin.JellyTrailers/Jellyfin.Plugin.JellyTrailers.csproj -p:Configuration=Release -v q -f net9.0
  OUT_NET8="$BUILD_DIR/net8.0"
  OUT_NET9="$BUILD_DIR/net9.0"
  for dir in "$OUT_NET8" "$OUT_NET9"; do
    if [[ ! -f "$dir/$PLUGIN_NAME.dll" ]]; then
      echo "Error: Build output not found: $dir/$PLUGIN_NAME.dll" >&2
      exit 1
    fi
  done
  RELEASES_DIR="$SCRIPT_DIR/releases"
  mkdir -p "$RELEASES_DIR"
  ZIP_10_10="JellyTrailers_${PLUGIN_VERSION}.zip"
  ZIP_10_12="JellyTrailers_${PLUGIN_VERSION}_10.12.zip"
  ( cd "$OUT_NET8" && zip -r "$RELEASES_DIR/$ZIP_10_10" ${PLUGIN_NAME}.* -q )
  ( cd "$OUT_NET9" && zip -r "$RELEASES_DIR/$ZIP_10_12" ${PLUGIN_NAME}.* -q )
  echo "Created: $RELEASES_DIR/$ZIP_10_10 (10.10.x)"
  echo "Created: $RELEASES_DIR/$ZIP_10_12 (10.11/10.12)"
  CHECKSUM_10_10=$(get_md5 "$RELEASES_DIR/$ZIP_10_10")
  CHECKSUM_10_12=$(get_md5 "$RELEASES_DIR/$ZIP_10_12")
  update_manifest_checksums "$MANIFEST_JSON" "$PLUGIN_VERSION" "$CHECKSUM_10_10" "$CHECKSUM_10_12"
  sort_manifest_versions "$MANIFEST_JSON"
  if [[ -n "$CHECKSUM_10_10" || -n "$CHECKSUM_10_12" ]]; then
    echo "Updated manifest.json checksums for $PLUGIN_VERSION"
  fi

  # Ensure manifest has multiple versions so catalog/CDN (e.g. jsDelivr) offers more than 1.0.0.0
  VERSION_ENTRIES=$(jq -r '.[0].versions | length' "$MANIFEST_JSON" 2>/dev/null || echo "0")
  if [[ -n "$VERSION_ENTRIES" && "$VERSION_ENTRIES" != "null" && "$VERSION_ENTRIES" -le 3 ]]; then
    echo "Error: manifest.json has only $VERSION_ENTRIES version entry/entries. Jellyfin catalog and CDN will only offer one version." >&2
    echo "Add all version entries to manifest.json, then re-run. Manifest is sorted latest-first." >&2
    exit 1
  fi

  # Commit and push before creating release (zips are gitignored)
  # Use "Release" only when we will create a new release; else "Update manifest checksums"
  RELEASE_EXISTS=0
  if command -v gh &>/dev/null && gh release view "$RELEASE_TAG" &>/dev/null; then
    RELEASE_EXISTS=1
  fi
  git add .
  if ! git diff --staged --quiet 2>/dev/null; then
    if [[ "$RELEASE_EXISTS" -eq 1 ]]; then
      git commit -m "Update manifest checksums for $RELEASE_TAG"
    else
      git commit -m "Release $RELEASE_TAG"
    fi
  fi
  git push

  # Create GitHub release if this version does not have one yet (requires gh)
  if command -v gh &>/dev/null; then
    if [[ "$RELEASE_EXISTS" -eq 1 ]]; then
      echo "Release $RELEASE_TAG already exists; zips are in releases/. Up version in manifest.json and re-run to publish a new release."
    else
      RELEASE_NOTES=$(jq -r '.[0].versions[0].changelog' "$MANIFEST_JSON" 2>/dev/null)
      [[ -z "$RELEASE_NOTES" || "$RELEASE_NOTES" == "null" ]] && RELEASE_NOTES="Release $RELEASE_TAG"
      echo "Creating GitHub release $RELEASE_TAG..."
      # Use --notes-file so special chars (quotes, etc.) in changelog don't break the shell
      gh release create "$RELEASE_TAG" \
        "$RELEASES_DIR/$ZIP_10_10" \
        "$RELEASES_DIR/$ZIP_10_12" \
        --title "$RELEASE_TAG" \
        --notes-file <(printf '%s' "$RELEASE_NOTES")
      echo "Done. Release: $(gh release view "$RELEASE_TAG" --json url -q .url 2>/dev/null || echo "$RELEASE_TAG")"
    fi
  else
    echo "Install gh (GitHub CLI) and re-run to create release $RELEASE_TAG automatically. Or create it manually and attach the zips from releases/."
  fi
  exit 0
fi

# --- Checks ---
if ! podman container exists "$CONTAINER" 2>/dev/null; then
  echo "Error: Container '$CONTAINER' not found. List containers: podman ps -a" >&2
  exit 1
fi

# Resolve plugin directory so all Jellyfin images and data folders are supported.
if [[ -n "${JELLYFIN_PLUGINS_PATH:-}" ]]; then
  CONTAINER_PLUGINS_PATH="$JELLYFIN_PLUGINS_PATH"
  echo "Using JELLYFIN_PLUGINS_PATH: $CONTAINER_PLUGINS_PATH"
else
  CONTAINER_PLUGINS_PATH=""
  # 1) Container env JELLYFIN_DATA_DIR (from Dockerfile or run -e)
  JF_DATADIR_INSPECT=$(podman inspect "$CONTAINER" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null | grep -E '^JELLYFIN_DATA_DIR=' | head -1 | cut -d= -f2-)
  if [[ -n "$JF_DATADIR_INSPECT" ]]; then
    CONTAINER_PLUGINS_PATH="$JF_DATADIR_INSPECT/plugins"
    echo "Using container JELLYFIN_DATA_DIR: $JF_DATADIR_INSPECT → $CONTAINER_PLUGINS_PATH"
  fi
  # 2) Jellyfin server process --datadir (skip s6/svc; match jellyfin.dll or /jellyfin/jellyfin)
  if [[ -z "$CONTAINER_PLUGINS_PATH" ]]; then
    JF_DATADIR_PROC=$(podman exec "$CONTAINER" sh -c '
      for f in /proc/*/cmdline; do
        [ -r "$f" ] || continue
        args=$(tr "\0" "\n" < "$f" 2>/dev/null)
        echo "$args" | grep -qE "(jellyfin\.dll|/jellyfin/jellyfin|/usr/lib/jellyfin)" || continue
        echo "$args" | grep -q "^--datadir$" || continue
        datadir=$(echo "$args" | grep -A1 "^--datadir$" 2>/dev/null | tail -1)
        [ -n "$datadir" ] && echo "$datadir" && break
      done
    ' 2>/dev/null | head -1)
    if [[ -n "$JF_DATADIR_PROC" ]]; then
      CONTAINER_PLUGINS_PATH="$JF_DATADIR_PROC/plugins"
      echo "Using Jellyfin process --datadir: $JF_DATADIR_PROC → $CONTAINER_PLUGINS_PATH"
    fi
  fi
  # 3) Known plugin dirs — first where dir or its parent (data dir) exists
  if [[ -z "$CONTAINER_PLUGINS_PATH" ]]; then
    for candidate in /config/data/plugins /config/plugins /data/plugins /var/lib/jellyfin/plugins; do
      parent="${candidate%/plugins}"
      if podman exec "$CONTAINER" test -d "$candidate" 2>/dev/null || podman exec "$CONTAINER" test -d "$parent" 2>/dev/null; then
        CONTAINER_PLUGINS_PATH="$candidate"
        echo "Using plugin path: $CONTAINER_PLUGINS_PATH"
        break
      fi
    done
  fi
  # 4) Container shell JELLYFIN_DATA_DIR (default env in exec)
  if [[ -z "$CONTAINER_PLUGINS_PATH" ]]; then
    JF_DATADIR_SHELL=$(podman exec "$CONTAINER" sh -c 'echo "${JELLYFIN_DATA_DIR:-}"' 2>/dev/null || true)
    if [[ -n "$JF_DATADIR_SHELL" ]]; then
      CONTAINER_PLUGINS_PATH="$JF_DATADIR_SHELL/plugins"
      echo "Using exec JELLYFIN_DATA_DIR: $JF_DATADIR_SHELL → $CONTAINER_PLUGINS_PATH"
    fi
  fi
  # 5) Default (official image)
  CONTAINER_PLUGINS_PATH="${CONTAINER_PLUGINS_PATH:-/config/plugins}"
  if [[ "$CONTAINER_PLUGINS_PATH" == "/config/plugins" ]]; then
    echo "Using default plugin path: $CONTAINER_PLUGINS_PATH"
  fi
fi

# --- Remove only (if -remove): wipe plugin dirs, plugin manifest, config/data, then restart container ---
if [[ "$REMOVE" -eq 1 ]]; then
  echo "Removing plugin from container (Trailers + JellyTrailers)..."
  for dir in "$OLD_PLUGIN_NAME" "$PLUGIN_NAME"; do
    if podman exec "$CONTAINER" test -f "$CONTAINER_PLUGINS_PATH/$dir/manifest.json" 2>/dev/null; then
      podman exec "$CONTAINER" rm -f "$CONTAINER_PLUGINS_PATH/$dir/manifest.json"
      echo "  Removed $CONTAINER_PLUGINS_PATH/$dir/manifest.json"
    fi
    if podman exec "$CONTAINER" test -d "$CONTAINER_PLUGINS_PATH/$dir" 2>/dev/null; then
      podman exec "$CONTAINER" rm -rf "$CONTAINER_PLUGINS_PATH/$dir"
      echo "  Removed $CONTAINER_PLUGINS_PATH/$dir"
    fi
  done
  # Root-level plugin manifest (some installs leave a manifest at plugins root)
  if podman exec "$CONTAINER" test -f "$CONTAINER_PLUGINS_PATH/manifest.json" 2>/dev/null; then
    podman exec "$CONTAINER" rm -f "$CONTAINER_PLUGINS_PATH/manifest.json"
    echo "  Removed $CONTAINER_PLUGINS_PATH/manifest.json"
  fi
  # Plugin config/data (stats.json, yt-dlp cache, etc.) lives under plugins/configurations/PluginName
  PLUGIN_CONFIG_DIR="$CONTAINER_PLUGINS_PATH/configurations/JellyTrailers"
  if podman exec "$CONTAINER" test -d "$PLUGIN_CONFIG_DIR" 2>/dev/null; then
    podman exec "$CONTAINER" rm -rf "$PLUGIN_CONFIG_DIR"
    echo "  Removed $PLUGIN_CONFIG_DIR"
  fi
  # Plugin configuration XML (Jellyfin may store per-plugin config by assembly name)
  for xml in "Jellyfin.Plugin.JellyTrailers.xml" "JellyTrailers.xml"; do
    if podman exec "$CONTAINER" test -f "$CONTAINER_PLUGINS_PATH/configurations/$xml" 2>/dev/null; then
      podman exec "$CONTAINER" rm -f "$CONTAINER_PLUGINS_PATH/configurations/$xml"
      echo "  Removed $CONTAINER_PLUGINS_PATH/configurations/$xml"
    fi
  done
  echo "Restarting container so Jellyfin drops the plugin from its list (allows clean reinstall)..."
  if podman restart "$CONTAINER" 2>/dev/null; then
    echo "Container restarted."
  else
    echo "Restart failed or container not running. Restart manually: podman restart $CONTAINER"
  fi
  echo "Done. No build or install."
  exit 0
fi

# Detect Jellyfin version in container (10.10 / 10.11 / 10.12); default 10.10 when unknown
# Try official path, then LinuxServer/Ubuntu paths, then jellyfin from PATH
JF_VER_RAW=$(podman exec "$CONTAINER" sh -c '
  for exe in /jellyfin/jellyfin /usr/lib/jellyfin/jellyfin /usr/bin/jellyfin jellyfin; do
    if out=$("$exe" --version 2>/dev/null); then
      echo "$out"
      break
    fi
  done
' 2>/dev/null | tr -d '\r' | head -1)
JF_VER_RAW="${JF_VER_RAW:-}"
if [[ "$JF_VER_RAW" =~ 10\.10 ]]; then
  JF_MAJOR_MINOR=10.10
elif [[ "$JF_VER_RAW" =~ 10\.11 ]]; then
  JF_MAJOR_MINOR=10.11
elif [[ "$JF_VER_RAW" =~ 10\.12 ]]; then
  JF_MAJOR_MINOR=10.12
else
  JF_MAJOR_MINOR=10.10
fi
echo "Jellyfin version in container: ${JF_VER_RAW:-unknown} (using build for ${JF_MAJOR_MINOR}.x)"

# --- Build ---
echo "Building $PLUGIN_NAME (net8.0 for 10.10, net9.0 for 10.11/10.12)..."
# Remove obj and bin so project.assets.json is regenerated for all TargetFrameworks (avoids NETSDK1005)
rm -rf "$SCRIPT_DIR/Jellyfin.Plugin.JellyTrailers/obj" "$SCRIPT_DIR/Jellyfin.Plugin.JellyTrailers/bin"
dotnet restore "$SCRIPT_DIR/Jellyfin.Plugin.JellyTrailers/Jellyfin.Plugin.JellyTrailers.csproj" -p:Configuration=Release -v q
BUILD_FULL_SUCCESS=0
# Build all targets; if net9.0 not supported by SDK or build error, build net8.0 only
if dotnet build "$SCRIPT_DIR/Jellyfin.Plugin.JellyTrailers/Jellyfin.Plugin.JellyTrailers.csproj" -p:Configuration=Release -v q 2>/dev/null; then
  BUILD_FULL_SUCCESS=1
else
  echo "Full build failed. Building net8.0 only..."
  dotnet build Jellyfin.Plugin.JellyTrailers/Jellyfin.Plugin.JellyTrailers.csproj -p:Configuration=Release -v q -f net8.0
fi

# Pick output dir: 10.10 → net8.0; 10.11/10.12 → net9.0 only (required)
if [[ "$JF_MAJOR_MINOR" == "10.10" ]]; then
  OUTPUT_DIR="$BUILD_DIR/net8.0"
else
  if [[ -f "$BUILD_DIR/net9.0/$PLUGIN_NAME.dll" ]]; then
    OUTPUT_DIR="$BUILD_DIR/net9.0"
  else
    echo "Error: Jellyfin ${JF_MAJOR_MINOR} requires a net9.0 build, but net9.0 was not built (full build failed)." >&2
    echo "Install .NET 9 SDK and re-run, or use a Jellyfin 10.10 container (uses net8.0)." >&2
    exit 1
  fi
fi

if [[ ! -f "$OUTPUT_DIR/$PLUGIN_NAME.dll" ]]; then
  echo "Error: Build output not found: $OUTPUT_DIR/$PLUGIN_NAME.dll" >&2
  exit 1
fi

# --- Zips into releases/ (version from manifest) ---
RELEASES_DIR="$SCRIPT_DIR/releases"
MANIFEST_JSON="$SCRIPT_DIR/manifest.json"
sort_manifest_versions "$MANIFEST_JSON"
# Manifest sorted latest-first; latest = first entry
PLUGIN_VERSION=$(jq -r '.[0].versions[0].version' "$MANIFEST_JSON" 2>/dev/null)
if [[ -n "$PLUGIN_VERSION" && "$PLUGIN_VERSION" != "null" ]]; then
  mkdir -p "$RELEASES_DIR"
  if [[ -f "$BUILD_DIR/net8.0/$PLUGIN_NAME.dll" ]]; then
    ZIP_10_10="JellyTrailers_${PLUGIN_VERSION}.zip"
    ( cd "$BUILD_DIR/net8.0" && zip -r "$RELEASES_DIR/$ZIP_10_10" ${PLUGIN_NAME}.* -q )
    echo "Created: $RELEASES_DIR/$ZIP_10_10"
    CHECKSUM_10_10=$(get_md5 "$RELEASES_DIR/$ZIP_10_10")
  else
    CHECKSUM_10_10=""
  fi
  if [[ -f "$BUILD_DIR/net9.0/$PLUGIN_NAME.dll" ]]; then
    ZIP_10_12="JellyTrailers_${PLUGIN_VERSION}_10.12.zip"
    ( cd "$BUILD_DIR/net9.0" && zip -r "$RELEASES_DIR/$ZIP_10_12" ${PLUGIN_NAME}.* -q )
    echo "Created: $RELEASES_DIR/$ZIP_10_12"
    CHECKSUM_10_12=$(get_md5 "$RELEASES_DIR/$ZIP_10_12")
  else
    CHECKSUM_10_12=""
  fi
  update_manifest_checksums "$MANIFEST_JSON" "$PLUGIN_VERSION" "$CHECKSUM_10_10" "$CHECKSUM_10_12"
  sort_manifest_versions "$MANIFEST_JSON"
else
  echo "Skipping release zips (could not read version from manifest.json; need jq?)."
fi

# --- Copy into container ---
# Remove previous/legacy plugin dirs so we don't mix old files with the new build.
echo "Removing previous plugin from container (Trailers + JellyTrailers)..."
for dir in "$OLD_PLUGIN_NAME" "$PLUGIN_NAME"; do
  if podman exec "$CONTAINER" test -d "$CONTAINER_PLUGINS_PATH/$dir" 2>/dev/null; then
    podman exec "$CONTAINER" rm -rf "$CONTAINER_PLUGINS_PATH/$dir"
    echo "  Removed $CONTAINER_PLUGINS_PATH/$dir"
  fi
done
echo "Copying plugin into container '$CONTAINER' at $CONTAINER_PLUGINS_PATH/$PLUGIN_NAME/ ..."
podman exec "$CONTAINER" mkdir -p "$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME"
podman cp "$OUTPUT_DIR/." "$CONTAINER:$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME/"

# Fix ownership: podman cp often leaves files root-owned; Jellyfin usually runs as non-root.
# Match ownership to /config so the Jellyfin process can read the plugin.
CONFIG_OWNER=$(podman exec "$CONTAINER" stat -c '%u:%g' /config 2>/dev/null || true)
if [[ -n "$CONFIG_OWNER" ]]; then
  if podman exec "$CONTAINER" chown -R "$CONFIG_OWNER" "$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME" 2>/dev/null; then
    echo "Set plugin dir ownership to $CONFIG_OWNER (same as /config)."
  else
    echo "Could not chown (container may run as non-root); making plugin readable by all..."
    podman exec "$CONTAINER" chmod -R a+rX "$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME" 2>/dev/null || true
  fi
else
  podman exec "$CONTAINER" chmod -R a+rX "$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME" 2>/dev/null || true
fi

# Verify DLL is present in container
if ! podman exec "$CONTAINER" test -f "$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME/$PLUGIN_NAME.dll" 2>/dev/null; then
  echo "Error: Plugin DLL not found in container after copy. Check path: $CONTAINER_PLUGINS_PATH/$PLUGIN_NAME/" >&2
  exit 1
fi
echo "Plugin files in container: $CONTAINER_PLUGINS_PATH/$PLUGIN_NAME/"
podman exec "$CONTAINER" ls -la "$CONTAINER_PLUGINS_PATH/$PLUGIN_NAME/" 2>/dev/null || true

if [[ "$BUILD_FULL_SUCCESS" -eq 1 ]]; then
  echo "Restarting container '$CONTAINER' so Jellyfin loads the plugin..."
  if ! podman restart "$CONTAINER" 2>/dev/null; then
    for wait in 3 5 8 12 20; do
      echo "Restart failed (port may still be in use). Waiting ${wait}s, then retrying start..."
      sleep "$wait"
      if podman start "$CONTAINER" 2>/dev/null; then
        echo "Container started after retry."
        break
      fi
    done
    if ! podman ps --filter "name=$CONTAINER" --filter "status=running" -q | grep -q .; then
      echo "Could not start container. Plugin is installed; start manually: podman start $CONTAINER" >&2
      exit 1
    fi
  fi
  echo "Waiting for Jellyfin to be reachable..."
  JF_URL="${JELLYFIN_BASE_URL:-http://localhost:8096}"
  max_attempts=60
  attempt=0
  while [[ $attempt -lt $max_attempts ]]; do
    if curl -sS -f -o /dev/null "${JF_URL}/health" 2>/dev/null || podman exec "$CONTAINER" curl -sS -f -o /dev/null "http://localhost:8096/health" 2>/dev/null; then
      echo "Jellyfin is up. Plugin should appear in Dashboard → Plugins."
      break
    fi
    attempt=$((attempt + 1))
    sleep 2
  done
  if [[ $attempt -ge $max_attempts ]]; then
    echo "Timed out waiting for Jellyfin (tried ${max_attempts}×). Plugin is installed; check Dashboard → Plugins once Jellyfin has started."
  fi
else
  echo "Build was partial (full build failed). Plugin copied; restart container manually to load: podman restart $CONTAINER"
fi
