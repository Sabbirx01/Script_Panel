# -*- coding: utf-8 -*-
"""Toolbox runtime - run one tool script straight out of the panel's own exe.

The panel packs every tool script into ONE encrypted blob that lives inside
``DeveloperXBulkAccPanel.exe`` (see ``temp\\build-toolbox.ps1``).  When a run
starts, the panel spawns::

    python.exe -u -c "<glue>" <tool args...>

and writes on stdin:

    4 bytes   big-endian length of this file
    N bytes   this file (UTF-8)
    4 bytes   big-endian length of the archive
    M bytes   the decrypted toolbox archive (a zip)
    ...       whatever the tool itself expects on stdin (nothing, usually)

The glue is three lines - ``read 4``, ``read N``, ``exec`` - so there is no
quoting or command-line length problem.  This file then

  1. reads the remaining stdin and unpacks the zip into dictionaries.  NOTHING
     is ever written to disk, so there is no ``.py`` for anybody to copy.
  2. installs a meta-path finder, so ``import create``, ``import panel_mail``,
     ``from stealth_bot.engine import StealthBot`` and relative imports inside
     ``stealth_bot`` all resolve out of those dictionaries.
  3. shims ``os.stat`` / ``listdir`` / ``open`` so the tools' own probes -
     ``os.path.isfile(node/".."/"_shared"/"panel_browser.py")``,
     ``Path.exists()``, ``open("stealth_bot/proxy.txt")`` - answer correctly
     for the virtual tree.
  4. exec's the requested entry script as ``__main__``.

Environment, set by the panel:

    PANEL_TOOLBOX_ROOT    where the archive is mounted.  This path deliberately
                          does NOT exist on disk - it is only a name.
    PANEL_TOOLBOX_ENTRY   which script to run, e.g. ``zai/Zcreatev2.py``.
    PANEL_TOOLBOX_DEBUG   ``1`` -> dump what was mounted, then continue.
"""

import io
import os
import os.path
import sys
import time
import zipfile

try:
    import builtins
except ImportError:                                     # pragma: no cover
    import __builtin__ as builtins  # type: ignore


# --------------------------------------------------------------------- paths

def _key(path):
    """Absolute, case-folded, backslash-normalised key for one path.

    '' when the value is not a usable path at all (None, an int file
    descriptor, ...).  Everything the virtual tree stores is keyed by this, and
    every probe the tools make is normalised through it, so ``Path("a/b.txt")``
    and ``"a\\\\b.txt"`` land on the same entry.
    """
    if isinstance(path, bytes):
        try:
            path = os.fsdecode(path)
        except Exception:                               # noqa: BLE001
            return ""
    try:
        path = os.fspath(path)
    except TypeError:
        return ""
    if not isinstance(path, str) or not path:
        return ""
    try:
        if not os.path.isabs(path):
            path = os.path.join(os.getcwd(), path)
        return os.path.normcase(os.path.normpath(path))
    except Exception:                                   # noqa: BLE001
        return ""


class _Tree(object):
    """The archive in memory: file bytes by key, plus the directory set."""

    def __init__(self, root):
        self.root = _key(root)
        self.files = {}
        self.dirs = set([self.root])

    def add(self, name, data):
        name = name.replace("\\", "/").strip("/")
        if not name or name.endswith("/"):
            return
        full = _key(os.path.join(self.root, *name.split("/")))
        if not full:
            return
        self.files[full] = data
        node = os.path.dirname(full)
        while len(node) > len(self.root):
            self.dirs.add(node)
            node = os.path.dirname(node)

    def is_file(self, path):
        return _key(path) in self.files

    def is_dir(self, path):
        return _key(path) in self.dirs


# --------------------------------------------------------------------- loader

class _Source(object):
    """Compiles one module out of the in-memory tree, writes nothing anywhere."""

    def __init__(self, tree, path, is_package, package_dir):
        self.tree = tree
        self.path = path
        self.is_package = is_package
        self.package_dir = package_dir

    def create_module(self, spec):
        return None                                     # default module object

    def exec_module(self, module):
        module.__file__ = self.path
        module.__loader__ = self
        if self.is_package and self.package_dir:
            module.__path__ = [self.package_dir]
        code = compile(self.tree.files[_key(self.path)], self.path, "exec")
        exec(code, module.__dict__)

    def get_filename(self, fullname=None):
        return self.path

    def get_source(self, fullname=None):
        try:
            return self.tree.files[_key(self.path)].decode("utf-8")
        except Exception:                               # noqa: BLE001
            return None

    def get_data(self, path):
        key = _key(path)
        if key in self.tree.files:
            return self.tree.files[key]
        raise IOError(path)

    def is_package(self, fullname=None):                # noqa: A003
        return self.is_package


