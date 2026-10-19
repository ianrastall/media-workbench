"""Headless checks for the drive-containment trace tooling in scripts/containment (no Process Monitor or GPU)."""
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts" / "containment"))
import acceptance  # noqa: E402
import procmon_config  # noqa: E402
import summarize_writes  # noqa: E402


class FillerTests(unittest.TestCase):
    def test_allocation_sets_the_size_without_writing(self):
        import tempfile
        with tempfile.TemporaryDirectory() as folder:
            filler = Path(folder) / "filler.bin"
            acceptance.allocate(filler, 3 * 1024 ** 2)
            self.assertEqual(filler.stat().st_size, 3 * 1024 ** 2)
            acceptance.allocate(filler, 1024)  # Replaces, never grows an older filler.
            self.assertEqual(filler.stat().st_size, 1024)


def records(data):
    offset, found = 0, {}
    while offset < len(data):
        size, header, header_and_name, data_size = struct.unpack_from("<IIII", data, offset)
        assert header == 16 and size == header_and_name + data_size
        name = data[offset + 16:offset + header_and_name].decode("utf-16-le").rstrip("\0")
        found[name] = data[offset + header_and_name:offset + size]
        offset += size
    return found


class ProcmonConfigurationTests(unittest.TestCase):
    def test_records_are_consistent_and_rules_parse_back(self):
        for terminator in (True, False):
            found = records(procmon_config.configuration(terminator=terminator))
            self.assertEqual(struct.unpack("<I", found["DestructiveFilter"])[0], 1)
            rules = found["FilterRules"]
            version, count = struct.unpack_from("<BI", rules)
            self.assertEqual((version, count), (1, len(list(procmon_config.rules()))))
            offset, parsed = 5, []
            for _ in range(count):
                column, relation, action, length = struct.unpack_from("<IIBI", rules, offset)
                offset += 13
                value = rules[offset:offset + length].decode("utf-16-le").rstrip("\0")
                offset += length + 8
                parsed.append((column, relation, action, value))
            self.assertEqual(offset, len(rules))
            self.assertEqual(parsed, list(procmon_config.rules()))

    def test_file_changes_and_processes_are_kept(self):
        kept = [rule for rule in procmon_config.rules() if rule[2] == procmon_config.INCLUDE]
        self.assertEqual({rule[3] for rule in kept}, {"File System", "Process"})
        excluded = {rule[3] for rule in procmon_config.rules() if rule[2] == procmon_config.EXCLUDE}
        self.assertFalse(excluded & {"WriteFile", "CreateFile", "SetRenameInformationFile", "SetDispositionInformationFile", "Process Create", "Process Exit"})


class ClassificationTests(unittest.TestCase):
    def test_changes(self):
        classify = summarize_writes.classify
        self.assertEqual(classify("WriteFile", "SUCCESS", "Offset: 0, Length: 65,536, Priority: Normal"), ("write", 65536))
        self.assertEqual(classify("CreateFile", "SUCCESS", "Desired Access: Generic Write, Read Attributes, Disposition: OverwriteIf, "
                                  "Options: Synchronous IO Non-Alert, Non-Directory File, Attributes: N, ShareMode: Read, Write, AllocationSize: 0, OpenResult: Created"), ("create", 0))
        self.assertEqual(classify("CreateFile", "SUCCESS", "Desired Access: Generic Read/Write, Disposition: Open, ShareMode: Read, OpenResult: Opened"), ("open-write", 0))
        self.assertIsNone(classify("CreateFile", "SUCCESS", "Desired Access: Generic Read, Disposition: Open, ShareMode: Read, Write, OpenResult: Opened"))
        self.assertIsNone(classify("CreateFile", "NAME NOT FOUND", "Desired Access: Generic Write, Disposition: Create"))
        self.assertEqual(classify("SetDispositionInformationEx", "SUCCESS", "Flags: FILE_DISPOSITION_DELETE, FILE_DISPOSITION_POSIX_SEMANTICS"), ("delete", 0))
        self.assertIsNone(classify("SetDispositionInformationFile", "SUCCESS", "Delete: False"))
        self.assertEqual(classify("SetRenameInformationFile", "SUCCESS", "ReplaceIfExists: True, FileName: D:\\x\\b.txt"), ("rename", 0))
        self.assertIsNone(classify("FileSystemControl", "SUCCESS", "Control: FSCTL_QUERY_ALLOCATED_RANGES"))
        self.assertIsNone(classify("QueryDirectory", "SUCCESS", ""))


