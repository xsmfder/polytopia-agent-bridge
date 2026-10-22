"""Retired entry: do not forward historical parameters into current match setup."""
import sys


def main(argv=None):
    print(
        "start_ai_match.py is retired. No bridge request or match creation was performed.\n"
        "Use agentcli.py --player 1 --session <session.json> setup_status, then choose\n"
        "a fresh native actionId with setup_action <setup.json> <actionId> --wait.\n"
        "Resume an existing match before considering a new one. See USAGE.md and\n"
        "INSTALL.md for the supported workflow.",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
