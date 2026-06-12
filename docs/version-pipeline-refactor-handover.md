# Version Pipeline Refactor — Implementation Handover

Status: core refactor implemented, NOT compiled or runtime-tested (no Unity available in
the implementation environment). This document lists what changed, deliberate deviations
from `version-pipeline-refactor.md`, and exactly what a follow-up agent must verify/fix.

## What was implemented

New files (`Editor/Versions/`):
- `VersionArtifact.cs` — `VersionManifest` (schema 1: builderVersion, createdUtc, asset
  identity, `formSignature`, `unsubmitted` flag, `outputs[]` with SHA-256 + bytes,
  `inputs[]`), `ArtifactState`, `ArtifactValidationResult`, `VersionArtifact` handle.
  Note: hashes are SHA-256 (repo convention via `MCBUtils.CalculateFileHash`), not the
  SHA-1 the plan doc said.
- `VersionRepository.cs` — scan/classify version folders (per-folder `version.json` +
  manifest replaces the central `unsubmittedVersions.json`; `manifest.unsubmitted`
  carries the state because `CustomBaseVersion.isUnsubmitted` is `[JsonIgnore]`);
  one-time legacy index migration (then the legacy file is deleted); GC of stale
  `.building-*`/`.trash-*` folders; `CreateStagingFolder`/`CommitStaging` (manifest
  written last; trash-swap that never destroys the old artifact before the new one is
  committed; folder `.meta` of the final name survives the swap so the folder GUID is
  stable); `Validate` (outputs hard gate → inputs drift); `Delete` (in-progress
  guarded); `MarkPublished` (manifest rewrite; folder stays as imported cache);
  `FlushRemovableData` (absorbed DiskSpaceService, now also skips in-progress folders);
  static in-progress registry (`BeginOperation*`).
- `OperationErrors.cs` — `OperationErrorCategory`, `OperationErrorClassifier`
  (disk-full detection absorbed from DiskSpaceService + 401/409/413/507/index.lock/
  cancelled/network message classification), `OperationErrorReporter` (console +
  warnings box + disk-full flush offer), `DiskUtils`.
- `VersionBuilder.cs` — build orchestration: inputs hashed pre-packaging → staging →
  `FileManagerService.PopulateVersionFolder` → metadata factory (runs post-packaging
  because entry hashes are filled during packaging) → manifest → `CommitStaging`.
  Failure deletes staging, never touches the previous artifact.
- `VersionPublisher.cs` — the single upload path (form Publish button AND version-list
  Upload button): validate manifest (OutputsMissing/Modified/NoManifest → hard block
  with explicit "rebuild first" dialog; SourceDrift → "Publish as built / Cancel"
  dialog) → zip exactly the manifest outputs (`CreateZipFromManifestOutputs`) → 600 MB
  cap → streaming upload with cancelable progress bar → `MarkPublished` only on 2xx →
  UnitGit checkpoint + refetch + success dialog (failures there never fail the publish).

Modified:
- `NetworkService.cs` — `SubmitNewVersionAsync` (byte[] + WWWForm + fixed 300 s timeout)
  deleted; replaced by `SubmitNewVersionStreamingAsync`: multipart body pre-written to a
  temp file (same field names `metadata`/`packageFile` → wire-compatible), streamed via
  `UploadHandlerFile`, progress callback, CancellationToken → `Abort()`, stall-based
  timeout (120 s without uploaded-bytes movement) instead of a total-duration timeout.
- `FileManagerService.cs` — `CreateVersionPackageForUpload` became
  `PopulateVersionFolder(folder, ...)`: no path math, no delete-of-existing-folder, no
  zip. New `CreateZipFromManifestOutputs(artifact)`.
- `CreatorModeModule.cs` — `BuildNewVersion` returns a committed `VersionArtifact` via
  `VersionBuilder`; new `ComputeFormSignature()` covers ALL output-affecting fields
  (entries, veins, normals, advanced-mesh/gzip, suggest-realistic, blendshapes JSON —
  old signature was metadata-only); pending build persisted via manifest formSignature +
  SessionState and recovered after domain reload (`TryRecoverPendingBuildAfterReload`);
  publish no longer silently auto-rebuilds; central-json `SaveUnsubmittedVersion`
  deleted; `RemoveUnsubmittedVersion` reduced to pending-state/cache refresh;
  `UploadUnsubmittedVersionCoroutine` is now a thin wrapper over VersionPublisher;
  `SaveTemporaryInternalVersion` goes through the same builder.
- `MCBEditor.cs` — `LoadUnsubmittedVersions`/`LoadImportedVersions` delegate to
  `VersionRepository.Scan`; dead `OnInspectorGUI` override removed (Unity never called
  it: `CreateInspectorGUI` always returns the UIToolkit root).
- `VersionActions.cs` — delete goes through `VersionRepository.Delete` (in-progress
  guarded). Everything else (apply/reset/download internals) untouched.
- `UnitGitReleasePublisher.cs`, `AdvancedModeModule.cs` — DiskSpaceService calls →
  classifier/reporter/DiskUtils/`VersionRepository.FlushRemovableData`.

Deleted: `Services/DiskSpaceService.cs` (+ .meta).

## Deliberate deviations from the plan doc (documented, low risk)

