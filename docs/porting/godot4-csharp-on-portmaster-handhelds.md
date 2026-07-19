# Running a Godot 4 **C#/.NET** game on RK3326 PortMaster handhelds (R36S, RG351, Anbernic…)

**Status: working, tested.** This is a complete, reproducible recipe for shipping a
**Godot 4 game written in C# (.NET / Mono)** to a Linux handheld that has no display
server (ArkOS / muOS / Knulli / stock-R36S, RK3326 / Mali-G31 class), via **PortMaster
+ WestonPack**.

We could find **no prior public example** of Godot-4 **C#** on a PortMaster handheld
(GDScript Godot games are common; C# ones were assumed impossible). It turned out to be
possible without recompiling the engine. This document exists so anyone can reproduce it —
more Godot C# games/ports on handhelds is the goal.

Proven on: **R36S**, ArkOS-lineage stock firmware, RK3326 / Mali-G31, 1 GB RAM, 640×480.
Game: OpenSWOS (Godot 4.6.2 .NET, ~26k LOC C#). Menu + music + a full 22-player match run
smoothly.

---

## 0. Why this is hard (skip if you just want the steps)

- **Godot 4 desktop-Linux binaries need X11 or Wayland.** These handhelds boot straight
  into EmulationStation on the **KMS/DRM framebuffer** — there is no display server. A bare
  Godot export dies with `X11 Display is not available / Can't connect to a Wayland display`.
- **FRT** (the classic framebuffer Godot platform) is **Godot 3 only and GDScript only** —
  no Godot 4, no .NET. Dead end for C#.
- **C# adds the .NET/CoreCLR runtime**, which must be present on-device. Godot 4 C# projects
  can only export to *Desktop* (Win/macOS/Linux) — but "Linux" here means an X11/Wayland
  binary.

**The trick:** PortMaster's **WestonPack** runtime provides a headless
Weston (Wayland) + Xwayland + GL compositor on top of the framebuffer. You export your game
as a **normal Godot 4 Linux arm64 .NET binary** (self-contained, bundling CoreCLR) and run
it *inside* WestonPack. The engine speaks to Weston's Wayland/X, WestonPack renders to the
framebuffer via the device GPU. No engine recompile.

---

## 1. Prerequisites

- **Godot 4.x — the .NET/Mono edition** (matching your project's version). Your project stays
  C#.
- **PortMaster installed on the device**, recent enough to have `harbourmaster` + the
  WestonPack runtime. (Almost all current CFWs ship it.)
- Your game must be **arm64 (aarch64)**. RK3326 has a 64-bit kernel; ship arm64. (Do NOT ship
  arm32 — .NET on armhf is second-class and unnecessary here.)
- A way to copy files to the device (SD card reader, or SSH/Samba over USB-Ethernet — WiFi
  dongles on these devices are unreliable).

---

## 2. Project settings (required)

WestonPack's GL path on Mali is GLES, not Vulkan. In `project.godot`:

```ini
[rendering]
renderer/rendering_method="gl_compatibility"          ; NOT forward_plus / mobile-vulkan
renderer/rendering_method.mobile="gl_compatibility"
textures/vram_compression/import_etc2_astc=true       ; ETC2/ASTC, not S3TC/BPTC
```

- Use the **Compatibility renderer** (OpenGL / GLES3). Vulkan will not work — Mali-G31 has no
  usable Vulkan here.
- Compressed textures must be **ETC2 or ASTC**. Most 2D games have none; 3D games do.
- Your game **must not** include native code for non-arm64 platforms.

---

## 3. Export the game (self-contained arm64 .NET)

1. In Godot's **.NET** editor, add the **"Linux — .NET — arm64"** export template
   (matching your engine version; if the official downloads don't have your exact patch
   version, build the template from source with `module_mono_enabled=yes arch=arm64`).
2. Export architecture = **arm64**, C# enabled.
3. **Verify the export is self-contained.** Next to your binary you must get a folder like
   `data_<YourGame>_linuxbsd_arm64/` containing the .NET runtime:
   ```
   libcoreclr.so  libhostfxr.so  libhostpolicy.so  GodotSharp.dll  System.*.dll  (…~189 files)
   ```
   If those aren't there, the device has no system .NET and the game won't start — re-export
   as self-contained (force with `-p:IncludeNativeLibrariesForSelfExtract=true` if needed).
4. You end up with three things to ship:
   ```
   YourGame.arm64                         # the Godot binary (ELF 64-bit ARM aarch64)
   YourGame.pck                           # your game data
   data_YourGame_linuxbsd_arm64/          # the bundled .NET runtime (whole folder)
   ```

**Headless export from CLI** (note: the Godot process often *hangs on exit* after writing
the files — wrap it in `timeout`; the output is complete once its size is stable):
```bash
timeout 420 godot_console --headless --path <projectdir> \
  --export-release "Linux ARM64" <out>/YourGame.arm64
```

---

## 4. Get the WestonPack runtime onto the device

WestonPack ships as a PortMaster runtime `weston_pkg_0.2.squashfs`. If the device is online,
`harbourmaster` downloads it on first launch of any WestonPack port. If offline, place it
manually at `.../PortMaster/libs/weston_pkg_0.2.squashfs`.

Source (arm64 build), from the PortMaster runtimes repo:
```
https://raw.githubusercontent.com/PortsMaster/PortMaster-New/main/runtimes/weston_pkg_0.2.aarch64.squashfs
```
Save it as `weston_pkg_0.2.squashfs` (drop the `.aarch64`) in `PortMaster/libs/`. Verify with
`file` → `Squashfs filesystem …`.

You do **NOT** need PortMaster's shared `godot_4.x` runtime — that is the GDScript engine
with no .NET. You ship your own binary instead. (This is the crux of getting C# to work.)

---

## 5. Launcher script

Layout on the device (ArkOS example — `$directory` resolves to `roms`):
```
roms/ports/YourGame.sh          # this launcher
roms/ports/YourGame/            # payload: YourGame.arm64 + YourGame.pck + data_*_arm64/
```

`YourGame.sh` (LF line endings only — CRLF will break bash on the device):
```bash
#!/bin/bash
# Godot 4 C#/.NET game on a PortMaster handheld via WestonPack.
progdir="$(cd "$(dirname "$0")" && pwd)"
GAMEDIR="$progdir/YourGame"          # payload folder
BINDIR="$GAMEDIR"                    # binary sits DIRECTLY here (BINDIR == GAMEDIR)
LOG="$BINDIR/game.log"

# Checkpoint logger with per-step sync so the log survives an unsafe SD pull.
log(){ echo "[$(date '+%H:%M:%S' 2>/dev/null)] $*" >> "$LOG"; sync 2>/dev/null; }
: > "$LOG"; sync
log "start progdir=$progdir uname=$(uname -m)"

# --- Locate the PortMaster that actually has the WestonPack runtime ----------
# GOTCHA: some devices have TWO PortMasters (a minimal /opt/system/Tools/PortMaster
# and the full one on the SD /roms/ports/PortMaster). Capture the ABSOLUTE runtime
# path from whichever has it, BEFORE sourcing control.txt (control.txt clobbers
# $controlfolder), and re-pin controlfolder afterwards.
controlfolder=""; WESTON=""
for cf in /roms/ports/PortMaster /roms2/ports/PortMaster "$HOME/.local/share/PortMaster" \
          /opt/system/Tools/PortMaster /opt/tools/PortMaster /storage/roms/ports/PortMaster; do
  [ -d "$cf" ] || continue
  if [ -f "$cf/libs/weston_pkg_0.2.squashfs" ]; then controlfolder="$cf"; WESTON="$cf/libs/weston_pkg_0.2.squashfs"; break; fi
  [ -z "$controlfolder" ] && [ -f "$cf/control.txt" ] && controlfolder="$cf"
done
OUR_CTRL="$controlfolder"
[ -z "$WESTON" ] && WESTON="$OUR_CTRL/libs/weston_pkg_0.2.squashfs"

source "$OUR_CTRL/control.txt"
[ -f "${OUR_CTRL}/mod_${CFW_NAME}.txt" ] && source "${OUR_CTRL}/mod_${CFW_NAME}.txt"
get_controls
controlfolder="$OUR_CTRL"                          # re-pin (control.txt clobbered it)
: "${DISPLAY_WIDTH:=640}"; : "${DISPLAY_HEIGHT:=480}"
log "controls ok res=${DISPLAY_WIDTH}x${DISPLAY_HEIGHT} weston=$([ -f "$WESTON" ] && echo YES || echo NO)"

# --- Mount WestonPack --------------------------------------------------------
weston_dir=/tmp/weston; $ESUDO mkdir -p "$weston_dir"
[ -f "$WESTON" ] || { $ESUDO "$controlfolder/harbourmaster" --quiet --no-check runtime_check "weston_pkg_0.2.squashfs"; WESTON="$controlfolder/libs/weston_pkg_0.2.squashfs"; }
[[ "$PM_CAN_MOUNT" != "N" ]] && $ESUDO umount "$weston_dir" 2>/dev/null
$ESUDO mount "$WESTON" "$weston_dir"
log "weston mounted, westonwrap: $([ -f "$weston_dir/westonwrap.sh" ] && echo FOUND || echo MISSING)"

chmod +x "$BINDIR/YourGame.arm64" 2>/dev/null
CONF="$GAMEDIR/conf"; $ESUDO mkdir -p "$CONF"

# --- Run OUR binary inside WestonPack ----------------------------------------
# - headless noop kiosk crusty_x11egl : Weston headless + no-op renderer + kiosk shell,
#   app renders via Crusty X11/EGL. This combo works for Godot 4 on RK3326.
# - NO --main-pack: the official export template rejects path overrides. Godot auto-loads
#   YourGame.pck sitting next to YourGame.arm64 (basename match).
# - --rendering-driver opengl3_es + --audio-driver ALSA.
# - Do NOT run gptokeyb if your game reads the gamepad natively (Godot SDL joypad) — running
#   both feeds every button twice. Only use gptokeyb for keyboard-only games.
log "exec game"
echo "----- game output -----" >> "$LOG"; sync
$ESUDO env $weston_dir/westonwrap.sh headless noop kiosk crusty_x11egl \
  HOME="$CONF" XDG_DATA_HOME="$CONF" \
  "$BINDIR/YourGame.arm64" \
  --resolution ${DISPLAY_WIDTH}x${DISPLAY_HEIGHT} -f \
  --rendering-driver opengl3_es --audio-driver ALSA >> "$LOG" 2>&1
log "game exited rc=$?"

$ESUDO $weston_dir/westonwrap.sh cleanup >> "$LOG" 2>&1
[[ "$PM_CAN_MOUNT" != "N" ]] && $ESUDO umount "$weston_dir" 2>/dev/null
sync
pm_finish
```

Register the port for EmulationStation the normal PortMaster way (a `port.json` / gameinfo),
or just drop the `.sh` in `roms/ports/` for the built-in Ports menu.

---

## 6. The gotchas that cost us hours (the important part)

1. **Run YOUR OWN self-contained binary, not PortMaster's shared `godot_4.x` runtime.** That
   shared runtime is GDScript-only (no .NET). Your C# `.pck` needs your own .NET-enabled
   binary + its `data_*_arm64/` CoreCLR folder next to it. This single fact is why "C# on
   these devices" was thought impossible.
2. **Drop `--main-pack`.** The official Godot export template is built *without* path-override
   support and aborts with `--main-pack … this Godot binary was compiled without support for
   path overrides`. Just place `YourGame.pck` next to `YourGame.arm64` and Godot auto-loads it.
3. **Two PortMasters.** Some firmwares have a minimal `/opt/system/Tools/PortMaster` (no
   runtimes) *and* the full one on the SD card. Detect the one that actually holds the runtime,
   capture its **absolute** path *before* `source control.txt` (sourcing overwrites
   `$controlfolder`), and re-pin after `get_controls`.
4. **CRLF kills it.** If you author the launcher on Windows, strip carriage returns
   (`tr -d '\r'`) — bash on the device errors on `\r`.
5. **westonwrap args for Godot 4 / RK3326:** `headless noop kiosk crusty_x11egl`, and give the
   Godot binary `--rendering-driver opengl3_es --audio-driver ALSA`. (`drm` backend does not
   work with gl4es on these; the headless + Crusty X11/EGL path does.)
6. **gptokeyb double-input.** If your game already reads the gamepad natively (Godot SDL
   joypad), do **not** also start `gptokeyb` — the same physical press then fires twice
   (e.g. "shoot" *and* "menu back"). Use gptokeyb only for keyboard-only games.
7. **Perf: don't supersample.** Mali-G31 is fill-rate limited. If your game renders at a high
   internal resolution and downscales (2D "viewport" stretch with a >1 scale, or a big design
   resolution), you waste GPU. Render the match/gameplay near the panel resolution
   (e.g. 640×480 / 768×544) and upscale once. On OpenSWOS this removed all match stutter.
8. **SD write flushing.** After writing to the card from a PC, flush the OS cache
   (Windows: `Write-VolumeCache -DriveLetter X`) before pulling it, or the write is lost. On
   the device side, `sync` in the launcher (and after each log line) makes the on-card log
   survive an unsafe power-off.

---

## 7. Debugging on-device (no display needed)

The launcher above writes a **checkpoint log with per-step `sync`** to
`roms/ports/YourGame/game.log` on the SD card. If the game doesn't start, power off, put the
card in a PC, and read that file — it tells you exactly which step failed (runtime mount,
binary exec, or the Godot/EGL output). This beats guessing. Typical first-run errors and their
meaning:
- `X11 Display is not available / Can't connect to a Wayland display` → the binary is running
  **outside** WestonPack (launcher didn't mount/enter it).
- `--main-pack … compiled without support for path overrides` → remove `--main-pack` (gotcha 2).
- `weston runtime present: NO` → wrong PortMaster / runtime not placed (gotchas 3, section 4).
- Crash after `Crusty loaded` with no window → renderer/driver mismatch; confirm
  `gl_compatibility` + `--rendering-driver opengl3_es`.

---

## 8. References & credits

- **WestonPack** — the runtime that makes this possible: <https://github.com/binarycounter/Westonpack>
  (see its wiki "Godot 4 Example").
- **PortMaster** — <https://portmaster.games> ; runtimes repo `PortsMaster/PortMaster-New`.
- **efornara/frt** — the Godot *3* framebuffer platform (why Godot 4 needed a different path).
- Godot .NET compiling docs — <https://docs.godotengine.org> (Compiling with .NET).

Worked example: **OpenSWOS** (this repository) — a from-scratch Godot 4.6.2 C# recreation of
Sensible World of Soccer, running on the R36S via exactly this recipe.

---

*Written after getting OpenSWOS (Godot 4.6.2, C#/.NET, ~26k LOC) playable on an R36S. If you
use this to ship a Godot-4-C# game on a handheld, you may well be the second — please add
yourself and any device-specific notes.*
