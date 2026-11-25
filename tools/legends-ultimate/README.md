# Legends Ultimate tools

Tools for running custom `.uce` cartridges (OneSauce, the Protect Me Knight port)
on an AtGames Legends Ultimate, and the start of an auto-boot loader.

| File | What it does |
|---|---|
| `probe.sh` | Read-only survey of the cabinet: boot chain, init scripts, mounts, process start times, GPU/input/audio libraries. Changes nothing. |
| `run_uce.sh` | Mounts one `.uce` the way the stock menu does and runs its `exec.sh`. Given a folder, it runs the first `.uce` in it. |

## Running the probe

1. Copy `probe.sh` to `script.d/probe.sh` at the root of the USB drive.
2. Start OneSauce's **Scripter** tile once.
3. Send back `scripter.log` from the root of the USB drive.

OneSauce's Scripter (`bash_launcher_libretro.so`) runs every `*.sh` in
`/media/usbN/script.d/` as root and appends the output to `/media/usbN/scripter.log`.

## .uce format (checked against OneSauce's `scripter.uce`)

```
[SquashFS (zlib), zero-padded to 4 KiB]
[64 bytes: MD5 of the padded SquashFS | 32 zero bytes | second MD5*]
[4 MiB ext4 save image]        <- always the last 4 MiB of the file
```

\* The second MD5 is probably of the save image as created; not confirmed.

The SquashFS holds `exec.sh`, `cartridge.xml`, `boxart/`, `emu/core.so`, `roms/`,
`bin/sudo` (Scripter only) and an empty `save/`. The save image holds `upper/` and
`work/`: the stock menu overlays it on the read-only cartridge, so a game can write
anywhere inside it and the changes persist. `run_uce.sh` does the same.

## Auto-boot loader plan

Goal: power on straight into one cartridge, skipping the stock menu, with no
selection screen. The loader looks in `<USB>/autoboot/` and runs the `.uce`
there (the first by name if there are several).

1. **Probe** (`probe.sh`) to learn how the stock menu is started, which
   partitions are writable and where boot time goes.
2. **Test `run_uce.sh` by hand** from the stock menu. Expected problem: Scripter's
   own retroplayer still holds the screen, sound and controls, so a second program
   may not get them. It has to stop that first, which the probe will show how to do.
3. **Boot hook**, only if a writable partition is on the boot path. Rules:
   * Never modify or replace the stock menu's files; only add one small hook.
   * Act only when the USB drive has `autoboot/` with a `.uce` in it. Otherwise
     boot normally.
   * A held button at power-on skips the hook.
   * If the cartridge exits or fails to start, fall back to the stock menu.
   * Install and uninstall both run through Scripter, so no PC or serial cable is needed.

If no writable partition is on the boot path, the fallback is a loader that starts
from the stock menu as early as possible. That saves less time, but still needs no
firmware change.
