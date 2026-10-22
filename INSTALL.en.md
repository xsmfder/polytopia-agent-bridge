# Installation, build, and rollback

[English](INSTALL.en.md) | [简体中文](INSTALL.md)

## Prepare the game and loader

Use Windows x64, your own working game installation, and usable Steam login cache. First start the unmodified game using Steam's official offline mode, confirm that the main menu appears, and exit normally. This project does not handle original-game launch or platform authentication. Online launch and other operating systems are outside the current supported scope.

**Install MelonLoader yourself. This project does not bundle, install, or automatically download it.** Obtain the checked [0.7.3 Open-Beta](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3) from the [official project](https://github.com/LavaGang/MelonLoader). Close the game and follow its installer or [manual instructions](https://github.com/LavaGang/MelonLoader#how-to-manually-use-melonloader), targeting `Polytopia.exe`.

Follow the [upstream runtime requirements](https://github.com/LavaGang/MelonLoader/tree/v0.7.3#requirements), which list the x64 .NET 6 Desktop Runtime. This is separate from the .NET Framework compiler used below. Check installed runtimes with `dotnet --list-runtimes`. The checked 0.7.3 package's `MelonLoader/net6/MelonLoader.runtimeconfig.json` requests `Microsoft.NETCore.App 6.0.0`, allowing patch updates within 6.0. The local acceptance environment also worked with 6.0.36 and no 6.x Desktop Runtime. That is evidence for this package and machine, not a universal minimum for other loader versions; having only .NET 7 does not establish that the .NET 6 requirement is met.

For manual installation, use the contents of the corresponding official release. The checked 0.7.3 x64 archive contains `version.dll` and `MelonLoader/`, without a separate `dobby.dll`. Do not download an unrelated DLL to satisfy generic instructions.

Start once with the loader, wait for its dependency generation and the main menu, then exit. Initial setup may download dependencies. Offline gameplay does not establish support for a fully disconnected first installation.

Confirm these files exist:

```text
Polytopia.exe
GameAssembly.dll
MelonLoader/net472/MelonLoader.dll
MelonLoader/net472/0Harmony.dll
```

The `net472` references are required by the current build entry point; MelonLoader handles game runtime loading separately. Complete the loader installation if files are missing. Never upload local references to the source repository.

## Place and build the source

Install Python 3.10+ and check the Windows .NET Framework compiler. The source/output directories and the current user's `$env:TEMP` must be writable. Low-integrity tool tokens may interfere with assembly loading and temporary files; use a normal user environment for building and launching.

```powershell
python --version
Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
```

Extract the selected source into this layout, without an extra archive-root folder:

```text
<game directory>/
  Polytopia.exe
  Mods/
  .offline-work/agent-bridge/
    src/
    tests/
    tools/
    sdk.py
    agentcli.py
```

The in-game queue currently uses this relative location. Python connects to the queue in its own source directory by default; an unrelated checkout will not automatically connect to the game.

```powershell
$gameRoot = 'C:\Games\Polytopia'  # Set the actual path
Set-Location (Join-Path $gameRoot '.offline-work\agent-bridge')
python tools/verify_bridge.py --all --game-root $gameRoot --out v1
```

Verification compiles the bridge and runs C#, Python, frozen-recovery, and tooling tests, producing `v1/verification.json`. It does not launch the game, deploy a DLL, or inspect a live match. Check success and input consistency before deployment. Use a new short output name such as `v2` if `v1` exists; local references and outputs are not committed.

## Deploy and start

Keep the game closed, inspect the plan, then install:

```powershell
python tools/deploy_bridge.py plan --evidence v1/verification.json
python tools/deploy_bridge.py install --evidence v1/verification.json
python tools/start_bridge.py start --player 1
```

Deployment installs only the verified `Mods/PolytopiaAgentBridge.dll`. It does not install the loader, copy game assemblies, or modify saves/authentication. A first installation needs no placeholder DLL; upgrades preserve rollback records. If process state cannot be established or any game instance is active, stop deployment and resolve that instance first.

Successful startup requires `ok=true`, `ready=true`, `status=ready`, and configuration restoration of `restored/already_restored`. This proves that the bridge loaded and reached the main menu, not that a match is ready for action.

Use `load_game --wait` to restore the latest solo save. For hotseat, explicitly open `saved_games --wait`, then run `load_game --mode hotseat --game-id '<GUID from the list>' --wait`. Use a new `--session` file for loading, then observe the controlled seat and readiness. Manual game UI loading remains available. CLI `resume` queries an original operation; it does not load a save.

`save_game --wait` saves the controlled match. `return_to_menu --wait` saves and returns to the menu. `quit_game --wait` saves an active match, returns to the menu, and requests normal exit. Keep the same persistent session and observe an active match to bind its identity first. A confirmed save requires a write change in the corresponding native save file; other saves are not inspected or published.

Before invoking Quit, the bridge durably records final progress in `lifecycle-receipts/`. A delayed query can read this record after the original process exits, checking its operation ID, PID, process-start marker, and controlled seat. Missing or mismatched records do not prove success. Exit is confirmed only with evidence that the original queue-owner process ended; `exitCause=not_verified` does not claim that normal termination was distinguished from a crash. Do not replay a timed-out request or forcibly kill the process to obtain a success result.

`start_bridge.py` has no `stop` command. Its `restore` restores temporary launch configuration; SDK `close()` releases the session. Neither stops the game. Save and exit normally, then wait for the process to end before deployment/rollback. See [usage examples](USAGE.en.md).

For hotseat, launch the actual fixed seat, such as `--player 2`. The temporary disk configuration is restored after startup; it does not change the in-process controlled seat or justify launching a second instance.

## Rollback

Keep the `completed.json` path returned as `record` by installation. Save and exit the game normally, then run:

```powershell
python tools/deploy_bridge.py plan --record '<actual path to completed.json>'
python tools/deploy_bridge.py rollback --record '<actual path to completed.json>'
```

First-install rollback removes only the unchanged bridge DLL from that installation. Upgrade rollback restores its previous DLL. Do not remove all of `Mods` or `UserData`. Uninstalling MelonLoader itself is a separate upstream procedure.

## Troubleshooting

| Symptom | Checks |
| --- | --- |
| Unmodified game also fails | Restore normal official Steam offline launch; the bridge does not handle authentication |
| Loader download/setup fails | Inspect MelonLoader logs and upstream dependencies; initial setup may need network access |
| `0x80131515`, assembly loaded from a network location | Check download `Zone.Identifier` and mandatory integrity labels separately; see below |
| Temporary file access denied | Check user permissions on `$env:TEMP`, the process token, and the exact failed path; retry verification in a new output directory after fixing the environment |
| Error 1455 | Check Windows memory/pagefile commit availability; see [Microsoft's error reference](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-) |
| Bridge is not ready | Check loader logs, versions, PID, seat, and queue path; inspect the existing process after timeout instead of repeatedly launching |
| Fields disappear after an update | Recheck [compatibility](COMPATIBILITY.en.md), preserving unknown/partial results |

Only after checking the download's source and SHA256, inspect and remove the archive's internet-download marker before extracting to a new destination:

```powershell
$sourceZip = 'C:\Downloads\polytopia-agent-bridge-source.zip'  # Set the checked archive
Get-Item -LiteralPath $sourceZip -Stream Zone.Identifier -ErrorAction SilentlyContinue
Unblock-File -LiteralPath $sourceZip
```

[`Unblock-File`](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/unblock-file) removes a download marker, not Low/Medium integrity labels. If loading still fails, inspect the exact refused path with `icacls '<actual path from the log>'`. The local DSH run encountered Low labels and passed after returning to a normal Medium-integrity environment. Recursively changing permissions on the entire game directory or weakening system policy is not a default installation step. Keep failure logs and use a new verification output directory after fixing the environment.

Earlier source selections passed independent-directory offline first installation and bridge rollback/reinstall on this machine. The lifecycle follow-up was an **in-place source replacement and upgrade acceptance**, not another clean installation. Neither result establishes other-machine support, fully disconnected setup, complete loader uninstallation, or first-install coverage for every later runtime change. See the [release checklist](RELEASE_CHECKLIST.en.md).
