"""Fetch pinned native codec libraries for the Unity Editor. No system installation."""
import hashlib
import io
import json
from pathlib import Path
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1] / "Editor/Plugins/Compression"
PACKAGES = [
    ("lz4.nativebinaries", "1.9.21", "lz4", "381d64320e87dc899b824dce81fba17d52e97387d3cb1b40e7fb8a2f0c3132c6"),
    ("zstdnet", "1.5.7", "zstd", "ab7bfbebd1bd50081bc8be6a881ee2a3fd949f035e98845a2f00c4c7d1be5442"),
]
TARGETS = {"win-x64": ("Windows", "x86_64"), "linux-x64": ("Linux", "x86_64"),
           "osx-x64": ("OSX", "x86_64"), "osx-arm64": ("OSX", "ARM64")}


def main():
    ROOT.mkdir(parents=True, exist_ok=True)
    evidence = []
    for name, version, codec, expected_hash in PACKAGES:
        url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg"
        data = urllib.request.urlopen(url, timeout=60).read()
        if hashlib.sha256(data).hexdigest() != expected_hash:
            raise RuntimeError(f"Package digest mismatch: {name} {version}")
        archive = zipfile.ZipFile(io.BytesIO(data))
        evidence.append({"package": name, "version": version, "url": url,
                         "sha256": hashlib.sha256(data).hexdigest(), "files": []})
        for rid, (os_name, cpu) in TARGETS.items():
            prefix = f"runtimes/{rid}/native/"
            matches = [p for p in archive.namelist() if p.startswith(prefix)
                       and p.rsplit('/', 1)[-1] in [f"{codec}.dll", f"lib{codec}.dll", f"lib{codec}.so", f"lib{codec}.dylib"]]
            for member in matches:
                suffix = Path(member).suffix
                filename = ("" if suffix == ".dll" else "lib") + "mcb_" + codec + suffix
                dest = ROOT / rid / filename
                dest.parent.mkdir(exist_ok=True)
                binary = archive.read(member)
                if not dest.exists() or dest.read_bytes() != binary:
                    dest.write_bytes(binary)
                guid = uuid.uuid5(uuid.NAMESPACE_URL, "orbiters.mcb/compression/" + rid + "/" + codec).hex
                dest.with_name(dest.name + ".meta").write_text(
                    f"fileFormatVersion: 2\nguid: {guid}\nPluginImporter:\n  externalObjects: {{}}\n"
                    "  serializedVersion: 2\n  iconMap: {}\n  executionOrder: {}\n  defineConstraints: []\n"
                    "  isPreloaded: 0\n  isOverridable: 0\n  isExplicitlyReferenced: 0\n  validateReferences: 1\n"
                    "  platformData:\n  - first:\n      Any: \n    second:\n      enabled: 0\n      settings: {}\n"
                    f"  - first:\n      Editor: Editor\n    second:\n      enabled: 1\n      settings:\n        CPU: {cpu}\n        OS: {os_name}\n"
                    "  userData: \n  assetBundleName: \n  assetBundleVariant: \n", encoding="utf-8")
                evidence[-1]["files"].append({"path": dest.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(binary).hexdigest()})
    for name, url in {
        "LZ4-LICENSE.txt": "https://raw.githubusercontent.com/lz4/lz4/v1.9.2/lib/LICENSE",
        "ZSTD-LICENSE.txt": "https://raw.githubusercontent.com/facebook/zstd/v1.5.7/LICENSE",
    }.items():
        (ROOT / name).write_bytes(urllib.request.urlopen(url, timeout=30).read())
    (ROOT / "provenance.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"Fetched {sum(len(p['files']) for p in evidence)} native libraries into {ROOT}")


if __name__ == "__main__":
    main()
