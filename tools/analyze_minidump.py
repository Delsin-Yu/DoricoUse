#!/usr/bin/env python3
"""Read a Windows minidump without a debugger.

It prints the exception that ended the process, which module the faulting address falls in, and a pseudo-stack:
every pointer-sized value on the faulting thread's stack that points into a loaded module, with its offset. That
is enough to tell "our script call went into Dorico's score wrapper" from "Qt crashed", which is the question a
crash dump is usually asked.

    python tools/analyze_minidump.py "%LOCALAPPDATA%\\CrashDumps\\Dorico6.exe.42948.dmp"
"""

import struct
import sys

MDMP_MAGIC = b"MDMP"
STREAM_THREAD_LIST = 3
STREAM_MODULE_LIST = 4
STREAM_EXCEPTION = 6
STREAM_MEMORY_LIST = 5


def read_string(data, rva):
    (length,) = struct.unpack_from("<I", data, rva)
    return data[rva + 4 : rva + 4 + length].decode("utf-16-le", "replace")


def analyze(path):
    with open(path, "rb") as handle:
        data = handle.read()

    if data[:4] != MDMP_MAGIC:
        raise SystemExit(f"{path} is not a minidump")

    _, _, stream_count, directory_rva = struct.unpack_from("<4sIII", data, 0)
    print(f"dump     {path}")
    print(f"size     {len(data):,} bytes, {stream_count} streams")

    streams = {}
    for index in range(stream_count):
        stream_type, size, rva = struct.unpack_from("<III", data, directory_rva + index * 12)
        streams.setdefault(stream_type, (size, rva))

    # ---------------------------------------------------------------- modules
    modules = []
    if STREAM_MODULE_LIST in streams:
        _, rva = streams[STREAM_MODULE_LIST]
        (count,) = struct.unpack_from("<I", data, rva)
        for index in range(count):
            base, image_size, _checksum, stamp, name_rva = struct.unpack_from(
                "<QIIII", data, rva + 4 + index * 108
            )
            modules.append((base, image_size, read_string(data, name_rva), stamp))

    print(f"modules  {len(modules)}")

    def locate(address):
        for base, size, name, _stamp in modules:
            if base <= address < base + size:
                return f"{name}+0x{address - base:x}"
        return None

    # ---------------------------------------------------------------- exception
    if STREAM_EXCEPTION not in streams:
        print("exception stream missing")
        return

    _, rva = streams[STREAM_EXCEPTION]
    thread_id, _align = struct.unpack_from("<II", data, rva)
    (
        code,
        flags,
        _nested,
        address,
        parameters,
        _reserved,
    ) = struct.unpack_from("<IIQQII", data, rva + 8)

    names = {
        0xC0000005: "ACCESS_VIOLATION",
        0xC000001D: "ILLEGAL_INSTRUCTION",
        0xC0000094: "INTEGER_DIVIDE_BY_ZERO",
        0xC00000FD: "STACK_OVERFLOW",
        0xC0000374: "HEAP_CORRUPTION",
        0x80000003: "BREAKPOINT",
        0xE06D7363: "C++ EXCEPTION",
        0xC0000409: "STACK_BUFFER_OVERRUN (fail fast)",
    }

    detail = ""
    if code == 0xC0000005 and parameters >= 2:
        operation, target = struct.unpack_from("<QQ", data, rva + 8 + 32)
        access = {0: "read", 1: "write", 8: "execute"}.get(operation, str(operation))
        detail = f" ({access} at 0x{target:x} -> {locate(target) or 'unmapped'})"

    print()
    print(f"crash    thread {thread_id}")
    print(f"         {names.get(code, hex(code))}{detail}")
    print(f"         at 0x{address:x} -> {locate(address) or 'unmapped'}")

    # ---------------------------------------------------------------- pseudo-stack
    stack = None
    if STREAM_THREAD_LIST in streams:
        _, rva = streams[STREAM_THREAD_LIST]
        (count,) = struct.unpack_from("<I", data, rva)
        for index in range(count):
            offset = rva + 4 + index * 48
            (tid,) = struct.unpack_from("<I", data, offset)
            start, data_size, data_rva = struct.unpack_from("<QII", data, offset + 24)
            if tid == thread_id:
                stack = (start, data_size, data_rva, count)
                break

    if stack is None:
        print("stack of the faulting thread not found")
        return

    start, data_size, data_rva, thread_count = stack
    print(f"         stack: 0x{start:x}, {data_size:,} bytes, of {thread_count} threads")

    print()
    print("stack scan (values that point into a module, innermost first):")
    words = data[data_rva : data_rva + data_size]
    seen = 0
    for index in range(0, len(words) - 8, 8):
        (value,) = struct.unpack_from("<Q", words, index)
        if value == 0:
            continue
        where = locate(value)
        if where:
            print(f"  0x{start + index:x}  {where}")
            seen += 1
            if seen >= 60:
                print("  ...")
                break

    # ---------------------------------------------------------------- nearest strings
    print()
    print("what the faulting thread had been doing (strings near the stack, sampled):")
    interesting = []
    for match in range(len(words) - 4):
        chunk = words[match : match + 4]
        if chunk.isascii() and chunk.isalpha() and len(chunk.strip()) == 4:
            pointer = start + match
            inner = words[match : match + 64].split(b"\x00")[0]
            if 8 <= len(inner) <= 60 and all(32 <= byte < 127 for byte in inner):
                interesting.append((pointer, inner.decode()))
    for pointer, text in interesting[:25]:
        print(f"  0x{pointer:x}  {text}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    analyze(sys.argv[1])
