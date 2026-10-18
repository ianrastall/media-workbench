"""Write a Process Monitor configuration (.pmc) that keeps only file-system changes and process events.

The binary layout follows the open-source procmon-parser project (MIT): a .pmc file is a sequence of records,
each a 16-byte header (record size, header size 0x10, header-plus-name size, data size), a UTF-16 name with a
terminator, and the data. FilterRules data is a version byte (1), a rule count (uint32), and the rules: column
(uint32), relation (uint32), action (uint8), value length in bytes (uint32), the UTF-16 value, an integer value
(uint32) and a reserved uint32. Whether the value carries a UTF-16 terminator is not certain, so both variants
can be written; trace-controller.ps1 checks which one Process Monitor obeys before tracing anything.

Usage: python procmon_config.py <output.pmc> [--no-terminator]
"""
import struct
import sys

# Column, relation and action values (procmon-parser's consts).
PROCESS_NAME, OPERATION, EVENT_CLASS = 40053, 40055, 40082
IS = 0
EXCLUDE, INCLUDE = 0, 1

# Reads, queries and bookkeeping that never change a file. Leaving one out only adds noise; the summary
# classifies every remaining event itself.
READ_ONLY_OPERATIONS = [
    "ReadFile", "QueryDirectory", "CloseFile", "Thread Create", "Thread Exit", "Load Image", "QueryOpen",
    "QueryBasicInformationFile", "QueryStandardInformationFile", "QueryNetworkOpenInformationFile",
    "QueryAttributeTagFile", "QueryEAFile", "QuerySecurityFile", "QueryNameInformationFile",
    "QueryNormalizedNameInformationFile", "QueryAllInformationFile", "QueryStreamInformationFile",
    "QueryCompressionInformationFile", "QueryIdInformation", "QueryFileInternalInformationFile",
    "QueryPositionInformationFile", "QueryRemoteProtocolInformation", "QueryInformationVolume",
    "QueryVolumeInformation", "QuerySizeInformationVolume", "QueryAttributeInformationVolume",
    "QueryDeviceInformationVolume", "QueryFullSizeInformationVolume", "QueryCaseSensitiveInformationFile",
    "QueryHardLinkInformationFile", "QueryLinkInformationFile", "QueryStatInformationFile",
    "FASTIO_ACQUIRE_FOR_SECTION_SYNCHRONIZATION", "FASTIO_RELEASE_FOR_SECTION_SYNCHRONIZATION",
    "FASTIO_ACQUIRE_FOR_CC_FLUSH", "FASTIO_RELEASE_FOR_CC_FLUSH", "FASTIO_ACQUIRE_FOR_MOD_WRITE",
    "FASTIO_RELEASE_FOR_MOD_WRITE", "FASTIO_NETWORK_QUERY_OPEN", "FASTIO_CHECK_IF_POSSIBLE",
    "CreateFileMapping", "LockFile", "UnlockFileSingle", "UnlockFileAll", "NotifyChangeDirectory",
    "DeviceIoControl", "IRP_MJ_CLOSE", "IRP_MJ_READ",
]
TRACER_PROCESSES = ["Procmon.exe", "Procmon64.exe", "Procmon64a.exe", "System"]


def rules():
    yield EVENT_CLASS, IS, INCLUDE, "File System"
    yield EVENT_CLASS, IS, INCLUDE, "Process"
    for name in TRACER_PROCESSES:
        yield PROCESS_NAME, IS, EXCLUDE, name
    for operation in READ_ONLY_OPERATIONS:
        yield OPERATION, IS, EXCLUDE, operation


def rule_bytes(column, relation, action, value, terminator=True):
    text = value.encode("utf-16-le") + (b"\0\0" if terminator else b"")
    number = int(value) if value.isdigit() else 0
    return struct.pack("<IIBI", column, relation, action, len(text)) + text + struct.pack("<II", number, 0)


def record(name, data):
    encoded = name.encode("utf-16-le") + b"\0\0"
    return struct.pack("<IIII", 16 + len(encoded) + len(data), 16, 16 + len(encoded), len(data)) + encoded + data


def configuration(terminator=True):
    rule_list = list(rules())
    filters = struct.pack("<BI", 1, len(rule_list)) + b"".join(rule_bytes(*rule, terminator=terminator) for rule in rule_list)
    # DestructiveFilter drops excluded events instead of only hiding them, keeping the backing file small.
    return record("FilterRules", filters) + record("DestructiveFilter", struct.pack("<I", 1))


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    with open(sys.argv[1], "wb") as output:
        output.write(configuration(terminator="--no-terminator" not in sys.argv))


if __name__ == "__main__":
    main()
