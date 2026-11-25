#!/bin/bash
# Mount one BYOG .uce and run it, the way the stock menu does, then clean up.
#   run_uce.sh <file.uce>
#   run_uce.sh <folder>      runs the first .uce in the folder (sorted by name)
#
# .uce layout (checked against OneSauce's scripter.uce):
#   [SquashFS, zero-padded to 4 KiB][64 B: MD5 of the padded SquashFS, 32 zero
#   bytes, a second MD5][4 MiB ext4 save image]  -> save = last 4 MiB of the file.
# The SquashFS holds exec.sh, cartridge.xml, boxart/, emu/, roms/ and an empty
# save/ directory. The save image contains upper/ and work/: the stock menu
# overlays it on the read-only cartridge so games can write anywhere. We do the
# same (cartridge at $MNT/ro, save at $MNT/save, overlay at $MNT/BYOG, save also
# bound onto BYOG/save) and fall back to a plain read-only mount without overlayfs.
set -u
SAVE_SIZE=$((4 * 1024 * 1024))
MNT=${UCE_MNT:-/tmp/uce_loader}
log() { echo "[run_uce] $*"; }

target=${1:?usage: run_uce.sh <file.uce|folder>}
if [ -d "$target" ]; then
  uce=$(ls "$target"/*.uce "$target"/*.UCE 2>/dev/null | sort | head -1)
  n=$(ls "$target"/*.uce "$target"/*.UCE 2>/dev/null | wc -l)
  [ -n "$uce" ] || { log "no .uce in $target"; exit 2; }
  [ "$n" -gt 1 ] && log "warning: $n .uce files in $target, running the first: $uce"
else
  uce=$target
fi
[ -f "$uce" ] || { log "not found: $uce"; exit 2; }

size=$(stat -c %s "$uce")
save_off=$((size - SAVE_SIZE))
root="$MNT/BYOG"; ro="$MNT/ro"; save="$MNT/save"
sq_loop=""; save_loop=""

cleanup() {
  cd /
  umount "$root/save" 2>/dev/null
  umount "$root" 2>/dev/null
  umount "$save" 2>/dev/null
  umount "$ro" 2>/dev/null
  [ -n "$save_loop" ] && losetup -d "$save_loop" 2>/dev/null
  [ -n "$sq_loop" ] && losetup -d "$sq_loop" 2>/dev/null
}
trap cleanup EXIT

mkdir -p "$root" "$ro" "$save"
sq_loop=$(losetup -f) && losetup -r "$sq_loop" "$uce" 2>/dev/null \
  && mount -t squashfs -o ro "$sq_loop" "$ro" \
  || { log "could not mount SquashFS from $uce"; exit 3; }

have_save=0
if [ "$save_off" -gt 0 ]; then
  save_loop=$(losetup -f) && losetup -o "$save_off" "$save_loop" "$uce" \
    && mount -t ext4 "$save_loop" "$save" && have_save=1 \
    || log "warning: save area not mounted (game runs, saves won't persist)"
fi

if [ $have_save = 1 ] && mkdir -p "$save/upper" "$save/work" \
   && mount -t overlay overlay -o "lowerdir=$ro,upperdir=$save/upper,workdir=$save/work" "$root"; then
  mount --bind "$save" "$root/save" 2>/dev/null || log "note: save/ not bound inside the cartridge"
else
  [ $have_save = 1 ] && log "note: no overlayfs, cartridge stays read-only"
  mount --bind "$ro" "$root" || { log "could not prepare $root"; exit 3; }
  [ $have_save = 1 ] && mount --bind "$save" "$root/save"
fi

export UCE_FULL_PATH=$(readlink -f "$uce")
log "running $UCE_FULL_PATH"
cd "$root" && eval "${UCE_EXEC:-sh ./exec.sh}"   # UCE_EXEC: test hook
rc=$?
log "exec.sh exited with $rc"
exit $rc
