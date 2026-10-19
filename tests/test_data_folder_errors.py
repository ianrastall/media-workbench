"""A file error from a missing or full data drive reaches the user as the plain explanation, not a Python traceback."""
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "workers/python"))
import media_worker  # noqa: E402


class DataFolderProblemTests(unittest.TestCase):
    def test_a_vanished_drive_is_explained_plainly(self):
        # The drive-containment trace unplugged the data folder's drive mid-picture and saw a raw FileNotFoundError.
        missing = str(Path(tempfile.gettempdir()) / "media-workbench-unplugged-森林" / "Media Workbench")
        error = FileNotFoundError(3, "The system cannot find the path specified", "T:\\")
        message = media_worker.data_folder_problem(error, missing)
        self.assertIn("could not use its data folder", message)
        self.assertIn("reconnect it", message)
        self.assertNotIn("FileNotFoundError", message)

    def test_a_full_drive_is_explained_plainly(self):
        with tempfile.TemporaryDirectory() as folder:
            error = OSError(28, "There is not enough space on the disk")
            error.winerror = 112
            message = media_worker.data_folder_problem(error, folder)
            self.assertIn("is full", message)
            self.assertIn("move the data folder in Settings", message)

    def test_other_errors_keep_their_details(self):
        with tempfile.TemporaryDirectory() as folder:
            self.assertIsNone(media_worker.data_folder_problem(PermissionError(13, "Access is denied"), folder))
            self.assertIsNone(media_worker.data_folder_problem(ValueError("not a file error"), folder))
            self.assertIsNone(media_worker.data_folder_problem(FileNotFoundError(2, "missing"), None))


if __name__ == "__main__":
    unittest.main()
