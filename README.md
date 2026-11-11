# SALTO Local IO Bridge for macOS

Port of SALTO's Windows Local IO Bridge (1.5.0 and 2.1.0 speak the same protocol). Protocol, decisions and state: Paměť `projects/salto-local-io-bridge-mac/_index`.

Build: `dotnet publish -c Release -o dist && cp install.sh dist/` · check: `dist/SaltoLocalIOBridge --selftest` (with an encoder or PPD on USB it also claims it) · install or update on a Mac: `sh dist/install.sh`.

`SignConsts.cs` is not in git: it holds SALTO's signing key, taken from the decompiled `SaltoLocalIOBridge.Core.Sign/SignConsts.cs` of the Windows installer.

Requests are logged to `~/Library/Logs/SaltoLocalIOBridge.log`.