class _Finder(object):
    """``import X`` resolved from the archive: this tool's folder, then _shared."""

    def __init__(self, tree, tool):
        self.tree = tree
        self.bases = [os.path.join(tree.root, tool),
                      os.path.join(tree.root, "_shared")]

    # -- helpers

    def _pick(self, base, leaf):
        """(path, is_package, package_dir) for `leaf` inside `base`, else None."""
        module = os.path.join(base, leaf + ".py")
        if self.tree.is_file(module):
            return module, False, None
        package_dir = os.path.join(base, leaf)
        init = os.path.join(package_dir, "__init__.py")
        if self.tree.is_file(init):
            return init, True, package_dir
        return None, None, None

    def _package_dir(self, dotted):
        """The virtual folder behind an already-imported package name."""
        for base in self.bases:
            candidate = os.path.join(base, *dotted.split("."))
            if self.tree.is_dir(candidate):
                return candidate
        return None

    def _spec(self, fullname, path, is_package, package_dir):
        import importlib.machinery as machinery
        spec = machinery.ModuleSpec(
            fullname,
            _Source(self.tree, path, is_package, package_dir),
            origin=path,
            is_package=is_package,
        )
        if is_package:
            spec.submodule_search_locations = [package_dir]
        return spec

    # -- meta-path protocol

    def find_spec(self, fullname, path=None, target=None):
        parts = fullname.split(".")
        leaf = parts[-1]
        if len(parts) == 1:
            for base in self.bases:
                hit, is_package, package_dir = self._pick(base, leaf)
                if hit:
                    return self._spec(fullname, hit, is_package, package_dir)
            return None
        parent = fullname.rsplit(".", 1)[0]
        base = self._package_dir(parent)
        if base is None:
            return None
        hit, is_package, package_dir = self._pick(base, leaf)
        if hit:
            return self._spec(fullname, hit, is_package, package_dir)
        return None


# ------------------------------------------------------------------ os shims

TREE = None
_REAL_STAT = os.stat
_REAL_LISTDIR = os.listdir
_REAL_OPEN = builtins.open


def _fake_stat(size):
    """A read-only regular file stat_result."""
    now = time.time()
    try:
        return os.stat_result((0o100444, 0, 0, 1, 0, 0, size, now, now, now))
    except Exception:                                   # noqa: BLE001
        return _REAL_STAT(sys.executable)


def _stat(path, *args, **kwargs):
    key = _key(path)
    if key and key in TREE.files:
        return _fake_stat(len(TREE.files[key]))
    return _REAL_STAT(path, *args, **kwargs)


def _listdir(path="."):
    key = _key(path)
    if key and TREE.is_dir(key):
        prefix = key + os.sep
        names = set()
        for known in list(TREE.files) + list(TREE.dirs):
            if known.startswith(prefix):
                rest = known[len(prefix):]
                if rest and os.sep not in rest:
                    names.add(rest)
        return sorted(names)
    return _REAL_LISTDIR(path)


def _open(file, mode="r", *args, **kwargs):
    """Serve the archive's own data files (proxy.txt); everything else is real."""
    if isinstance(mode, str) and not any(c in mode for c in "wax+"):
        key = _key(file)
        if key and key in TREE.files:
            data = TREE.files[key]
            if "b" in mode:
                return io.BytesIO(data)
            encoding = kwargs.get("encoding")
            if encoding is None and len(args) >= 2:
                encoding = args[1]
            return io.StringIO(data.decode(encoding or "utf-8", "replace"))
    return _REAL_OPEN(file, mode, *args, **kwargs)


# ---------------------------------------------------------------------- boot

def _read_exact(stream, size):
    out = bytearray()
    while len(out) < size:
        chunk = stream.read(size - len(out))
        if not chunk:
            break
        out += chunk
    return bytes(out)


def _mount():
    """Unpack the archive the panel wrote on stdin into the virtual tree."""
    raw = sys.stdin.buffer
    size = int.from_bytes(_read_exact(raw, 4), "big")
    data = _read_exact(raw, size)
    if not data:
        raise SystemExit("toolbox: no payload on stdin")
    tree = _Tree(os.environ.get("PANEL_TOOLBOX_ROOT") or
                 os.path.join(os.environ.get("PANEL_TOOL_STATE_DIR") or os.getcwd(),
                              "__panel__"))
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            if info.is_dir():
                continue
            tree.add(info.filename, archive.read(info))
    # Everything past the archive is the tool's own stdin - some of them still
    # ask "how many accounts?" on it.  Hand it back as a normal text stream.
    sys.stdin = io.TextIOWrapper(io.BytesIO(raw.read()), encoding="utf-8",
                                 errors="replace")
    return tree


def main():
    global TREE

    entry = (os.environ.get("PANEL_TOOLBOX_ENTRY") or "").strip().replace("\\", "/")
    if not entry:
        raise SystemExit("toolbox: PANEL_TOOLBOX_ENTRY is not set")

    TREE = _mount()
    tool = entry.split("/")[0]

    # The tools themselves read/write small caches beside their script
    # ("_live_proxies.txt", bug reports).  Give those a real, empty home - no
    # source file ever lands here, but os.makedirs/open() keep working.
    for folder in (TREE.root, os.path.join(TREE.root, tool)):
        try:
            os.makedirs(folder, exist_ok=True)
        except Exception:                               # noqa: BLE001
            pass

    # Shim AFTER mounting, so the probes the tools run on import see the tree.
    os.stat = _stat
    os.listdir = _listdir
    builtins.open = _open
    io.open = _open

    sys.meta_path.append(_Finder(TREE, tool))
    for base in (os.path.join(TREE.root, tool), os.path.join(TREE.root, "_shared")):
        if base not in sys.path:
            sys.path.insert(0, base)

    if os.environ.get("PANEL_TOOLBOX_DEBUG") == "1":
        sys.stderr.write("toolbox: root=%s tool=%s entry=%s files=%d\n"
                         % (TREE.root, tool, entry, len(TREE.files)))

    # ``python -c "code" a b`` -> sys.argv == ['-c', 'a', 'b']
    sys.argv = [entry] + list(sys.argv[1:])

    path = os.path.join(TREE.root, *entry.split("/"))
    if _key(path) not in TREE.files:
        raise SystemExit("toolbox: %s is not in the archive" % entry)

    module = type(sys)("__main__")
    module.__file__ = path
    module.__package__ = None
    module.__spec__ = None
    sys.modules["__main__"] = module
    exec(compile(TREE.files[_key(path)], path, "exec"), module.__dict__)


main()
