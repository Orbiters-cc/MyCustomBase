#!/usr/bin/env python3
import json
import sys
import zipfile
from pathlib import PurePosixPath


EXPECTED_PACKAGE_NAME = "orbiters.mcb"
PACKAGE_ROOTS = ("Editor", "Runtime", "Tests", "creator assets", "debug")
REQUIRED_PACKAGE_ROOTS = ("Editor", "Runtime", "Tests")
ROOT_FILES = ("LICENSE.md", "README.md", "package.json")
FORBIDDEN_ROOTS = (".git", ".github", ".idea", ".agents", ".codex")

REQUIRED_ASMDEF_FILES = (
    "Runtime/mcb.asmdef",
    "Editor/mcb.Editor.asmdef",
    "Tests/Editor/mcb.Editor.Tests.asmdef",
)
REQUIRED_USS_FILES = (
    "Editor/Styles/mcb-theme.uss",
)
REQUIRED_HDIFF_FILES = (
    "Editor/Plugins/Hdiff/THIRD_PARTY_NOTICES.md",
    "Editor/Plugins/Hdiff/hdiffinfo.dll",
    "Editor/Plugins/Hdiff/hdiffz.dll",
    "Editor/Plugins/Hdiff/hpatchz.dll",
)
REQUIRED_COMPRESSION_FILES = (
    "Editor/Plugins/Compression/win-x64/mcb_lz4.dll",
    "Editor/Plugins/Compression/win-x64/mcb_zstd.dll",
    "Editor/Plugins/Compression/linux-x64/libmcb_lz4.so",
    "Editor/Plugins/Compression/linux-x64/libmcb_zstd.so",
    "Editor/Plugins/Compression/osx-x64/libmcb_lz4.dylib",
    "Editor/Plugins/Compression/osx-x64/libmcb_zstd.dylib",
    "Editor/Plugins/Compression/osx-arm64/libmcb_zstd.dylib",
    "Editor/Plugins/Compression/provenance.json",
    "Editor/Plugins/Compression/LZ4-LICENSE.txt",
    "Editor/Plugins/Compression/ZSTD-LICENSE.txt",
)
ALLOWED_EXTERNAL_ASMDEF_REFERENCES = {
    "VRC.SDK3A",
    "VRC.SDK3A.Editor",
    "VRC.SDKBase",
    "VRC.SDKBase.Editor",
}


def validate_zip_name(name):
    normalized = name.replace("\\", "/")
    parts = PurePosixPath(normalized).parts
    if normalized != name:
        return "uses backslashes"
    if not parts:
        return "is empty"
    if normalized.startswith("/"):
        return "is absolute"
    if any(part in ("", ".", "..") for part in parts):
        return "contains an invalid path segment"
    return None


def is_allowed_package_file(name):
    if name.endswith((".meta", ".py")):
        return False

    if name in ROOT_FILES:
        return True

    parts = PurePosixPath(name).parts
    if parts and parts[0] in PACKAGE_ROOTS:
        return True

    return False


def read_text(archive, name, failures):
    try:
        return archive.read(name).decode("utf-8")
    except UnicodeDecodeError as ex:
        failures.append(f"{name}: not valid UTF-8 ({ex})")
    except KeyError:
        failures.append(f"{name}: required file missing")
    return None


def read_json(archive, name, failures):
    text = read_text(archive, name, failures)
    if text is None:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError as ex:
        failures.append(f"{name}: invalid JSON ({ex})")
        return None


def require_file(archive, names, name, failures, allow_empty=False):
    if name not in names:
        failures.append(f"{name}: required package file missing")
        return

    if not allow_empty and archive.getinfo(name).file_size == 0:
        failures.append(f"{name}: required package file is empty")


def validate_required_files(archive, names, failures):
    for required_file in ROOT_FILES:
        require_file(archive, names, required_file, failures)

    for required_root in REQUIRED_PACKAGE_ROOTS:
        if not any(name.startswith(required_root + "/") for name in names):
            failures.append(f"{required_root}/: required package root missing")

    for required_file in REQUIRED_ASMDEF_FILES:
        require_file(archive, names, required_file, failures)

    for required_file in REQUIRED_USS_FILES:
        require_file(archive, names, required_file, failures)

    for required_file in REQUIRED_HDIFF_FILES:
        require_file(archive, names, required_file, failures)

    for required_file in REQUIRED_COMPRESSION_FILES:
        require_file(archive, names, required_file, failures)


