#!/usr/bin/env python3
"""Select and validate a HandyPlayer package-cache root without changing state."""

import json
import os
import re
import stat
import subprocess
import sys
from pathlib import Path
from urllib.parse import urlsplit


class InvalidProjectRoot(Exception):
    pass


def require(condition, message):
    if not condition:
        raise InvalidProjectRoot(message)


def canonical_directory(value, label):
    require(isinstance(value, str) and value and os.path.isabs(value),
            f"{label} must be an absolute canonical directory")
    require(not any(ord(char) < 0x20 or ord(char) == 0x7F for char in value),
            f"{label} contains a control character")
    try:
        path = Path(value)
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError, ValueError):
        raise InvalidProjectRoot(f"{label} must be an existing canonical directory") from None
    require(str(resolved) == value and path.is_dir(),
            f"{label} must be an existing canonical directory")

    cursor = path
    while True:
        try:
            info = cursor.lstat()
        except OSError:
            raise InvalidProjectRoot(f"{label} has a missing or inaccessible ancestor") from None
        reparse_point = bool(getattr(info, "st_file_attributes", 0) & 0x400)
        require(not stat.S_ISLNK(info.st_mode) and not reparse_point,
                f"{label} must not use links or junctions")
        if cursor == cursor.parent:
            break
        cursor = cursor.parent
    return path


def git(root, *arguments):
    child_env = os.environ.copy()
    for name in tuple(child_env):
        if name.upper().startswith("GIT_"):
            del child_env[name]
    child_env["GIT_OPTIONAL_LOCKS"] = "0"
    child_env["GIT_TERMINAL_PROMPT"] = "0"
    try:
        result = subprocess.run(
            ["git", "--no-optional-locks", "-C", str(root), *arguments],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=child_env,
            check=False,
        )
    except OSError:
        raise InvalidProjectRoot("Git metadata could not be read") from None
    require(result.returncode == 0, "Git metadata could not be read")
    return result.stdout


def git_line(root, *arguments):
    value = git(root, *arguments)
    require(value.endswith(b"\n"), "Git metadata could not be read")
    value = value[:-1]
    if value.endswith(b"\r"):
        value = value[:-1]
    try:
        return value.decode("utf-8", "strict")
    except UnicodeError:
        raise InvalidProjectRoot("Git metadata is not valid UTF-8") from None


def assert_git_root(root, label):
    top = git_line(root, "rev-parse", "--show-toplevel")
    try:
        top_path = Path(top).resolve(strict=True)
    except (OSError, RuntimeError, ValueError):
        raise InvalidProjectRoot(f"{label} is not the exact Git root") from None
    require(top_path == root, f"{label} is not the exact Git root")


def assert_independent_git_root(root):
    marker = root / ".git"
    try:
        info = marker.lstat()
    except OSError:
        raise InvalidProjectRoot("A different cache root requires an independent release clone") from None
    reparse_point = bool(getattr(info, "st_file_attributes", 0) & 0x400)
    require(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode) and not reparse_point,
            "A different cache root requires an independent release clone")
    assert_git_root(root, "Source checkout")


def read_json_file(root, relative, label, required=True):
    path = root / relative
    cursor = root
    for part in Path(relative).parts:
        cursor = cursor / part
        try:
            info = cursor.lstat()
        except FileNotFoundError:
            if not required and cursor == path:
                return None
            raise InvalidProjectRoot(f"{label} is missing") from None
        except OSError:
            raise InvalidProjectRoot(f"{label} could not be read") from None
        reparse_point = bool(getattr(info, "st_file_attributes", 0) & 0x400)
        require(not stat.S_ISLNK(info.st_mode) and not reparse_point,
                f"{label} must not use a link")
        if cursor == path:
            require(stat.S_ISREG(info.st_mode), f"{label} must be a regular file")
        else:
            require(stat.S_ISDIR(info.st_mode), f"{label} has an invalid parent")
    try:
        if path.stat().st_size > 1024 * 1024:
            raise InvalidProjectRoot(f"{label} is too large")
        raw = path.read_text(encoding="utf-8")
        return json.loads(raw, object_pairs_hook=unique_object)
    except InvalidProjectRoot:
        raise
    except (OSError, UnicodeError, ValueError):
        raise InvalidProjectRoot(f"{label} is not valid JSON") from None


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def assert_owner(marker, keys, label):
    require(isinstance(marker, dict) and set(marker) == set(keys),
            f"{label} has the wrong fields")
    require(marker.get("owner") == "request-release" and
            type(marker.get("version")) is int and marker["version"] == 1 and
            marker.get("project") == "HandyPlayer",
            f"{label} has the wrong project identity")


