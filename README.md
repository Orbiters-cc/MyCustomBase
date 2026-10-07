# My Custom Base (MCB) by Enzo

## 1.12.2 — 2026-10-07

- Fix "Unsupported conversion of vertex data (format 0 to 4, dimensions 4 to 4)" in builds of versions with twisting bones (Ultirex): Unity logged it when a twist gave a vertex more than four bone influences. Both twists were applied already; the build now writes the weights without the error.
- XMuscles: undoing **Build rig on this avatar** removes its contacts and constraints cleanly; Unity no longer warns that a component "had become dangling during an undo operation".
- Faster version list: the applied version is worked out once when the avatar's meshes or versions change, not for every row on every refresh, and a refresh adds an undo step or an unsaved change only when MCB's state actually changed. Hidden MCB inspectors no longer ask the server for the account state.
- Quieter Console: the apply, native mesh and dynamic normals timings now follow Advanced Options › Log in Console (off by default), and with it on, cache hits, account refreshes and version-state checks are no longer logged on every refresh. Warnings and errors are unchanged.
- Fix the wording of the message shown when you do not own the MCB.

## 1.12.1 — 2026-10-06

- Unencrypted versions keep the original base's pose: switching a T-posed Rexouium to Ultirex 5.1 no longer leaves it in the star pose of the Ultirex FBX. The arms, hands, fingers, legs and feet point like the original base model; bone lengths, hips, spine and head stay the version's.
- XMuscles: **Build rig on this avatar** places each contact where Blender measured it (XMuscle Orbit Helper API 2): a receiver and sender along the muscle's stretch, and for twisting muscles a receiver on a pivot an Aim constraint keeps along the twisting bone, so it reads the twist alone (2D blend tree). Exports from another helper API are not stored.
- Requires Orbiters Toolkit 0.3.14.

## 1.12.0 — 2026-10-05

- Physic: creators name a version's secondary-motion bones with "physic" and turn on Support physic in the version form. Users switch Physic on in the version options (off by default). On, the build gives the chains always-on PhysBones, one per parent bone, that every player simulates; off, the build removes those bones and moves their weights to the parent.
- Squishy interaction, the same way: bones named with "interaction" (a chain of two) and Support squishy in the version form; on, players' hands squash them and they spring back (PhysBone squish with collision), off, the build removes them.
- The parameter graph of the Sliders card is gone: XRay Gizmos 0.2.8 shows the avatar budget over the Scene view, with the custom base's share including the PhysBones its build adds and the bones it removes.
- Modes are folded by default in the version form.
- The avatar's own animations keep working on bones a version moves to another parent: the build copy's controllers follow the moved bones.
- Authoring drafts can set the realistic material suggestion.
- Fix hidden MCB editors left by interrupted operations raising errors after every script reload.
- Fix versions with twisting bones uploading without any mesh (Ultirex 5.0.1 was invisible to everyone): the build copied every mesh that listed the twisted bone, in memory, after VRCFury had saved its files, and the SDK's save of the avatar dropped them. Only meshes weighted to the bone are copied now, once for all its twists, and the build saves them (Orbiters Toolkit 0.3.13).
- Authoring drafts build with their own source and custom models, also on an avatar that has a version applied.
- Uploads leave out the custom base's unused blendshapes: the ones no animation, viseme, eyelid or MMD dance uses are removed from its meshes, and one set to a weight is baked in. Ultirex went from 697 MB (over VRChat's 500 MB limit) to 266 MB uncompressed, 106 MB to download. Play mode keeps every shape.
- About credits Prefabulous Universal (MIT), which the twisting bones are adapted from.
- Requires Orbiters Toolkit 0.3.13.

## 1.11.1 — 2026-10-05

- The version timeline, gallery cards, safe archive extraction, package hashing, content trust prompts and uploads now come from Orbiters Toolkit 0.3.12, shared with My Avatar's asset gallery. No change in behaviour intended.

## 1.11.0 — 2026-10-05