def validate_package_json(archive, failures):
    package_json = read_json(archive, "package.json", failures)
    if not isinstance(package_json, dict):
        failures.append("package.json: expected JSON object")
        return

    package_name = package_json.get("name")
    if package_name != EXPECTED_PACKAGE_NAME:
        failures.append(f"package.json: expected name '{EXPECTED_PACKAGE_NAME}', got '{package_name}'")

    for field in ("displayName", "version", "unity"):
        value = package_json.get(field)
        if not isinstance(value, str) or not value.strip():
            failures.append(f"package.json: required field '{field}' is missing or empty")


def validate_asmdefs(archive, names, failures):
    asmdef_paths = sorted(name for name in names if name.endswith(".asmdef"))
    if not asmdef_paths:
        failures.append("*.asmdef: no assembly definitions found")
        return

    parsed_asmdefs = []
    asmdef_names = {}
    for path in asmdef_paths:
        asmdef = read_json(archive, path, failures)
        if not isinstance(asmdef, dict):
            failures.append(f"{path}: expected JSON object")
            continue

        name = asmdef.get("name")
        if not isinstance(name, str) or not name.strip():
            failures.append(f"{path}: asmdef name is missing or empty")
        elif name in asmdef_names:
            failures.append(f"{path}: duplicate asmdef name '{name}' also used by {asmdef_names[name]}")
        else:
            asmdef_names[name] = path

        parsed_asmdefs.append((path, asmdef))

    for path, asmdef in parsed_asmdefs:
        references = asmdef.get("references", [])
        if not isinstance(references, list):
            failures.append(f"{path}: references must be a list")
            continue

        for reference in references:
            if not isinstance(reference, str) or not reference.strip():
                failures.append(f"{path}: references contains an empty or non-string value")
                continue

            if reference.startswith("GUID:"):
                failures.append(f"{path}: uses GUID asmdef reference '{reference}' after .meta removal")
                continue

            if reference not in asmdef_names and reference not in ALLOWED_EXTERNAL_ASMDEF_REFERENCES:
                failures.append(f"{path}: reference '{reference}' is neither packaged nor allowlisted")

    test_asmdefs = [path for path, _ in parsed_asmdefs if path.startswith("Tests/Editor/")]
    if not test_asmdefs:
        failures.append("Tests/Editor/: no editor test asmdef found")

    if not any(name.startswith("Tests/Editor/") and name.endswith(".cs") for name in names):
        failures.append("Tests/Editor/: no editor test source files found")

    for path, asmdef in parsed_asmdefs:
        if not path.startswith("Tests/Editor/"):
            continue

        optional_references = asmdef.get("optionalUnityReferences", [])
        if "TestAssemblies" not in optional_references:
            failures.append(f"{path}: editor test asmdef must reference optional Unity TestAssemblies")

        include_platforms = asmdef.get("includePlatforms", [])
        if "Editor" not in include_platforms:
            failures.append(f"{path}: editor test asmdef must include the Editor platform")


def validate_uss_assets(archive, names, failures):
    style_files = sorted(name for name in names if name.startswith("Editor/Styles/") and name.endswith(".uss"))
    if not style_files:
        failures.append("Editor/Styles/: no USS files found")

    for name in style_files:
        if archive.getinfo(name).file_size == 0:
            failures.append(f"{name}: USS file is empty")


def validate_hdiff_assets(archive, names, failures):
    dlls = [name for name in REQUIRED_HDIFF_FILES if name.endswith(".dll")]
    for name in dlls:
        if name in names and archive.getinfo(name).file_size == 0:
            failures.append(f"{name}: HDiff native DLL is empty")


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: validate-package-archive.py <package.zip>")

    zip_path = sys.argv[1]
    with zipfile.ZipFile(zip_path) as archive:
        names = sorted(name for name in archive.namelist() if not name.endswith("/"))
        name_set = set(names)

        if not names:
            raise SystemExit("Package archive is empty.")

        failures = []
        for name in names:
            invalid_reason = validate_zip_name(name)
            if invalid_reason:
                failures.append(f"{name}: {invalid_reason}")
                continue

            root = PurePosixPath(name).parts[0]
            if root in FORBIDDEN_ROOTS:
                failures.append(f"{name}: forbidden repository/internal path")
                continue

            if name.endswith(".meta"):
                failures.append(f"{name}: .meta files must not be included in the package archive")
                continue

            if not is_allowed_package_file(name):
                failures.append(f"{name}: not in package allowlist")

        validate_required_files(archive, name_set, failures)
        validate_package_json(archive, failures)
        validate_asmdefs(archive, name_set, failures)
        validate_uss_assets(archive, name_set, failures)
        validate_hdiff_assets(archive, name_set, failures)

    if failures:
        print("Package archive validation failed:")
        for failure in failures:
            print(" - " + failure)
        raise SystemExit(1)

    print(f"Package archive validation passed for {len(names)} files.")


if __name__ == "__main__":
    main()
