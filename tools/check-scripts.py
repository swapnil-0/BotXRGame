#!/usr/bin/env python3
"""
Cheap pre-commit checks for the Unity C# scripts.

Run before committing, and WAIT for the result:

    python3 tools/check-scripts.py

These catch a narrow class of mistake. They are not a compiler, and they have
already been wrong twice - both times because the checker itself was naive.
Unity's console is the real check. This exists to catch the fast, obvious
things before burning a build cycle on them.

Caught so far:
  - unbalanced braces/parens
  - [Header]/[Tooltip]/[Range] on a property (CS0592) - made twice
"""

import glob
import os
import re
import sys

ATTRS = ("Header", "Tooltip", "Range", "SerializeField", "HideInInspector", "Space")


def strip_code(src):
    """
    Remove comments and string literals, in that priority order.

    Strings must be recognised BEFORE line comments. Stripping "//" first lets
    the slashes inside any https:// URL in a string literal swallow the rest of
    the line, which silently unbalances the counts and reports a correct file as
    broken. That exact false positive happened on a file containing a GitHub URL.
    """
    out = []
    i, n = 0, len(src)
    in_str = in_chr = in_line = in_blk = False

    while i < n:
        c = src[i]
        nxt = src[i + 1] if i + 1 < n else ""

        if in_line:
            if c == "\n":
                in_line = False
                out.append(c)
        elif in_blk:
            if c == "*" and nxt == "/":
                in_blk = False
                i += 1
        elif in_str:
            if c == "\\":
                i += 1
            elif c == '"':
                in_str = False
        elif in_chr:
            if c == "\\":
                i += 1
            elif c == "'":
                in_chr = False
        else:
            if c == "/" and nxt == "/":
                in_line = True
                i += 1
            elif c == "/" and nxt == "*":
                in_blk = True
                i += 1
            elif c == '"':
                in_str = True
            elif c == "'":
                in_chr = True
            else:
                out.append(c)
        i += 1

    return "".join(out)


def check_balance(path):
    t = strip_code(open(path, encoding="utf-8", errors="replace").read())
    b = t.count("{") - t.count("}")
    p = t.count("(") - t.count(")")
    if b or p:
        return f"unbalanced: braces {b:+d} parens {p:+d}"
    return None


def check_attr_on_property(path):
    """
    [Header] and friends are AttributeTargets.Field. On a property they are
    CS0592, which no amount of brace counting will reveal.
    """
    lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
    problems = []
    for i, ln in enumerate(lines):
        s = ln.strip()
        if not s.startswith("[") or not any(a in s for a in ATTRS):
            continue
        for nxt in lines[i + 1:]:
            t = nxt.strip()
            if not t or t.startswith("[") or t.startswith("//"):
                continue
            if "{ get;" in t or "=> " in t:
                problems.append(f"line {i + 1}: {s} on a property (CS0592)")
            break
    return problems


def main():
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
    base = os.path.join(root, "unity", "BotXRGame", "Assets")

    files = sorted(
        glob.glob(os.path.join(base, "Scripts", "*.cs"))
        + glob.glob(os.path.join(base, "Editor", "*.cs"))
    )

    if not files:
        print("no scripts found - wrong working directory?")
        return 1

    failures = 0
    for f in files:
        rel = os.path.relpath(f, root)

        bal = check_balance(f)
        if bal:
            print(f"FAIL {rel}: {bal}")
            failures += 1

        for p in check_attr_on_property(f):
            print(f"FAIL {rel}: {p}")
            failures += 1

    print(f"\n{len(files)} files checked, {failures} problem(s).")
    print("Reminder: this is not a compiler. Unity's console is the real check.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
