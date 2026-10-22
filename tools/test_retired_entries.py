"""Run only retired compatibility stubs in a fake installation; never originals."""
import ast
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

WORK = Path(__file__).resolve().parents[1]
POWERSHELL = shutil.which("powershell.exe") or shutil.which("pwsh.exe")


class RetiredEntryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=WORK)
        self.addCleanup(self.temp.cleanup)
        self.fake = Path(self.temp.name)
        for name in ("deploy.ps1", "start_ai_match.py"):
            shutil.copyfile(WORK / name, self.fake / name)
        tree = ast.parse((self.fake / "start_ai_match.py").read_text(encoding="utf-8-sig"))
        self.assertEqual([alias.name for node in ast.walk(tree)
                          if isinstance(node, ast.Import) for alias in node.names], ["sys"])
        self.assertFalse(any(isinstance(node, ast.ImportFrom) for node in ast.walk(tree)))
        ps = (self.fake / "deploy.ps1").read_text(encoding="utf-8-sig")
        for legacy_call in ("Start-Process", "Get-Process", "PostMessage", "Copy-Item", "Remove-Item"):
            self.assertNotIn(legacy_call, ps, "do not run a restored legacy deployment script")
        for name, value in {
            "client.py": b'raise RuntimeError("legacy client must not load")\n',
            "cmd.jsonl": b'original command log\n',
            "out.jsonl": b'original receipt log\n',
            "controlled-seat.json": b'{"playerId":2}\r\n',
            "Polytopia.exe": b'fake, never executed',
            "candidate.dll": b'fake old staged DLL',
        }.items():
            (self.fake / name).write_bytes(value)
        self.before = self.snapshot()

    def snapshot(self):
        return {str(p.relative_to(self.fake)): p.read_bytes()
                for p in self.fake.rglob("*") if p.is_file()}

    def check(self, command):
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        result = subprocess.run(command, cwd=self.fake, env=env, capture_output=True,
                                encoding="utf-8", errors="replace", timeout=15)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("is retired", result.stderr)
        self.assertEqual(self.before, self.snapshot())

    def python(self, *args):
        self.check([sys.executable, "start_ai_match.py", *args])

    def powershell(self, *args):
        self.assertIsNotNone(POWERSHELL, "PowerShell parser/runtime unavailable")
        # Parse the new stub before execution; never execute the preserved old file.
        parse = subprocess.run([
            POWERSHELL, "-NoProfile", "-Command",
            "$parseTokens=$null; $parseErrors=$null; "
            "[void][System.Management.Automation.Language.Parser]::ParseFile("
            "(Join-Path (Get-Location) 'deploy.ps1'), [ref]$parseTokens, [ref]$parseErrors); "
            "if ($parseErrors.Count) { $parseErrors | Out-String | Write-Error; exit 1 }",
        ], cwd=self.fake, capture_output=True, text=True, timeout=15)
        self.assertEqual(parse.returncode, 0, parse.stdout + parse.stderr)
        # Applies only to this child process and the parsed fake-directory stub;
        # no machine/user execution-policy configuration is changed.
        self.check([POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass",
                    "-File", "deploy.ps1", *args])

    def test_python_default(self):
        self.python()

    def test_python_old_side_effect_parameters(self):
        self.python("opponents=2", "difficulty=Crazy", "dry=0")

    def test_python_old_dry_parameters(self):
        self.python("dry=1", "--help")

    def test_powershell_default(self):
        self.powershell()

    def test_powershell_old_install_parameters(self):
        self.powershell("-Build", "candidate.dll", "-WaitSeconds", "1")

    def test_powershell_old_plan_parameters(self):
        self.powershell("-Build", "candidate.dll", "-WhatIfOnly")


if __name__ == "__main__":
    unittest.main(verbosity=2)
