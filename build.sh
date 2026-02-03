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
#   ./build.sh [-remove] [CONTAINER]
#
#   -remove   Remove both Jellyfin.Plugin.Trailers and Jellyfin.Plugin.JellyTrailers from
#             the container's plugins folder and exit (no build, no install).
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
if [[ "${1:-}" == "-remove" ]]; then
  REMOVE=1
  shift
fi
CONTAINER="${1:-${JELLYFIN_CONTAINER:-jellyfin}}"
PLUGIN_NAME="Jellyfin.Plugin.JellyTrailers"
OLD_PLUGIN_NAME="Jellyfin.Plugin.Trailers"
BUILD_DIR="Jellyfin.Plugin.JellyTrailers/bin/Release"
DOTNET_DIR="${DOTNET_INSTALL_DIR:-$HOME/.dotnet}"

# --- Prerequisites: dotnet (8.0 + 9.0) and podman ---
ensure_dotnet() {
  export PATH="$DOTNET_DIR:$PATH"
  if command -v dotnet &>/dev/null && dotnet --list-sdks 2>/dev/null | grep -qE '^9\.'; then
    return 0
  fi
  echo " .NET 9 SDK not found; installing .NET 8 and 9 SDKs to $DOTNET_DIR ..."
  if ! command -v curl &>/dev/null; then
    echo "Error: curl required to install .NET SDK. Install curl or install .NET 8+9 SDK manually." >&2
    exit 1
  fi
  mkdir -p "$DOTNET_DIR"
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0 --install-dir "$DOTNET_DIR" --no-path
  export PATH="$DOTNET_DIR:$PATH"
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0 --install-dir "$DOTNET_DIR" --no-path
  export PATH="$DOTNET_DIR:$PATH"
  if ! command -v dotnet &>/dev/null; then
    echo "Error: dotnet not available after install. Add to PATH: export PATH=\"$DOTNET_DIR:\$PATH\"" >&2
    exit 1
  fi
  if ! dotnet --list-sdks 2>/dev/null | grep -qE '^9\.'; then
    echo "Error: .NET 9 SDK still not available. Build will work for Jellyfin 10.10 only." >&2
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

echo "Checking prerequisites..."
ensure_dotnet
ensure_podman
echo "Prerequisites OK (dotnet: $(dotnet --version), podman: $(podman --version 2>/dev/null || echo 'ok'))."

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

# --- Remove only (if -remove): wipe both plugin dirs and exit ---
if [[ "$REMOVE" -eq 1 ]]; then
  echo "Removing plugin dirs from container (Trailers + JellyTrailers)..."
  for dir in "$OLD_PLUGIN_NAME" "$PLUGIN_NAME"; do
    if podman exec "$CONTAINER" test -d "$CONTAINER_PLUGINS_PATH/$dir" 2>/dev/null; then
      podman exec "$CONTAINER" rm -rf "$CONTAINER_PLUGINS_PATH/$dir"
      echo "  Removed $CONTAINER_PLUGINS_PATH/$dir"
    fi
  done
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
BUILD_FULL_SUCCESS=0
# Build all targets; if net9.0 not supported by SDK or build error, build net8.0 only
if dotnet build Jellyfin.Plugin.JellyTrailers.sln -c Release -v q 2>/dev/null; then
  BUILD_FULL_SUCCESS=1
else
  echo "Full build failed. Building net8.0 only..."
  dotnet build Jellyfin.Plugin.JellyTrailers/Jellyfin.Plugin.JellyTrailers.csproj -c Release -v q -p:TargetFrameworks=net8.0
fi

# Pick output dir: 10.10 → net8.0; 10.11/10.12 → net9.0 if present, else net8.0
if [[ "$JF_MAJOR_MINOR" == "10.10" ]]; then
  OUTPUT_DIR="$BUILD_DIR/net8.0"
else
  if [[ -f "$BUILD_DIR/net9.0/$PLUGIN_NAME.dll" ]]; then
    OUTPUT_DIR="$BUILD_DIR/net9.0"
  else
    OUTPUT_DIR="$BUILD_DIR/net8.0"
    if [[ "$JF_MAJOR_MINOR" != "10.10" ]]; then
      echo "Note: net9.0 build not available (install .NET 9 SDK for 10.11/10.12). Using net8.0; plugin may not load on ${JF_MAJOR_MINOR}."
    fi
  fi
fi

if [[ ! -f "$OUTPUT_DIR/$PLUGIN_NAME.dll" ]]; then
  echo "Error: Build output not found: $OUTPUT_DIR/$PLUGIN_NAME.dll" >&2
  exit 1
fi

# --- Copy into container ---
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
