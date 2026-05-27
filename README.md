# My Custom Base (MCB) by Enzo

## UI Toolkit migration

MCB editor UI is transitioning to Unity UI Toolkit with the shared styled surfaces used by the gallery and account modules. New UI elements and changes to existing UI should be built with UI Toolkit and the package USS style sheets instead of adding new IMGUI blocks.

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
