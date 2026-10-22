# Retired compatibility entry. Deliberately no parameter binding or forwarding:
# even old -Build/-WhatIfOnly calls must not close, install, or start the game.
[Console]::Error.WriteLine(@'
deploy.ps1 is retired. No game, DLL, or configuration was changed.
Use these separate entry points from .offline-work/agent-bridge:
  python tools/verify_bridge.py --all
  python tools/deploy_bridge.py plan --evidence <verification.json>
  python tools/deploy_bridge.py install --evidence <verification.json>
  python tools/start_bridge.py plan --player 1
  python tools/start_bridge.py start --player 1
See INSTALL.md and README.md for the supported workflow.
'@)
exit 2
