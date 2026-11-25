# Protect Me Knight PC (unofficial port)

A native Windows/Linux port of the Xbox Live Indie Game *Protect Me Knight / まもって騎士*
(Ancient, 2010), built on [FNA](https://fna-xna.github.io/). It adds keyboard controls,
window and scaling options, online play for up to 4 players, and an in-game updater.

**Bring your own game.** This repository and its releases contain **no game code or
assets**, only the port: a compatibility layer, an installer and converters. On first
start, the installer reads **your own** copy of the game package and builds a PC copy
on your machine. It is for personal use; please don't redistribute the resulting files.

## Install and play

1. Download `ProtectMeKnightPC-win-x64.zip` (or `-linux-x64.zip`) from the
   [latest release](../../releases/latest) and unzip it anywhere you can write to
   (not inside Program Files).
2. Run **`Protect Me Knight.exe`** (Linux: `./ProtectMeKnight`). The folder looks like this:

   ```
   Protect Me Knight\
     Protect Me Knight.exe   <- start the game
     app\                    <- program files (don't touch)
     data\                   <- created on first run: your game copy, settings, logs
     README.md
   ```
3. First-time setup looks for the game package in your Xenia content folder. If it
   doesn't find it, pick the file yourself. It is the 42-character file inside
   `...\584E07D2\00000002\`, for example
   `584E07D2\00000002\75B16C4FA738C74FF911D01325FA563D9B4FEFA458`.
4. Setup extracts the package, converts the game code, and decodes the Xbox audio.
   Audio decoding uses ffmpeg; on Windows a copy is downloaded once if
   none is installed. On Linux, install `ffmpeg` with your package manager.

Everything the installer creates lives in the `data` folder (or
`%LOCALAPPDATA%\ProtectMeKnightPC` if that folder isn't writable). To redo setup,
delete `data\install.json` (or the whole `data` folder).

### Controls

Controllers work like on the Xbox: controller 1 is player 1, and so on, up to 4.
The keyboard drives player 1 by default (Options → Keyboard controls changes the
player or turns it off). Default keys, all rebindable in Options → Keyboard bindings:

| Pad | Key | Pad | Key |
|---|---|---|---|
| D-pad | Arrow keys | Start | Enter |
| A | Z | Back | Backspace |
| B | X | LB / RB | Q / W |
| X | A | LT / RT | Left Shift / Space |
| Y | S | | |

* **Esc** (or **Back + Start + LB** on a controller) opens the port menu: options,
  quit to the title menu, quit.
* **F11** or **Alt+Enter** toggles fullscreen.

### Options

Window size (1×–8× of 640×360), fullscreen, VSync, sharp or smooth (Xbox-style)
pixels, integer scaling, music and sound volume, vibration, keyboard player and key
bindings, online settings, automatic update checks. Saved in `data/settings.json`.

## Game modes

*Select game mode* (after everyone has joined) offers four modes:

| Mode | |
|---|---|
| **ARCADE** | The original game: ten stages on the difficulty you choose, then the ending. |
| **SURVIVE** | The original survival mode. |
| **ENDLESS** | Arcade stages on **HELL** with no ending. After stage 10 the stages repeat in order (stage 11 is stage 1, stage 20 is stage 10, and so on), and every stage past stage 10 brings 25% more of each stage's monsters: stage 11 has 125%, stage 12 150%, stage 20 350%. The final boss is never doubled. Game over works like Arcade: retry the stage or quit. |
| **REMIX** | Greyed out for now. It will be the modded game, with new characters and stages. |

## Online play

Up to 4 players over the internet or a home network, with any mix of players per
PC (for example 2 on one PC and 2 on another). The connection handling follows
*Moemon Emerald Kai Co-op*.

**Hosting:** main menu, then *Online: host a game*.
* The game asks your router to open its port (UPnP, or NAT-PMP on Apple routers)
  and works out your home's internet address. That becomes an **invite code**
  like `K7QM-3XPA`, copied to your clipboard. Send it to your friends.
* When you stop hosting or close the game, the port is closed again. If the game
  ever crashes, the router closes it by itself within two hours, and the next
  start cleans up.
* **Windows Firewall** asks the first time you host: tick **both Private and
  Public**, then Allow. Windows 11 often counts home networks as Public.
* Optional password: Options, then Online settings. Only a hash of it ever
  crosses the network.

**Joining:** main menu, then *Online: join a game*.
* **Rejoin <name>** takes you back to the last game you joined, with nothing to
  type (the password isn't asked again).
* **Enter invite code or address**: type the code. A plain address works too
  (`1.2.3.4`, `1.2.3.4:47820`, or a Radmin VPN or Tailscale address). Ctrl+V pastes.
  Look-alike characters are forgiven (O/0, I/L/1), and a mistyped code is caught.
* Games on the **same home network** are listed automatically.

**If the host screen says friends can't reach you:**
1. *Your router's automatic port opening (UPnP) is off*: turn on UPnP in the
   router's settings page (often under Advanced, NAT or Gaming), then host again.
2. *Your internet provider shares one address between many homes (CGNAT)*,
   common on mobile and some fiber connections: nobody can reach your PC
   directly. Let a friend with a normal connection host, or use step 3.
3. Last resort: **Radmin VPN** (free) or **Tailscale**. The host and every friend
   install it and join the same network; the host screen then shows the address
   (and a code) that works inside it.
4. VPN apps such as Surfshark or NordVPN can get in the way: turn them off.

**Dropped connections:** if someone's connection drops mid-game, everyone pauses
("Waiting for X (reconnecting, 15 s)") while their game reconnects by itself.
They pick up exactly where they left off. After 20 seconds the game goes on
without them. Leaving through the menu tells everyone at once.

**How it works:** every PC runs the full game in **deterministic lockstep**. Only
controller inputs cross the network, a few frames ahead (*Input delay*, default 3
frames, about 50 ms), which hides latency up to roughly that much each way; raise
it for distant players. All players must run the same port version and the same
game files; the host refuses mismatches with a clear reason. The game's random
numbers are seeded per session, and peers compare a checksum every 2 seconds. If
a desync is ever detected, a warning appears and you should restart the session.
TCP port 47820 (up to 47827) carries the game; UDP 47821 finds games on the home
network.

## Updates

On start (at most every 6 hours) the game checks this repository's latest GitHub
release. *Update to vX* in the main menu downloads the new version, checks its
SHA-256 when GitHub provides one, swaps the program files and restarts. Your `data`
folder (game copy, settings) is never touched; the game code is re-converted
automatically if a new version needs it. Automatic checks can be turned off in
Options.

**Private repository?** GitHub hides a private repository's releases from
unauthenticated requests. Create a fine-grained token with read-only *Contents*
access to this repository and save it as `data/github-token.txt` (or set
`PMK_GITHUB_TOKEN`); the updater then checks and downloads through the API. The
token stays on your PC.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet build src/ProtectMeKnightPC.slnx
dotnet test src/ProtectMeKnightPC.slnx
dotnet run --project src/PMK.Launcher
```

Useful command-line options:

* `--install <package>` runs setup without a window.
* `--data <dir>` uses another data folder.
* `--play` skips the menu.
* `--host` / `--join <addr>` goes straight to online play.

For automated tests there are also `--frames N`, `--screenshots <dir>`,
`--screenshot-every N`, `--input-script <file>`, `--seed N` and `--autostart N`.
Setting `PMK_GAME_PACKAGE=<path to your package>` enables an end-to-end extract and
relink test in `dotnet test`.

### Releasing

Change the `VERSION` file (for example to `1.0.0`) and push, or push a tag such as `v1.0.0`. The **Release** workflow tests, publishes self-contained
Windows and Linux builds, and attaches `ProtectMeKnightPC-win-x64.zip` and
`ProtectMeKnightPC-linux-x64.zip` to a GitHub release. Those asset names are what the
in-game updater looks for. You can also run the workflow manually with a version
number.

## How the port works

XBLIG games are .NET assemblies built against XNA Game Studio 3.1, not PowerPC
machine code, so no emulation or recompilation is needed.

| Piece | What it does |
|---|---|
| `PMK.Patcher/StfsPackage.cs` | Reads the Xbox 360 STFS (LIVE) package. A Python version is in `tools/stfs_extract.py`. |
| `PMK.Patcher/Relinker.cs` | Uses Mono.Cecil to rewrite the game's references from XNA 3.1 / .NET Compact Framework to FNA + `PMK.Compat` + .NET 10, and checks that every reference resolves. Also replaces the 74 clock-seeded `new Random()` calls with session-seeded ones for online play. |
| `PMK.Compat` | The XNA 3.1 API the game expects, implemented over FNA: `Game`, `GraphicsDevice`, `SpriteBatch` (3.1 blend modes, non-premultiplied alpha), `RenderTarget2D.GetTexture`, XACT audio (`AudioEngine`/`SoundBank`/`WaveBank`/`Cue`), input, and stubs for Xbox LIVE GamerServices (full game, nobody signed in). |
| Textures and fonts | FNA reads the original Xbox 360 XNB files directly (LZX decompression, byte-swapping), so no asset conversion is needed. |
| `PMK.Patcher/AudioConverter.cs` | The Xbox wave banks are XMA, which FNA can't play. Each sound is wrapped in an XMA2 RIFF header and decoded once by ffmpeg to WAV. Cue structure, playlists and loop points still come from the original banks. |
| `PMK.Launcher` | Window, scaling, menus, settings, setup, lockstep netplay (`Net/`), updater. |
| `PMK.Mods` | Gameplay additions such as the mode select with ENDLESS and REMIX. Static methods marked `[Hook("Game.Type", "Method")]` run before (or, as `Postfix`, after) a game method; a prefix that returns `false` replaces it. New menu words are drawn at run time from the letters in your copy of the game's menu textures, with missing letters (X) generated in the same style. |
| `PMK.GameApi` | Declarations only (names, signatures and enum values) of the game members `PMK.Mods` uses, so it compiles without any game code. Never loaded at run time. |
| `PMK.Patcher/ModLinker.cs` | During setup, points `PMK.Mods` at the real game, checks that every name, signature and enum value it uses matches the game (setup stops with a clear error otherwise), makes those members accessible and splices the hook calls into the game code. Setup re-runs automatically when the mods change. |

The game renders at 640×360; the port scales it by an integer factor without
resampling. Each game frame is drawn exactly once per update (like the Xbox's fixed
60 Hz), which keeps the simulation identical regardless of PC speed.
