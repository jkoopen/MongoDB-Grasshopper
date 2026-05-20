#!/usr/bin/env zsh
set -euo pipefail

# Packs the Grasshopper plugin (.gha) + dependencies into a shareable zip.
# Usage: ./pack-plugin.zsh            (Release)
#        ./pack-plugin.zsh Debug

config=${1:-Release}

root_dir=$(cd "${0:A:h}" && pwd)
cd "$root_dir"

# Build
if [[ "$config" != "Release" && "$config" != "Debug" ]]; then
  echo "Config must be Release or Debug (got: $config)" >&2
  exit 2
fi

dotnet build -c "$config" -v minimal

out_dir="$root_dir/bin/$config/net7.0"

# Find the .gha in the output folder (supports renamed assembly output).
gha_files=("$out_dir"/*.gha(N))
if (( ${#gha_files} == 0 )); then
  echo "No .gha found in: $out_dir" >&2
  exit 3
fi
if (( ${#gha_files} > 1 )); then
  echo "Multiple .gha files found in: $out_dir" >&2
  printf '  %s\n' "${gha_files[@]}" >&2
  exit 4
fi

gha_path="$gha_files[1]"
base_name="${gha_path:t:r}"

# Prepare staging
rm -rf "$root_dir/dist"
mkdir -p "$root_dir/dist/staging"

# Copy the minimal runtime set Grasshopper needs
cp -f "$gha_path" "$root_dir/dist/staging/"
cp -f "$out_dir/$base_name.deps.json" "$root_dir/dist/staging/" || true

# Managed dependencies
cp -f "$out_dir"/*.dll "$root_dir/dist/staging/" 2>/dev/null || true

# Native runtime assets (MongoDB.Libmongocrypt, etc.)
if [[ -d "$out_dir/runtimes" ]]; then
  mkdir -p "$root_dir/dist/staging/runtimes"
  cp -R "$out_dir/runtimes/" "$root_dir/dist/staging/"
fi

# Third-party notice for the MongoDB driver dependency.
cp -f "$root_dir/NOTICE" "$root_dir/dist/staging/NOTICE"

# Optional symbols
if [[ -f "$out_dir/$base_name.pdb" ]]; then
  cp -f "$out_dir/$base_name.pdb" "$root_dir/dist/staging/" || true
fi

zip_name="$base_name-$config-net7.zip"
(
  cd "$root_dir/dist/staging"
  # -X strips extra file attributes for more consistent zips.
  zip -r -X "../$zip_name" .
)

rm -rf "$root_dir/dist/staging"

echo "Created: $root_dir/dist/$zip_name"