class SummaryTests(unittest.TestCase):
    environment = [("%TEMP%", "c:\\profiles\\someone\\appdata\\local\\temp"), ("%LOCALAPPDATA%", "c:\\profiles\\someone\\appdata\\local"), ("%USERPROFILE%", "c:\\profiles\\someone")]

    def test_data_and_app_folders_are_labelled(self):
        environment = summarize_writes.environment_folders("d:\\run\\data", "c:\\profiles\\someone\\appdata\\local\\programs\\media workbench")
        self.assertEqual(summarize_writes.anonymize("D:\\run\\data\\jobs\\a.json", environment), "<data>\\jobs\\a.json")
        self.assertEqual(summarize_writes.anonymize("C:\\Profiles\\Someone\\AppData\\Local\\Programs\\Media Workbench\\x.pyc", environment), "<app>\\x.pyc")

    def row(self, process, pid, operation, path, detail="", result="SUCCESS"):
        return {"Process Name": process, "PID": str(pid), "Operation": operation, "Path": path, "Result": result, "Detail": detail}

    def test_only_the_app_tree_counts_and_areas_are_grouped(self):
        data, app = "d:\\run\\data", "c:\\profiles\\someone\\appdata\\local\\programs\\media workbench"
        rows = [
            self.row("python.exe", 10, "Process Create", "C:\\Profiles\\Someone\\AppData\\Local\\Programs\\Media Workbench\\MediaWorkbench.Cli.exe", "PID: 100, Command line: mcp"),
            self.row("MediaWorkbench.Cli.exe", 100, "Process Create", "C:\\x\\python.exe", "PID: 200, Command line: worker"),
            self.row("python.exe", 200, "WriteFile", "D:\\run\\data\\temp\\work\\abc\\x.tmp", "Offset: 0, Length: 1,024"),
            self.row("python.exe", 200, "WriteFile", "C:\\Profiles\\Someone\\AppData\\Local\\Temp\\leak.tmp", "Offset: 0, Length: 10"),
            self.row("python.exe", 200, "CreateFile", "C:\\Profiles\\Someone\\AppData\\Local\\Programs\\Media Workbench\\workers\\python\\__pycache__\\a.pyc",
                     "Desired Access: Generic Write, Disposition: OverwriteIf, OpenResult: Created"),
            self.row("python.exe", 200, "WriteFile", "\\\\.\\pipe\\x", "Length: 5"),
            self.row("uv.exe", 200, "WriteFile", "D:\\Other Program", "Offset: 4,096, Length: 4,096, I/O Flags: Non-cached, Paging I/O, Synchronous Paging I/O"),
            self.row("uv.exe", 200, "WriteFile", "D:\\$Mft", "Offset: 0, Length: 4,096, I/O Flags: Non-cached, Paging I/O"),
            self.row("Explorer.EXE", 300, "WriteFile", "C:\\Profiles\\Someone\\elsewhere.txt", "Length: 99"),
            self.row("python.exe", 200, "Process Exit", "", ""),
            self.row("other.exe", 200, "WriteFile", "C:\\reused-pid.txt", "Length: 7"),
        ]
        areas = summarize_writes.summarize_phase(rows, data, app, self.environment)
        self.assertEqual(set(areas), {"data/temp", "%TEMP%\\leak.tmp", "app/workers", "device", "D:\\Other Program", "ntfs-metadata/D:"})
        self.assertEqual(areas["D:\\Other Program"]["kinds"], {"paging": 1})
        self.assertEqual(areas["data/temp"]["bytes"], 1024)
        self.assertEqual(areas["app/workers"]["created"], 1)
        self.assertEqual(set(summarize_writes.outside(areas)), {"%TEMP%\\leak.tmp", "app/workers"})

    def test_profile_folders_are_named_by_variable(self):
        area = summarize_writes.area_of("C:\\Profiles\\Someone\\AppData\\Local\\NVIDIA\\GLCache\\x\\y.bin", "d:\\data", "d:\\app", self.environment)
        self.assertEqual(area, "%LOCALAPPDATA%\\NVIDIA\\GLCache")
        self.assertEqual(summarize_writes.area_of("D:\\data\\jobs\\a.json", "d:\\data", "d:\\app", self.environment), "data/jobs")
        self.assertEqual(summarize_writes.area_of("D:\\data\\.operation.lock", "d:\\data", "d:\\app", self.environment), "data/(top level)")


if __name__ == "__main__":
    unittest.main()
