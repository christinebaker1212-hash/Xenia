#!/bin/bash
# Read-only probe of an AtGames Legends Ultimate, run through OneSauce's Scripter.
# Copy to <USB root>/script.d/probe.sh, launch Scripter once, then send back
# <USB root>/scripter.log. Nothing on the cabinet is changed.

cap() { head -c "${1:-4000}"; echo; }       # keep each section short
hdr() { echo; echo "======== $*"; }

hdr "system";      uname -a; cat /proc/cmdline; echo "uptime: $(cat /proc/uptime)"
hdr "cpu";         grep -m4 -iE 'model|hardware|cpu part|processor' /proc/cpuinfo
hdr "glibc";       ldd --version 2>&1 | head -1; ls -la /lib/libc.so* /lib/libc-* /lib/aarch64-linux-gnu/libc.so* 2>/dev/null
hdr "id";          id
hdr "busybox";     ls -la /bin/sh /bin/busybox 2>/dev/null

# Who launched us: walk the parent chain up to init. The stock menu is in here.
hdr "parent chain"
p=$$
while [ -n "$p" ] && [ "$p" != 0 ]; do
  echo "pid=$p exe=$(readlink /proc/$p/exe) cwd=$(readlink /proc/$p/cwd)"
  echo "   cmd: $(tr '\0' ' ' < /proc/$p/cmdline | cap 300)"
  p=$(awk '/^PPid:/{print $2}' /proc/$p/status 2>/dev/null)
done

# Boot timeline: every process with its start time (seconds after boot).
hdr "process start times (s after boot)"
hz=$(getconf CLK_TCK 2>/dev/null || echo 100)
for d in /proc/[0-9]*; do
  [ -r "$d/stat" ] || continue
  st=$(sed 's/^.*) //' "$d/stat" | awk '{print $20}')
  printf '%8.2f  %6s  %s\n' "$(echo "$st $hz" | awk '{print $1/$2}')" "${d#/proc/}" \
    "$(tr '\0' ' ' < "$d/cmdline" | cut -c1-160)"
done | sort -n | cap 12000

hdr "mounts";      cat /proc/mounts
hdr "disk usage";  df -h 2>/dev/null
hdr "block devs";  cat /proc/partitions; ls -la /dev/block/by-name /dev/disk/by-label 2>/dev/null

hdr "init system"
ls -la /sbin/init /etc/inittab /etc/init.d /etc/rc.local /etc/systemd/system 2>/dev/null
cat /etc/inittab 2>/dev/null | cap 3000
for f in /etc/init.d/* /etc/rc.local; do
  [ -f "$f" ] || continue; echo "--- $f"; cap 2500 < "$f"
done 2>/dev/null | cap 20000

# Anything in /etc that starts the stock menu or looks at USB at boot.
hdr "boot scripts mentioning usb/media/emulator/launcher"
grep -rlsE 'media/usb|/emulator|launcher|autorun|addon|\.uce' /etc 2>/dev/null | head -40

hdr "writable test (no writes: just checks mount flags)"
awk '$4 ~ /(^|,)rw(,|$)/ {print $2, $3, $4}' /proc/mounts

hdr "/emulator";   ls -la /emulator 2>/dev/null | cap 3000
hdr "retroplayer deps"; ldd /emulator/retroplayer 2>/dev/null

hdr "gpu/display/audio libs"
ls /usr/lib /usr/lib/aarch64-linux-gnu /lib /usr/local/lib 2>/dev/null \
  | grep -iE 'sdl|egl|gles|mali|drm|gbm|asound|pulse|openal' | sort -u
hdr "devices";     ls -la /dev/dri /dev/fb* /dev/mali* /dev/snd 2>/dev/null; ls /dev/input
grep -E 'Name|Handlers' /proc/bus/input/devices

# What OneSauce has already installed (it may have its own boot hook).
hdr "OneSauce traces"
grep -rlsE 'OneSauce|onesauce' /etc /opt /data /userdata /usr/local 2>/dev/null | head -40

hdr "done at uptime $(cut -d' ' -f1 /proc/uptime)"
