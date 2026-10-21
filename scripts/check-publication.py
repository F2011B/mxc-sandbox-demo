#!/usr/bin/env python3
"""Check the exact Git history being published without printing matched data."""
import argparse
import json
import re
import subprocess
import sys

IDENTITY = ("Dark Light", "darklight@noreply.com")
RULES = {
    "personal-user-path": re.compile(r"(?i)(?:/(?:Users|home)/[^/\\\s\"'<>`]+|[A-Z]:\\+(?:Users|Benutzer)\\+[^\\\s\"'<>`]+)"),
    "private-volume-path": re.compile(r"/(?:Volumes)/[^/\s\"'<>`]+"),
    "user-sid": re.compile(r"S-1-5-21-(?:\d+-){2,}\d+"),
    "credential": re.compile(r"(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|sk-[A-Za-z0-9]{20,})"),
    "email": re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
}
FORBIDDEN_EXTENSIONS = {".exe", ".dll", ".pdb", ".so", ".dylib", ".zip", ".tar", ".gz", ".bundle"}


def git(*args):
    return subprocess.check_output(["git", *args], stderr=subprocess.PIPE)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ref", nargs="?", default="HEAD")
    parser.add_argument("--identifiers-stdin", action="store_true")
    args = parser.parse_args()
    private = json.load(sys.stdin) if args.identifiers_stdin else []
    if not isinstance(private, list) or any(not isinstance(item, str) or not item for item in private):
        parser.error("stdin must contain a JSON array of nonempty strings")
    private_patterns = [re.compile(r"(?<!\w)" + re.escape(item) + r"(?!\w)", re.IGNORECASE) for item in private]
    failures = []

    def inspect(label, data):
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError:
            failures.append((label, "non-text-data"))
            return
        if "\0" in text:
            failures.append((label, "binary-data"))
            return
        for category, pattern in RULES.items():
            matches = list(pattern.finditer(text))
            if category == "email":
                matches = [match for match in matches if match.group().lower() != IDENTITY[1]]
            if matches:
                failures.append((label, category))
        if any(pattern.search(text) for pattern in private_patterns):
            failures.append((label, "private-identifier"))

    commits = git("rev-list", args.ref).decode().splitlines()
    if not commits:
        raise ValueError("No commits to inspect")
    seen = set()
    for commit in commits:
        metadata = git("show", "-s", "--format=%an%x00%ae%x00%cn%x00%ce%x00%B", commit).split(b"\0", 4)
        if tuple(item.decode() for item in metadata[:4]) != IDENTITY + IDENTITY:
            failures.append(("commit:" + commit[:12], "unexpected-author-or-committer"))
        inspect("commit:" + commit[:12], b"\n".join(metadata))
        for entry in git("ls-tree", "-r", "-z", commit).split(b"\0"):
            if not entry:
                continue
            fields, filename = entry.split(b"\t", 1)
            mode, kind, oid = fields.split()
            inspect("path:" + commit[:12], filename)
            path = filename.decode("utf-8", errors="replace")
            parts = path.lower().split("/")
            if any(part in {"bin", "obj", "artifacts", ".git"} for part in parts) or any(path.lower().endswith(ext) for ext in FORBIDDEN_EXTENSIONS):
                failures.append(("path:" + commit[:12], "build-or-binary-artifact"))
            if kind != b"blob":
                failures.append(("tree:" + commit[:12], "unsupported-git-entry"))
            elif oid not in seen:
                seen.add(oid)
                inspect("blob:" + oid.decode()[:12], git("cat-file", "blob", oid.decode()))
    if failures:
        for label, category in sorted(set(failures)):
            print("FAIL", label, category)
        print("Publication refused; matched values are withheld.")
        return 1
    print(f"PASS publication privacy: {len(commits)} commits, {len(seen)} text blobs; authors/committers use the approved pseudonym.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (subprocess.CalledProcessError, ValueError, UnicodeError) as error:
        print("Publication check failed:", type(error).__name__, file=sys.stderr)
        raise SystemExit(2)
