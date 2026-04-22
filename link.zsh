#!/usr/bin/env zsh
set -euo pipefail

# Symlink the plugin build output into Grasshopper's Libraries folder (Rhino 8 on macOS).
#
# Usage:
#   ./link-gh-libraries.zsh
#
# Optional env overrides:
#   GH_LIB_DIR=...           # target Grasshopper Libraries directory
#   BUILD_DIR=...            # directory that contains the built .gha/.deps.json/.dll files
#   CONFIG=Debug|Release     # selects build folder when BUILD_DIR isn't set
#   TFM=net7.0               # target framework folder name when BUILD_DIR isn't set
#

PLUGIN_DIR="${0:A:h}"
CONFIG="${CONFIG:-Debug}"
TFM="${TFM:-net7.0}"

DEFAULT_GH_LIB_DIR="$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries"
GH_LIB_DIR="${GH_LIB_DIR:-$DEFAULT_GH_LIB_DIR}"

BUILD_DIR="${BUILD_DIR:-$PLUGIN_DIR/bin/$CONFIG/$TFM}"

if [[ ! -d "$BUILD_DIR" ]]; then
  print -u2 "Error: BUILD_DIR not found: $BUILD_DIR"
  print -u2 "Hint: run 'dotnet build' first, or set BUILD_DIR explicitly."
  exit 1
fi

# Find the .gha in the build dir (supports renamed assembly output).
GHA_FILES=("$BUILD_DIR"/*.gha(N))
if (( ${#GHA_FILES} == 0 )); then
  print -u2 "Error: No .gha found in: $BUILD_DIR"
  exit 1
fi
if (( ${#GHA_FILES} > 1 )); then
  print -u2 "Error: Multiple .gha files found in: $BUILD_DIR"
  print -u2 "  ${GHA_FILES[@]}"
  print -u2 "Set BUILD_DIR to a folder containing exactly one .gha."
  exit 1
fi

GHA_PATH="$GHA_FILES[1]"
BASE_NAME="${GHA_PATH:t:r}"  # filename without extension

mkdir -p "$GH_LIB_DIR"

link_one() {
  local src="$1"
  local dst="$2"

  if [[ ! -e "$src" ]]; then
    return 0
  fi

  ln -sf "$src" "$dst"
}

# Always link the main .gha
link_one "$GHA_PATH" "$GH_LIB_DIR/${GHA_PATH:t}"

# Link the .deps.json if present (helps runtime resolve NuGet dependencies)
link_one "$BUILD_DIR/$BASE_NAME.deps.json" "$GH_LIB_DIR/$BASE_NAME.deps.json"

# Link all dlls in the output folder (do NOT flatten runtimes/*)
for dll in "$BUILD_DIR"/*.dll(N); do
  link_one "$dll" "$GH_LIB_DIR/${dll:t}"
done

print "Linked plugin from: $BUILD_DIR"
print "Into Grasshopper:  $GH_LIB_DIR"
print "  - ${GHA_PATH:t}"
print "  - $BASE_NAME.deps.json (if present)"
print "  - *.dll (${#${(f)$(ls -1 "$BUILD_DIR"/*.dll 2>/dev/null || true)}:-0} files)"

print "\nNote: Restart Rhino/Grasshopper to reload the plugin."