def safe_worktree_directory(value):
    require(isinstance(value, str) and 0 < len(value) <= 240,
            "Release worktree directory must be a safe relative path")
    require(not value.startswith("/") and not re.search(r"[\\:\x00-\x1f\x7f{}<>\"|?*\[\]]", value),
            "Release worktree directory must be a safe relative path")
    parts = value.split("/")
    require(all(part not in ("", ".", "..") for part in parts),
            "Release worktree directory must be a safe relative path")
    for part in parts:
        require(part.lower() != ".git" and not part.endswith((".", " ")),
                "Release worktree directory contains a reserved path")
        require(not re.match(r"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", part, re.I),
                "Release worktree directory contains a reserved path")
    return value


def credential_free_origin(value):
    if not value or any(char.isspace() or ord(char) < 0x20 or ord(char) == 0x7F for char in value):
        return False
    if value.startswith("https://"):
        if "?" in value or "#" in value:
            return False
        try:
            parsed = urlsplit(value)
            if (parsed.scheme != "https" or not parsed.hostname or len(parsed.path) <= 1 or
                    parsed.username is not None or parsed.password is not None or "@" in parsed.netloc):
                return False
            # Accessing .port validates both syntax and range when present.
            parsed.port
            return True
        except ValueError:
            return False
    return re.fullmatch(r"git@[^:/@?#\s]+:[^?#\s]+", value) is not None


def origin(root):
    value = git(root, "config", "--local", "--no-includes", "--null", "--get-all", "remote.origin.url")
    require(value.endswith(b"\0") and value.count(b"\0") == 1 and len(value) > 1,
            "Exactly one local, non-included NUL-terminated Git origin is required")
    value = value[:-1]
    try:
        decoded = value.decode("utf-8", "strict")
    except UnicodeError:
        raise InvalidProjectRoot("Credential-free Git origin is required") from None
    require(credential_free_origin(decoded), "Credential-free Git origin is required")
    return decoded


def select_root(checkout_value, default_value, hint_value):
    default_root = canonical_directory(default_value, "Default cache root")
    if not hint_value:
        return str(default_root)

    hint_root = canonical_directory(hint_value, "Build project root hint")
    assert_git_root(hint_root, "Build project root hint")
    if hint_root == default_root:
        return str(default_root)

    checkout = canonical_directory(checkout_value, "Source checkout")
    assert_independent_git_root(checkout)

    manifest = read_json_file(checkout, "eng/release-targets.json", "Release target manifest")
    require(isinstance(manifest, dict) and
            type(manifest.get("schemaVersion")) is int and manifest["schemaVersion"] == 1 and
            manifest.get("project") == "HandyPlayer",
            "Release target manifest identity is invalid")
    worktree_directory = safe_worktree_directory(manifest.get("worktreeDirectory"))
    targets = manifest.get("targets")
    require(isinstance(targets, dict) and bool(targets), "Release target manifest has no targets")

    clone_marker = read_json_file(checkout, ".request-release-owner.json", "Release clone owner marker")
    assert_owner(clone_marker, ("owner", "version", "project", "target", "root"),
                 "Release clone owner marker")
    target = clone_marker.get("target")
    require(isinstance(target, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}", target) and
            target in targets and clone_marker.get("root") == hint_value,
            "Release clone marker target or root differs")

    expected_checkout = hint_root / worktree_directory / f"release-{target}"
    require(checkout == expected_checkout,
            "Build project hint must own this checkout at its exact declared release target path")

    outer_marker = read_json_file(hint_root, ".request-release-owner.json",
                                  "Outer project owner marker", required=False)
    if outer_marker is not None:
        assert_owner(outer_marker, ("owner", "version", "project"), "Outer project owner marker")

    require(origin(hint_root) == origin(checkout),
            "Build project root must have the same credential-free origin as this checkout")
    return str(hint_root)


def main(argv):
    if len(argv) != 4:
        print("Usage: project-root.py CHECKOUT DEFAULT_ROOT HINT", file=sys.stderr)
        return 2
    try:
        selected = select_root(argv[1], argv[2], argv[3])
    except InvalidProjectRoot as error:
        print(f"Build project root validation failed: {error}", file=sys.stderr)
        return 1
    print(selected)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
