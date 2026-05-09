# CCJ Relay — BepInEx Plugin

Lets remote players join a CCJ host through a generic UDP relay, regardless
of NAT type. Only the host needs this DLL; joiners run vanilla CCJ.

CCJ's MLAPI/UNet host normally just listens, so it isn't reachable from the
internet without port forwarding. This plugin opens a UDP socket, registers
the host with whichever relay address the matchmaker returns in the
`game.matchMake` response, and runs a loopback bridge to UNet's listen port.
UNet sees each remote client as a distinct local peer, so its reliability,
ordering and CRC keep working end-to-end.

## Build

```powershell
dotnet build -c Release
```

## Install

1. Setup https://github.com/ainlorn/SpiceDoorstop with BepInEx
2. Place `CcjRelay.dll` in `BepInEx\plugins\`

## Configure

`BepInEx\config\io.ccj-relay-plugin.cfg` after first launch:

```ini
[Relay]
Enabled = true

[Diagnostics]
VerboseLogging = false

[HUD]
Enabled = true
ToggleKey = F8
```
