# Spacebridge

SALTO's **Local IO Bridge** for macOS. ProAccess SPACE runs in the browser, but encoders, PPDs and nCoders plug into your computer; the Local IO Bridge is the small local service that connects the two. SALTO ships it for Windows only. Spacebridge speaks the same protocol (bridge 1.5.0 and 2.1.0), so the SPACE web app finds it and uses it as if it were SALTO's own.

Not affiliated with or endorsed by SALTO Systems.

```
$ spacebridge status
Spacebridge 1.0.0 · SALTO Local IO Bridge 2.1.0.0 for macOS

  Service      ● running (pid 2437)  http://localhost:50000  https://localhost:50001
  Certificate  ● trusted for https://localhost until 2029-02-08
  Signing key  ● imported
  Allowed      ● https://space.example:8100
  Bluetooth    off
  Serial ports on

  usb0                   PPD          Portable Programmer Device NFC 020000103417
```

## Install

Apple silicon, macOS 13 or later.

With Homebrew:

```sh
brew tap berrysekk/spacebridge https://github.com/Berrysekk/SaltoSpacebridge-macos
brew install spacebridge
spacebridge setup https://your-space:8100
```

Or with one command:

```sh
curl -fsSL https://raw.githubusercontent.com/Berrysekk/SaltoSpacebridge-macos/main/install.sh | sh -s -- https://your-space:8100
```

`setup` takes the address of your SPACE exactly as you type it in the browser and does three things:

1. **Fetches SALTO's signing key** from that server. Every SPACE serves SALTO's own bridge installer (`/ProAccessSpace/Setup_SaltoLocalIOBridge.exe`); the key the bridge signs its connections with is read out of it. It is not part of this repository or of the releases. You can also point at a downloaded installer: `spacebridge key import ~/Downloads/Setup_SaltoLocalIOBridge.exe`.
2. **Allows that SPACE** to use the bridge (see [Who may use the bridge](#who-may-use-the-bridge)).
3. **Installs the background service** (a LaunchAgent that starts at login) and trusts the bridge's own certificate for `https://localhost`, which a SPACE on https needs. macOS asks for your password once.

Then log in to SPACE again. The first time, the browser asks whether the SPACE page may reach devices on this computer (Chrome: "local network", Firefox: "apps and services on this device"). Allow it and repeat the action; SPACE gives up after two seconds, so the first try fails while the question is open.

## Commands

| | |
|---|---|
| `spacebridge` | A menu of everything below: arrows move, Enter picks, Esc goes back |
| `spacebridge status` | What is running, what is allowed, which devices are attached |
| `spacebridge devices [--bluetooth]` | Encoders, PPDs, nCoders and serial ports; `--bluetooth` listens 15 s for SALTO devices |
| `spacebridge logs [-f] [-n N] [--all]` | Which page asked for what, and what it got |
| `spacebridge start` / `stop` / `restart` | The background service |
| `spacebridge allow <address>...` | Let only the listed SPACE servers use the bridge |
| `spacebridge allow --any` | Let any website use it, as SALTO's Windows bridge does |
| `spacebridge deny <address>...` | Take an address off the list |
| `spacebridge origins` | Show the list |
| `spacebridge config [bluetooth\|serial on\|off]` | Show or change settings |
| `spacebridge key import <address\|file>` | Take the signing key from a SPACE or from SALTO's installer |
| `spacebridge setup <address>` | Key, allow, install in one go |
| `spacebridge install` / `uninstall [--all]` | The service; run `install` again after an upgrade |
| `spacebridge selftest` | Check the bridge's own workings; with a device attached it also opens it |

Settings live in `~/Library/Application Support/Spacebridge/config.json`, the log in `~/Library/Logs/Spacebridge.log`.

## Who may use the bridge

SALTO's bridge answers any web page that asks. A page you happen to visit could then open a connection to your encoder. Spacebridge can keep a list instead:

```sh
spacebridge allow https://space.example:8100 http://10.0.0.5:8100
```

From then on only pages from those addresses get an answer, and a pipe can only lead back to the page's own server. Everything else is refused and shows up in `spacebridge status` with the command that would let it in. `spacebridge allow --any` goes back to SALTO's behaviour. `setup <address>` puts its address on the list.

Independently of the list, the bridge listens on loopback only and refuses requests addressed to any name but `localhost`, `127.0.0.1` or `[::1]`.

## Devices

| Device | How | State |
|---|---|---|
| USB encoder, PPD | Silicon Labs CP210x under SALTO's id `10C4:82E9`, driven through libusb (macOS has no driver for it) | listed correctly; a full session against real hardware is still to be confirmed |
| nCoder | USB HID `307E:0164` through HidSharp | untested |
| Serial encoders | any `/dev/cu.*`, 38400 8N1 | tested against a pseudo terminal only |
| Bluetooth LE | CoreBluetooth; SALTO's advertisement, GATT service and packet framing ported from the Windows bridge | off by default, **not yet tried against a SALTO device** |

Bluetooth: `spacebridge config bluetooth on`, then answer macOS's question whether Spacebridge may use Bluetooth. `spacebridge devices --bluetooth` shows what it hears.

## Troubleshooting

- **SPACE says it cannot connect to the Local IO Bridge.** `spacebridge logs`: if nothing from your SPACE appears, the browser did not reach the bridge. Check the browser's permission for the SPACE site (local network / devices on this computer), then `spacebridge status` for the certificate (https SPACE) and the allow-list.
- **`refused origin ...` in the log or in `status`.** The page is not on the list: `spacebridge allow <address>`.
- **Signing key missing.** `spacebridge key import <SPACE address>`.
- **The service is an older version than the command** (after `brew upgrade`): `spacebridge install`.

## Build from source

Needs the .NET 9 SDK and Xcode's command line tools.

```sh
./build.sh                                                  # dist/Spacebridge.app and the release archive
dist/Spacebridge.app/Contents/MacOS/spacebridge selftest
dist/Spacebridge.app/Contents/MacOS/spacebridge install
```

`lib/` holds HidSharp (Apache-2.0, the build SALTO ships) and libusb 1.0.30 from Homebrew (LGPL-2.1). The build is ad-hoc signed, not notarised: an archive downloaded with a browser is quarantined by macOS; `install.sh` and Homebrew are not affected.

How the protocol works, what was found in SALTO's bridge and what is still open: Paměť `projects/salto-local-io-bridge-mac/_index` (Totosoft internal).

## Uninstall

```sh
spacebridge uninstall --all     # service, certificate, settings, signing key
brew uninstall spacebridge      # or: rm "$(command -v spacebridge)"
```