1. `MCBUtils.GetVersionDataPath` stays public. It already is the single path
   constructor; making it repository-private would have forced touching every read-only
   consumer (CustomVeinsDrawer, AnimationPositionOffsetService, VersionListDrawer,
   MCBUtils' own GetVersion*Path helpers) for zero behavior change. Repository owns all
   writes/moves/deletes; readers keep using the helpers.
2. `VersionActions` was NOT renamed to `ApplyService` and was not repathed through
   `VersionArtifact.ResolveFile` (pure mechanical churn, 2.8k lines). Candidate cleanup.
3. No `OperationRunner` class / no `IPostPublishHook` interface: existing busy flags
   (`isSubmitting` etc.) already serialize operations; post-publish steps are two direct
   calls in VersionPublisher. Revisit only if more operations/hooks appear.
4. `VersionFormState` was not extracted as a class; the signature/snapshot
   (`ComputeFormSignature`) + SessionState recovery deliver the same guarantees without
   rebinding 1.8k lines of UIToolkit form code.
5. The dead IMGUI creator form body (`CreatorModeModule.Draw()` + its Draw* helpers) and
   the now-unreachable IMGUI `VersionListDrawer.cs` draw path were left in place (dead
   code — the only caller, `OnInspectorGUI`, is gone). Safe deletion pass = follow-up.
6. No EditMode test suite was authored (cannot be executed from this environment;
   writing untestable tests felt worse than none). See test list below.

## MUST DO before merging (for the follow-up agent)

1. **Compile.** Open the project in Unity (or `dotnet build mcb.Editor.csproj
   --no-restore` from the Unity project root). Expect possible small fixes:
   - C# 9 only (`/langversion:9.0`) — check no accidental newer syntax.
   - `UploadHandlerFile` exists since 2017.1 — fine; verify
     `UnityWebRequest.kHttpVerbPOST` + manual multipart Content-Type header is accepted
     by the backend (wire format unchanged, but transport changed: TEST a real upload).
   - Unused usings/consts left intentionally (e.g. `MaxVersionPackageUploadBytes` in
     CreatorModeModule, `StringEnumConverter` import) — harmless, clean up if desired.
2. **Run the editor health checks** (`Tools > My Custom Base (MCB) > Health Checks >
   All Deterministic` / `MCBEditorHealthChecks.RunAllOrThrow`). They were NOT run.
   Apply/reset internals were not modified, but delete/build paths were.
3. **Run `graphify update .`** from `Packages/orbiters.mcb` (CLI unavailable here).

## Verify / test (priority order)

1. Build → Publish happy path (simple FBX patch): folder committed with version.json +
   manifest; Publish uploads; folder remains and now lists as Imported; UnitGit
   checkpoint dialog appears.
2. Migration: project with old `Assets/MCB/unsubmittedVersions.json` → entries appear as
   unsubmitted after first scan, legacy file deleted, entries with missing folders
   dropped with console note.
3. Upload-from-list of a migrated unsubmitted version (manifest generated at migration).
4. Tamper tests: delete a `.bin` from the version folder between Build and Publish →
   publish must hard-block with the rebuild dialog (NOT silently rebuild). Edit the
   source FBX between Build and Publish → drift warning with "Publish as built".
5. Domain reload between Build and Publish → Publish button recovers after the
   unsubmitted version is selected (form repopulates; verify `ComputeFormSignature`
   equals the manifest signature after `PopulateFieldsFromVersion` — if it doesn't for
   some field, signature canonicalization in `ComputeFormSignature` needs adjusting).
6. Large upload (≥500 MB): progress bar advances, Cancel works (artifact stays
   unsubmitted), memory stays flat, slow-link upload survives >5 min.
7. Crash-sim mid-build (kill editor) → on restart, `.building-*` folder GC'd, previous
   artifact intact.
8. Flush removable data (Advanced window): keeps applied + unsubmitted + in-progress.
9. Delete unsubmitted version; delete while a publish is running (must be refused).
10. Blender-sync temporary internal version auto-save still works
    (`SaveTemporaryInternalVersion` now commits through the builder).
11. Offline export of an unsubmitted version: exported package now carries version.json
    + manifest.json; importing it into another project lists it as *unsubmitted* there
    (arguably correct; today it appeared as imported — confirm this is wanted, otherwise
    strip `manifest.json` in `ExportOfflineVersionPackage`).
12. Check `ComputeFormSignature` cost in UI refresh paths (it JSON-serializes the
    blendshape list; called from validation refreshes, not per-frame — should be fine).

EditMode tests worth adding later (none exist in the package): manifest round-trip +
`CreateManifestFromFolder` exclusion of version.json/manifest.json(.meta);
`Validate` for each ArtifactState; staging commit/swap crash points (temp dirs);
migration; classifier table; multipart body writer byte-level format;
zip-from-outputs entry-set equality vs `ZipFile.CreateFromDirectory` minus local-only
files.

## Known small behavior changes (intended)

- Publish/upload never repackages from live state (the core bug fix). Broken artifacts
  require an explicit rebuild.
- Editing ANY output-affecting form field (not just title/version) now invalidates the
  pending build (Publish hides, Build re-greens).
- Published artifact folders remain locally (flushable, listed as Imported); previously
  the folder also remained but the unsubmitted entry was just removed from the index.
- Cancelled upload leaves the version as unsubmitted; a retry that hits 409 (server
  actually finished) shows the version-conflict message with a "bump the version" hint.
- `version.json` + `manifest.json` now live inside unsubmitted version folders (and in
  offline exports). They are never included in the upload zip.
