# My Custom Base (MCB) by Enzo

## UI Toolkit migration

MCB editor UI is transitioning to Unity UI Toolkit with the shared styled surfaces used by the gallery and account modules. New UI elements and changes to existing UI should be built with UI Toolkit and the package USS style sheets instead of adding new IMGUI blocks.

## About and third-party notices

**About** in the bottom toolbar (next to Advanced Options and Blendshape Links) shows MCB's version and license and the
third-party software it ships with (HDiffPatch with libdivsufsort, CocoTools, YUCP Dev Tools, Zstandard, zlib, bzip2,
the LZMA SDK and LZ4), each with its license. The texts live in `Editor/Plugins/Hdiff/THIRD_PARTY_NOTICES.md`.

## See a version's differences

The expanded card of a version that ships meshes shows **Mesh updated · See differences**. It opens a window with the
avatar turning in 3D, before and after the version, so creators see what it does to their avatar before applying it:

- **Slider** wipes between before and after with a handle, **Side by side** turns both together, **Overlay** shows the
  version with its former shape as an x-ray ghost. Holding Space shows the before side.
- **Changes** makes the surface that moves glow, from amber (a little) to magenta (the most); **Clay** shows the shape
  alone; **Textured** uses the avatar's own materials. The clothes button adds the avatar's other renderers.
- It compares with **your avatar now**, or with the **original** model while a version is applied. Parts are shown with
  the renderer's blendshape values, and the version's new blendshapes at the value the creator chose.
- The list of meshes that change flies the camera to each change; blendshapes added, reshaped or removed are listed.
- **Apply** goes through the usual confirmation. A version that is not downloaded is downloaded first.

Reading changes nothing in the project: FBX replacements are decoded in memory (HDiff through a temporary file in
`Library/MCB/HdiffTemp`), advanced meshes are decrypted and parsed in memory (or read from the cache an earlier apply
left), and the models are read with Toolkit's `FbxReader`. `VersionActions.ResolveModelPatches` locates a downloaded
version's patches without creating backups; `VersionMeshComparison` pairs them with the avatar's renderers and measures
the change with Toolkit's `MeshComparison`.

## Small accessories on the body (beta)

Advanced Options › Blendshape processing › **Keep Small Accessories On The Body (beta)** adds Toolkit's Follow Body
Blendshapes to the avatar root: piercings, studs and other small rigid accessories on the skin move and tilt with the
body's blendshapes (muscles, versions) at upload and in Play Mode.

## Custom base FBX backup invariant

MCB custom base versions are applied over the original/default base FBX. If the default base is `A` and custom bases are `B` or `C`, then `*.fbx.old` is always the preserved copy of `A`.

- Applying a custom FBX or version creates `*.fbx.old` only when it is missing.
- Existing `*.fbx.old` files must not be overwritten, deleted, or moved during apply/reset flows.
- Resetting to Base Default copies `*.fbx.old` back over `*.fbx` while keeping `*.fbx.old` in place.
- The only valid state without `*.fbx.old` is the untouched default-base state where `*.fbx` is already `A`.
- XOR `.bin` patches are computed against `A`, so version switching depends on `*.fbx.old` remaining the original default source.

## Editor health checks

MCB includes deterministic editor health checks for risky apply/reset behavior. They are menu-driven checks, not Unity Test Runner edit-mode tests, so they are meant to stay fast enough to run during local development.

Run all deterministic checks from Unity:

1. Open `Tools > My Custom Base (MCB) > Health Checks`.
2. Click `All Deterministic`.
3. Check the Console if the dialog reports a failure.

Individual checks are also available in the same menu:

- `Native Mesh Payload`
- `Version Apply Reset Invariants`

For batch-mode validation, run Unity with `MCBEditorHealthChecks.RunAllOrThrow`:

```powershell
& "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe" `
  -batchmode `
  -quit `
  -projectPath "H:\metaverse\unity projects\MCB Test" `
  -executeMethod MCBEditorHealthChecks.RunAllOrThrow `
  -logFile "Logs\mcb-health-checks.log"
```

Run these checks when touching version apply/reset code, FBX backup handling, native mesh payloads, advanced mesh paths, dynamic normals, material mutations, slider creation, blendshape preservation, or applied-version caches.

## Testing SSL failures on Windows

The easiest way to test MCB's connectivity failure UI is now built into the package.

1. Open an avatar with the `My Custom Base` component.
2. Enable `Advanced Mode`.
3. In `Connectivity Simulation`, set `API simulation` to:
   - `SSL Failure` to force requests to `https://wrong.host.badssl.com`
   - `Transport Failure` to force requests to `https://127.0.0.1:1`
4. Click `Retry connectivity check` or reopen the inspector.
5. Optionally click `Open connectivity tests` to run the standalone diagnostics window against the simulated target.

`SSL Failure` is the fast path if you specifically want certificate validation errors. `Transport Failure` is useful when you only want to verify the "cannot connect" report UI.

Turn `API simulation` back to `Off` when you want the package to use the real `api.orbiters.cc` / `dev.api.orbiters.cc` endpoints again.

## Manual fallback

If you still want to force a real local hostname mismatch on Windows outside the built-in simulation, the reliable manual approach is:

1. Map `api.orbiters.cc` or `dev.api.orbiters.cc` to `127.0.0.1` in `hosts`.
2. Run a local HTTPS listener on port `443`.
3. Present a certificate for a different hostname, such as `localhost`.

That will produce a genuine TLS hostname/certificate error for the real MCB host names.
