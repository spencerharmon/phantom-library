"""Exercise the real bootstrap against isolated repositories, without network."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("p0-prepare-jellyfin.sh")


class BootstrapTests(unittest.TestCase):
    def test_empty_wrong_tip_missing_object_and_dirty_checkout(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp)
            fork, hive = base / "fork", base / "hive"
            env = dict(os.environ, GIT_AUTHOR_NAME="Test", GIT_AUTHOR_EMAIL="test@example.com",
                       GIT_COMMITTER_NAME="Test", GIT_COMMITTER_EMAIL="test@example.com")
            env.pop("PHANTOM_JELLYFIN_DIR", None)
            env.pop("PHANTOM_JELLYFIN_REPO", None)

            def git(where, *args):
                return subprocess.check_output(["git", "-C", str(where), *args], env=env, text=True).strip()

            for repo in (fork, hive):
                repo.mkdir()
                git(repo, "init", "-q")
            project = fork / "MediaBrowser.Controller/MediaBrowser.Controller.csproj"
            project.parent.mkdir()
            project.write_text("pinned\n")
            git(fork, "add", ".")
            git(fork, "commit", "-qm", "pin")
            pin = git(fork, "rev-parse", "HEAD")
            project.write_text("wrong-tip\n")
            git(fork, "commit", "-qam", "advance")
            tip = git(fork, "rev-parse", "HEAD")
            script = hive / "scripts/tests/p0-prepare-jellyfin.sh"
            script.parent.mkdir(parents=True)
            shutil.copyfile(SCRIPT, script)
            (hive / ".gitmodules").write_text(
                f'[submodule "jellyfin"]\n path = jellyfin\n url = {fork.as_uri()}\n')
            git(hive, "add", ".")
            git(hive, "update-index", "--add", "--cacheinfo", f"160000,{pin},jellyfin")
            git(hive, "commit", "-qm", "fixture")

            def prepare(success=True):
                result = subprocess.run(["bash", str(script)], env=env, text=True, capture_output=True)
                self.assertEqual(result.returncode == 0, success, result.stdout + result.stderr)
                if success:
                    self.assertEqual(git(hive / "jellyfin", "rev-parse", "HEAD"), pin)
                    self.assertEqual((hive / "jellyfin" / project.relative_to(fork)).read_text(), "pinned\n")

            # Depth-one clone has only the newer tip; the helper must fetch the pin.
            (hive / "jellyfin").mkdir()
            prepare()
            prepare()  # idempotent
            git(hive / "jellyfin", "checkout", "--detach", tip)
            prepare()  # csproj exists but HEAD is wrong
            git(hive / "jellyfin", "checkout", "--detach", tip)
            (hive / "jellyfin" / project.relative_to(fork)).write_text("local edit\n")
            prepare(False)
            self.assertEqual(git(hive / "jellyfin", "rev-parse", "HEAD"), tip)


if __name__ == "__main__":
    unittest.main(verbosity=2)
