#!/usr/bin/env bash
# Reproduce the local Terraria "kernel" sources under third_party/kernel/ (not committed).
#   runtime : ILSpy decompile of the installed Steam tModLoader.dll (exactly what BossPilot runs on)
#   145     : tModLoader 1.4.5 dev setup (vanilla 1.4.5.8 decompile + tML patches), slow, optional
# Usage: scripts/setup_kernel.sh [runtime|145|all]   (default: runtime)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KERNEL="$ROOT/third_party/kernel"
STEAM="$HOME/Library/Application Support/Steam/steamapps/common"
TML="$STEAM/tModLoader"
# tModLoader stable targets net8.0; Homebrew's dotnet only ships .NET 10, so use the user-local SDK.
export DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1

runtime() {
	[ -x "$HOME/.dotnet/tools/ilspycmd" ] || "$HOME/.dotnet/dotnet" tool install -g ilspycmd --version 9.1.0.7988
	local ver out
	ver=$(python3 -I -c 'import re,sys; s=open(sys.argv[1],"rb").read().decode("utf-16-le","ignore"); print(max(re.findall(r"20\d\d\.\d+\.\d+\.\d+",s)))' "$TML/tModLoader.dll")
	out="$KERNEL/tModLoader-v$ver"
	refs=()
	while IFS= read -r d; do refs+=(-r "$d"); done < <(find "$TML/Libraries" -name '*.dll' -not -path '*/Native/*' -not -path '*/runtimes/*' -exec dirname {} \; | sort -u)
	mkdir -p "$out"
	PATH="$HOME/.dotnet:$PATH" "$HOME/.dotnet/tools/ilspycmd" -p --nested-directories -lv Latest -o "$out" "${refs[@]}" "$TML/tModLoader.dll"
	echo "runtime kernel: $out ($(find "$out" -name '*.cs' | wc -l | tr -d ' ') files)"
}

v145() {
	# Separate APFS copy so a setup run never races another agent working in third_party/tModLoader.
	local src="$KERNEL/tModLoader-1.4.5-src"
	[ -d "$src" ] || cp -cR "$ROOT/third_party/tModLoader" "$src"
	# Only DiffPatch is needed; FNA's native-lib submodules (FAudio, SDL*) fail often over a flaky GitHub link.
	git -C "$src" submodule update --init --depth 1 FNA setup/PatchReviewer
	git -C "$src/setup/PatchReviewer" submodule update --init --depth 1 DiffPatch
	touch "$src/.git/tml-setup-module-init.touch"
	# Setup decrypts Terraria_v1.4.5.8_win.exe next to the Steam Terraria.exe (proof of ownership = the local Mac
	# install). Its own TerrariaServer download drops mid-transfer through the proxy, so prefetch it with resume.
	local res="$STEAM/Terraria/Terraria.app/Contents/Resources" tmp
	if [ ! -f "$res/TerrariaServer_v1.4.5.8_win.exe" ]; then
		tmp=$(mktemp -d)
		curl -L --fail --retry 5 --retry-all-errors -C - -sS -o "$tmp/server.zip" \
			https://terraria.org/api/download/pc-dedicated-server/terraria-server-1458.zip
		unzip -q "$tmp/server.zip" '1458/Windows/TerrariaServer.exe' -d "$tmp"
		cp "$tmp/1458/Windows/TerrariaServer.exe" "$res/TerrariaServer_v1.4.5.8_win.exe"
	fi
	# The 1.4.5 setup tool targets net10.0 (Homebrew dotnet).
	(cd "$src" && PATH="/opt/homebrew/bin:$PATH" ./setup-cli.sh setup --no-prompts --plain-progress)
	echo "1.4.5 source: $src/src"
}

case "${1:-runtime}" in
	runtime) runtime ;;
	145) v145 ;;
	all) runtime; v145 ;;
	*) echo "usage: $0 [runtime|145|all]" >&2; exit 2 ;;
esac
