# Native mesh pipeline experiments

Python is not an MCB user dependency. `benchmark_payload_codecs.py` is an optional
developer comparison and is excluded from release archives. Scripts under `ci/`
prepare or verify packages on developer/CI machines. Unity calibration, creator
builds, downloads and Apply call the bundled native codec libraries through C#;
the release validator requires those binaries. Blender sync uses Blender's own
embedded Python interpreter and does not require a system Python installation.

For the current implementation, call
`NativeMeshPipelineBenchmark.StartAdaptiveDelivery(assetPath, binPath, originalKeyPath, 3)`
with an existing uncompressed native payload fixture. It compares the bundled
production codecs and word XOR, then exercises the synthetic 50/150 MB CPU
calibration. It performs no network calibration. The sanitized 28-observation
result is `results/2026-09-07-adaptive.json`.

The original experiments below describe the source baseline at
`b4224c9d87fa5d4d043dd65d81d904b955c0752d`. The production cache now prefers binary
serialization, so the original `Start` entrypoint no longer represents a text
versus binary comparison on the current source. Use that baseline checkout to
reproduce the historical comparison; do not label a current binary cache as text.

These are explicitly invoked developer benchmarks. They do not change the
production serializer, wire format, cache identity, Apply flow, or UI animations.
They do not upload/download content or apply a version to the current scene.

Run the Unity suite in edit mode with a real, uncompressed (`NONE`) advanced mesh
payload, its generated `.asset`, and the exact original FBX key. Supply file paths
to `NativeMeshPipelineBenchmark.Start(assetPath, binPath, originalKeyPath, 3)`.
Read `Status` and `ResultPath` to follow completion. Do not modify scripts or reload
assemblies while a suite is running. Some baseline operations block the main thread
for seconds; run when the Editor is available for benchmarking.

The suite uses the actual production mesh serializer, parser, mesh creation code,
hash streams and XOR writer through reflection. The buffered variant changes only
stream buffering. An explicitly attributed benchmark container tests binary cache
serialization without changing `NativeMeshPayloadAsset` in that baseline.

Results are checkpointed under `Library/MCB/Benchmarks/<run>/results.json`. Owned
scratch assets are created under `Assets/MCB/generated/pipelineBenchmarks/<run>`
and deleted on normal completion. Source assets are never deleted or unloaded.
Output bytes from packaging variants and decoded XOR variants must match; saved
caches must match the MCB mesh serialization fingerprint after unload/reload.

Iteration zero is warmup and must be excluded from reported medians. This is one
fixture on one computer, with the normal Windows file cache enabled. Cache reload
means Unity objects are unloaded first; it is not a cold-disk test. Already-loaded
lookups are measured separately. Do not sum unrelated stage timings and present
them as an end-to-end Apply measurement. The suite does not include avatar logic,
sliders, materials, scene transitions, real network time, or the completion animation.

After the Unity suite completes, run `benchmark_payload_codecs.py` on its owned
`payload-for-codec-benchmark.bin`, using the same key and an output JSON path. It
requires NumPy and zstandard; supply `--lz4-library` to include a native LZ4 library.
Do not run codec benchmarks concurrently with Unity benchmarks. It compares
compression before XOR and stored/deflated outer ZIP payloads, verifying every
roundtrip. Python codec timings are not Unity codec integration timings.

`NativeMeshPipelineBenchmark.StartNativeSupplement(assetPath, binPath,
originalKeyPath, 3)` runs the Windows-only follow-up. It compares the current
managed SHA-256 provider with Windows CNG, tests buffered packaging with identical
hashes, and measures geometry creation separately from blendshape submission.
It also writes an owned binary-cache sample for a separate transport-size
comparison. This sample is not a validated distribution format or an AssetBundle.

`NativeMeshPipelineBenchmark.StartTransportSupplement(assetPath, binPath,
originalKeyPath, binarySamplePath, lz4DllPath, zstdDllPath, 3)` compares native LZ4,
Zstd 3 and Zstd 9 inside Unity using identical pinned-array bindings. Supply trusted
Windows x64 libraries that export the standard LZ4/Zstd C APIs. The suite loads them
only for the experiment; it does not install plugins or change production code.
It then copies the binary sample to fresh asset paths and measures synchronous
import and load. This runs in the same Unity project with warm filesystem and
import caches; it does not prove cross-project or cross-version portability.

The aggregate evidence from 2026-09-07 is in
[`results/2026-09-07.json`](results/2026-09-07.json). It preserves measured samples
without source mesh contents or private fixture paths. The detailed interpretation
is in the Documentation repository's `docs/reference/mcb-version-pipeline-benchmarks.md`.
The required deterministic suite passed HDiff and native-mesh checks, then stopped
at the existing apply/reset path-resolution fixture: its three fictional source
paths are never created, while the current resolver requires files to exist.
No production or health-check code was changed to conceal that failure.
The native-mesh health check returned successfully but logged "Bones do not match
bindpose" while restoring its fixture's renderer bounds; the suite is not clean.

The first recorded run exposed unavailable Mono allocation and process peak
counters (returned zero). Ignore those fields in that run; subsequent harness
versions represent unavailable readings as null. Derived dense-array sizes are
explicitly labeled and are not measured process-memory peaks.

Keep benchmark reports and aggregate sizes/timings, but do not publish the mesh
payloads or scratch binary assets. Those contain the original creator content.
