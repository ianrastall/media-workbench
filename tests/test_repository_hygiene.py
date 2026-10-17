"""Tracked files stay machine-independent: no personal paths, identities, or locally built binaries."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
# A user profile path written out literally (placeholders such as %LOCALAPPDATA% or <user> are fine).
PROFILE_PATH = re.compile(r"(?i)\b[a-z]:[\\/]+users[\\/]+(?![<%$])[^\\/\s\"'<>]+")
EMAIL = re.compile(r"[\w.+-]+@[\w-]+(\.[\w-]+)+")
ALLOWED_EMAILS = re.compile(r"(?i)(@users\.noreply\.github\.com|@anthropic\.com|@example\.(com|org|invalid))$")
BUILT_SUFFIXES = {".dll", ".exe", ".pdb", ".lib", ".obj", ".msi", ".log", ".tmp", ".partial", ".safetensors", ".ckpt"}


def git(*arguments):
    return subprocess.run(["git", *arguments], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", check=True).stdout


@unittest.skipUnless(shutil.which("git") and (ROOT / ".git").exists(), "requires a git checkout")
class RepositoryHygieneTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.files = [ROOT / name for name in git("ls-files", "-z").split("\0") if name]
        cls.texts = {}
        for path in cls.files:
            try:
                cls.texts[path] = path.read_text(encoding="utf-8")
            except (UnicodeDecodeError, FileNotFoundError):
                pass  # Binary assets, or a deletion not yet committed.

    def offenders(self, predicate):
        return [str(path.relative_to(ROOT)) for path, text in self.texts.items() if predicate(text)]

    def test_no_user_profile_paths(self):
        self.assertEqual(self.offenders(PROFILE_PATH.search), [])

    def test_no_paths_or_names_from_this_machine(self):
        values = {str(ROOT), str(ROOT).replace("\\", "/"), str(Path.home()), str(Path.home()).replace("\\", "/")}
        computer = os.environ.get("COMPUTERNAME", "")
        if len(computer) >= 4:
            values.add(computer)
        for value in sorted(values):
            with self.subTest(value="<redacted>"):
                self.assertEqual(self.offenders(lambda text: value.lower() in text.lower()), [])

    def test_only_public_email_addresses(self):
        def private(text):
            return any(not ALLOWED_EMAILS.search(m.group(0)) for m in EMAIL.finditer(text)
                       if "." in m.group(0).rsplit("@", 1)[-1] and not m.group(0).startswith("git@"))
        self.assertEqual(self.offenders(private), [])

    def test_no_built_binaries_or_local_state(self):
        tracked = [str(p.relative_to(ROOT)) for p in self.files
                   if p.suffix.lower() in BUILT_SUFFIXES or p.name.lower() in {"settings.json", ".operation.lock"}]
        self.assertEqual(tracked, [])


if __name__ == "__main__":
    unittest.main()