- Protection belongs to the custom base asset: trusted creators can verify a Discord role and publish one unencrypted package for every original base, with Discord role access managed from Create custom base and the asset's Edit panel. Requires the matching Orbiters backend.
- Modes replace genres: pick-one and combinable categories with blendshape, object and animation rules, a new Modes card for users, and choices remembered per asset (cleared when returning to the original base).
- Material slots follow their names across original layouts, with hidden original pieces, fallback materials and a folded summary in the creator form.
- Fix stretched necks and heads after a version moved bones: Avatar assignment and rebinds keep the version's skeleton pose.
- Reset and version switches restore the original FBX hierarchy and pose, ignore same-named logic objects, and keep the avatar's own original model.
- Custom veins apply to and are removed from every material instantly; custom base blendshape values are remembered across a reset.
- Differences viewer: realistic distances when a version splits meshes, original materials in their slot order without version veins.
- ReFit no longer lists meshes the applied version adds; Blendshapes appear above Sliders; the base row names the original base.

## 1.10.4 — 2026-10-03

- Generate and reuse the native custom base humanoid definition for preview, Play Mode and avatar upload, preventing repeated armature corrections.
- Preserve clothing placement in mesh comparison and reuse prepared original-body data for ReFit.
- Fix player compilation of the embedded Blender addon and keep the MCB editor assembly out of player builds.

## UI Toolkit migration

MCB editor UI is transitioning to Unity UI Toolkit with the shared styled surfaces used by the gallery and account modules. New UI elements and changes to existing UI should be built with UI Toolkit and the package USS style sheets instead of adding new IMGUI blocks.

## About and third-party notices

**About** in the bottom toolbar (next to Advanced Options and Blendshape Links) shows MCB's version and license and the
third-party software it ships with (HDiffPatch with libdivsufsort, CocoTools, YUCP Dev Tools, Prefabulous Universal,
Zstandard, zlib, bzip2, the LZMA SDK and LZ4), each with its license. The texts live in `Editor/Plugins/Hdiff/THIRD_PARTY_NOTICES.md`.

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

Advanced Options › Blendshape processing › **Keep Small Accessories On The Body** (beta) adds Toolkit's Follow Body
Blendshapes to the avatar root: piercings, studs and other small rigid accessories on the skin move and tilt with the
body's blendshapes (muscles, versions) at upload and in Play Mode.

## Banners

The Orbiters server applies the banner effect (blur and fade toward `#303030` over the lower third, the same for web and
Unity uploads). The Photoshoot therefore uploads the banner as rendered and shows it with the effect applied locally
(Toolkit's `PhotoshootService.ApplyBannerEffect`), then the server's image once the upload returns. This needs a server
with the banner pipeline (asset field `mcbBannerEffectVersion`).

## XMuscles (experimental)

Correctives baked with XMuscles in Blender (XMuscle Orbit Helper) come with each Magic Sync export (`manifest.xmuscle`)
and are kept on the custom base per exported mesh (`MuscleCorrectiveStore`). The Creator panel shows them with their
contact cost; **Build rig on this avatar** (`MuscleDriverGenerator`) turns each sensor the bake measured (XMuscle Orbit
Helper API 2) into a proximity receiver and a point sender where Blender measured them: the muscle's stretch, from its
origin to its insertion, and for a muscle whose drivers read a twist, a receiver on a pivot that a VRC Aim constraint
keeps along the twisting bone (up held by its parent), so it reads the twist alone. A 1D blend tree (2D Freeform
Cartesian with a twist) puts each corrective fully on at the readings its baked distances give, inside one Direct blend
tree, with a VRCFury Full Controller. Contacts run on every client and scale with the avatar; a VRC Raycast would only
hit the wearer's own colliders on the wearer's client (and colliders are PC only), so others would not see the muscles.
Exports from another XMuscle Orbit Helper API are not stored. Publishing the rig with a version waits for the prototype
check in Gesture Manager and VRChat.

## Custom base FBX backup invariant

MCB custom base versions are applied over the original/default base FBX. If the default base is `A` and custom bases are `B` or `C`, then `*.fbx.originalbase` (next to the FBX) is always the preserved copy of `A`.

- Applying a custom FBX or version creates `*.fbx.originalbase` only when it is missing.
- Existing `*.fbx.originalbase` files must not be overwritten, deleted, or moved during apply/reset flows.
- Resetting to Base Default copies `*.fbx.originalbase` back over `*.fbx` while keeping `*.fbx.originalbase` in place.
- The only valid state without `*.fbx.originalbase` is the untouched default-base state where `*.fbx` is already `A`.
- XOR `.bin` patches are computed against `A`, so version switching depends on `*.fbx.originalbase` remaining the original default source.

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
