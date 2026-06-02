#!/usr/bin/env python3
import argparse
from pathlib import Path


PACKAGE_ROOTS = ("Editor", "Runtime", "creator assets", "debug")
ROOT_FILES = ("LICENSE.md", "README.md", "package.json")


def to_posix(path):
    return path.as_posix()


def collect_package_files(root):
    files = set()

    for root_file in ROOT_FILES:
        path = root / root_file
        if path.is_file() and not path.is_symlink():
            files.add(to_posix(path.relative_to(root)))

        meta_path = root / (root_file + ".meta")
        if meta_path.is_file() and not meta_path.is_symlink():
            files.add(to_posix(meta_path.relative_to(root)))

    for package_root in PACKAGE_ROOTS:
        package_root_path = root / package_root
        if package_root_path.is_dir() and not package_root_path.is_symlink():
            for path in package_root_path.rglob("*"):
                if path.is_file() and not path.is_symlink():
                    files.add(to_posix(path.relative_to(root)))

        root_meta = root / (package_root + ".meta")
        if root_meta.is_file() and not root_meta.is_symlink():
            files.add(to_posix(root_meta.relative_to(root)))

    return sorted(files, key=str.lower)


def main():
    parser = argparse.ArgumentParser(description="Build the explicit MCB package release file list.")
    parser.add_argument("--root", default=".", help="Package root to scan.")
    parser.add_argument("--output", default="packageFiles", help="Output file path.")
    parser.add_argument("--meta-output", help="Optional output path for the .meta file list used by create-unitypackage.")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    output = Path(args.output)
    if not output.is_absolute():
        output = root / output

    files = collect_package_files(root)
    if not files:
        raise SystemExit("Package allowlist produced no files.")

    output.write_text("\n".join(files) + "\n", encoding="utf-8")
    print(f"Wrote {len(files)} package file entries to {output}")

    if args.meta_output:
        meta_output = Path(args.meta_output)
        if not meta_output.is_absolute():
            meta_output = root / meta_output

        meta_files = [path for path in files if path.endswith(".meta")]
        if not meta_files:
            raise SystemExit("Package allowlist produced no .meta files.")

        meta_output.write_text("\n".join(meta_files) + "\n", encoding="utf-8")
        print(f"Wrote {len(meta_files)} package .meta file entries to {meta_output}")


if __name__ == "__main__":
    main()
