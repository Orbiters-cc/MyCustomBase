# MCB Version Pipeline Refactor — Final Implementation Plan

Status: PLAN — nothing implemented.
Mode: single full refactor on a dedicated branch, merged once, no intermediate half-states on main.
Supersedes: the first draft of this document (see §10 for what changed and why).

---

## 1. Goals

- Eliminate the root design flaw: the upload package is re-derived from live, mutable project
  state at publish time (`CreatorModeModule.UploadUnsubmittedVersionCoroutine` →
  `FileManagerService.CreateVersionPackageForUpload`, which even deletes and rebuilds the
  version folder on every upload). What is published must be exactly what was built and reviewed.
- One owner for version-storage *lifecycle* (create/replace/delete/scan/migrate). Today that
  responsibility is smeared across CreatorModeModule, VersionActions, FileManagerService,
  DiskSpaceService and MCBEditor.
- One error pipeline (today: every coroutine hand-rolls try/catch + progress bar + dialog +
  warningsModule + console differently). `DiskSpaceService` is dissolved into it.
- One creator form implementation. The IMGUI form is verified dead code:
  `MCBEditor.CreateInspectorGUI()` unconditionally returns the UIToolkit root, so
  `OnInspectorGUI()` (the only caller of the IMGUI `creatorModule.Draw()`) is never invoked by
  Unity. It is deleted, not migrated.
- Streaming upload with progress and cancel (fixes the 30-minute-hang class of issue: today the
  whole zip is loaded into RAM via `WWWForm.AddBinaryData` with a fixed 300 s timeout —
  `NetworkService.SubmitNewVersionAsync`, ~line 357).
- Survive editor domain reloads and interruptions at any step; never destroy the last valid
  artifact before a replacement is complete.
- Keep 100% of current functionality and the server/UnitGit wire contracts.

Non-goals (explicitly unchanged): NativeMeshPayloadService internals, FileManagerService
XOR/zip primitives, NetworkService endpoints and request/response contract, backend API,
UnitGit public API, version folder naming scheme (`u{ver}d{avi}`), `.unitgit-releases.json`.

Constraints: C# 9 only (`/langversion:9.0`), no new IMGUI surfaces (AGENTS.md), no
retrocompatibility beyond the one-time migration approved in §10.

---

## 2. Functionality inventory (must-keep checklist)

Creator form
- [ ] Version major/minor/patch with auto-suggest (parent/applied version + 1 patch)
- [ ] Scope enum, title, changelog, parent version dropdown (compatible versions, default index)
- [ ] Model file build entries (multi-FBX), logic prefab, custom veins (+ auto-assigned
      texture), dynamic normals (body/flexing), suggest-realistic mesh paths, custom
      blendshapes + correctives, advanced mesh replacement toggle + GZip payload flag
- [ ] Validation: version > parent, required metadata, payload presence, veins texture required
      when enabled, per-entry validity; UIToolkit help boxes
- [ ] Populate form from selected unsubmitted version; form pinned to built version after Build
- [ ] Build button (green → gray once built), Publish hidden until built, full-width Build

Build / Publish
- [ ] Build: package files, persist unsubmitted version, apply locally, keep form state,
      enable Publish
- [ ] Publish: validate artifact, upload, remove unsubmitted status, refetch versions, UnitGit
      release checkpoint, success dialog with checkpoint info
- [ ] Recovery when artifact files are missing at publish time (now: explicit Rebuild action,
      see §4.4 — no longer a silent auto-rebuild)
- [ ] Upload size cap (600 MB) + package size logging
- [ ] Upload button on unsubmitted versions in the version list (same pipeline + checkpoint)
- [ ] Auto-saved temporary internal versions (post Blender-sync path, isUnsubmitted=true)

Apply / Reset / Delete (VersionActions today)
- [ ] Apply server version (download zip, RAM-vs-disk advanced mesh decision, XOR FBX patches,
      native mesh payload apply + authoring pose, veins, dynamic normals, logic prefab,
      blendshape value preservation, default avatar handling)
- [ ] Apply local/unsubmitted version (no download); Reset to base; Delete version files +
      generated payload caches + unsubmitted entry
- [ ] Progress UI (VersionApplyProgressState), busy flags, UI refresh after operations
- [ ] FBX backup invariant (`*.fbx.old` always a copy of base A — AGENTS.md) untouched

Storage / housekeeping
- [ ] Unsubmitted version persistence (new mechanism, §4.1; old central JSON migrated once)
- [ ] Imported version discovery (version.json scan under assets/*/versions/*)
- [ ] Offline export of a version folder (`FileManagerService.ExportOfflineVersionPackage`)
- [ ] Generated advanced-mesh cache stats + delete-all (Advanced window)
- [ ] Flush removable data (keep applied + unsubmitted + in-progress), free-space display
- [ ] Disk-full detection across build/publish/checkpoint with clear warning + flush offer;
      distinct message for server-side disk-full (507)

Server interaction
- [ ] Fetch versions + recommended version, access-denied handling, caches (AsyncVersionService)
- [ ] Upload multipart (metadata JSON field + packageFile zip), 401/409/413/507 semantics —
      wire format unchanged, transport reimplemented (§4.6)

UnitGit integration
- [ ] Release checkpoint commit on publish (trailer + .unitgit-releases.json); failure never
      fails the publish
- [ ] Connector test commit, Insert-release from advanced window
- [ ] index.lock retry + stale lock removal; UnitGit window auto-refresh event

---

## 3. Key design decision: manifest + deterministic re-zip (no stored zip)

Today the upload zip is literally `ZipFile.CreateFromDirectory(versionFolder)` — zip content
*is* folder content at packaging time. We exploit that instead of storing a duplicate zip:

- Build writes the version folder as today, plus a `manifest.json` listing every upload-bound
  file with its SHA-1. The manifest is the artifact's identity.
- Publish validates each manifest-listed file hash, then zips **exactly the manifest-listed
  files** and uploads. Content-identical to the build-time package; if any byte changed, publish
  refuses with a Rebuild action instead of silently uploading drifted state.

Why not store `package.zip`:
- Inside the version folder it leaks into offline exports
  (`ExportOfflineVersionPackage` exports the folder recursively) and would recursively include
  itself on rebuilds.
- In `Library/` it dies with Library clears and duplicates up to 600 MB of disk, while the
  fallback would have been manifest-validated re-zip anyway. Cut the middleman.

`manifest.json` (schema versioned, written LAST as the commit marker):

```jsonc
{
  "schema": 1,
  "builderVersion": "<package version>",
  "createdUtc": "...",
  "assetId": 123, "version": "0.3.0", "defaultAviVersion": "...",
  "formSignature": "<sha1 of canonical VersionFormState json>",
  "outputs": [            // exact zip entry set; relative paths inside the version folder
    { "path": "01_body_advancedMesh.bin", "sha1": "...", "bytes": 123 },
    { "path": "default avatar.asset", "sha1": "...", "bytes": 123 },
    { "path": "01_body_advancedMesh.bin.meta", "sha1": "...", "bytes": 123 }   // .meta included: today's zip contains them
  ],
  "inputs": [             // source-drift detection only — NEVER gates publish hard
    { "path": "Assets/.../body.fbx.old", "sha1": "...", "kind": "sourceFbx" },
    { "path": "Assets/.../custom.fbx",   "sha1": "...", "kind": "customFbx" },
    { "path": "Assets/.../logic.prefab", "sha1": "...", "kind": "logicPrefab" }
  ]
}
```

- `outputs` answer "can this artifact be published as-built?" → hard gate.
- `inputs` answer "has the user edited sources since the build?" → publish-time **warning**
  ("Sources changed since build — Publish as built / Rebuild / Cancel"). This is the split the
  review demanded: output integrity ≠ source drift, and drift never triggers a silent rebuild.
- `version.json` (now written into the folder for unsubmitted versions too, see §4.1) and
  `manifest.json` are local-only: present in the folder and in offline exports, **excluded from
  the upload zip** (the zip = `outputs` exactly, preserving today's wire content).
- Known limitation, accepted: prefab *dependencies* are not input-hashed (only the prefab file);
  the `mcb logic.unitypackage` output hash still hard-gates what is actually uploaded.

`ArtifactState` enum: `Valid | SourceDrift | OutputsMissing | OutputsModified | NoManifest |
Corrupt` (the first two are publishable, the rest are not).

---

## 4. Target architecture

All new code in `Packages/orbiters.mcb/Editor/Versions/` (global namespace, no namespace churn).

### 4.1 Data layer

**`VersionFormState`** (plain serializable class)
- Every creator-form field, replacing the ad-hoc fields + SerializedProperties scattered across
  CreatorModeModule/MCBEditor. SerializedProperties that drive UIToolkit bindings stay, but
  their values flow through this class for validation/signature/snapshot.
- `Validate() → FormValidation` — single source of truth for all current rules.
- `ComputeSignature()` — SHA-1 over canonical JSON of **all** fields that affect outputs
  (replaces `GetNewVersionFormSignature()`, which only covered
  version|scope|title|changelog|parent and let entry/toggle edits slip through).
- `SuggestNextVersion(parentOrApplied)`, `PopulateFrom(CustomBaseVersion)`.
- Persisted in `SessionState` keyed by asset id — survives domain reload (today
  `builtPendingVersion` is lost on reload).

**`VersionBuildRequest`** — immutable snapshot of VersionFormState + editor selection (assetId,
fbx/prefab/texture *asset paths*, not live object refs). The builder reads only the request.

**`VersionArtifact`** — immutable handle: folder path + parsed manifest + metadata.
- `ResolveFile(relativePath)` is how *all* read-only consumers (veins drawer, animation offset
  service, apply pipeline) get file paths. No path math outside the repository.

**Unsubmitted persistence — new mechanism.** The central `Assets/MCB/unsubmitted_versions.json`
is replaced by `version.json` (with `isUnsubmitted: true`) written inside each version folder,
discovered by the existing imported-version scan. One index file shared by all unsubmitted
versions was the source of ghost-entry bugs; per-folder metadata makes folder and metadata
atomic with each other. One-time migration in §4.2.

### 4.2 `VersionRepository`

Sole owner of version-storage **lifecycle and path construction**. `MCBUtils.GetVersionDataPath`
becomes private to it (other call sites repathed, §6). Read-only consumers keep working through
`VersionArtifact` handles they obtain from the repository — that is the precise ownership claim
(the draft's "only component touching version storage paths" was overstated and unverifiable).

API: `GetUnsubmitted()`, `GetImported()`, `Find(assetId, version, defaultAviVersion)`,
`Validate(artifact) → ArtifactState` (+ detail lists), `CreateStaging(request) → stagingFolder`,
`Commit(staging) → VersionArtifact` (atomic replace, below), `Delete(artifact)`,
`MarkPublished(artifact)` (rewrites version.json without unsubmitted flag; folder stays as
flushable local cache), `FlushRemovable(keep: applied + unsubmitted + in-progress) → FlushReport`,
`GetDiskUsage()` (absorbs `DiskSpaceService.FlushRemovableData` + generated payload cache stats
via NativeMeshPayloadService).

**Atomic replace** (fixes today's delete-then-rebuild at `CreateVersionPackageForUpload`):
- Build into a *visible* sibling folder `u{ver}d{avi}.building-{shortguid}` in the same parent.
  It must be AssetDatabase-importable because the build pipeline uses `AssetDatabase.CopyAsset`,
  avatar asset generation and `ExportPackage`; hidden folders (`.`-prefix / `~`-suffix) would
  break those APIs.
- Manifest written last inside staging → staging without manifest is garbage by definition.
- Swap: existing final folder moved to `u{...}.trash-{guid}` → staging moved to final name →
  trash deleted. Crash at any point leaves either the old valid artifact or both (trash + new),
  never zero. `.building-*` / `.trash-*` folders are garbage-collected on next repository scan.
- Asset/meta pairing and the single trailing `AssetDatabase.Refresh` are centralized here.

**One-time migration** (runs once per project, then never again):
- Read legacy `unsubmitted_versions.json`; for each entry whose folder exists, write
  `version.json` (isUnsubmitted=true) into the folder and generate `manifest.json` from the
  files currently on disk (their current hashes become the baseline — honest: we cannot know
  build-time hashes retroactively). Entries whose folder is gone are dropped with a console
  note. The legacy file is then deleted. No `LegacyArtifactAdapter`, no downgrade support, no
  permanent legacy read path (per project no-retrocompat rule; approved decision §10).

**In-progress guard:** static registry keyed by final folder path; artifacts being built,
published or applied are excluded from `FlushRemovable` and `Delete`, and a second MCB editor
instance attempting an operation on the same artifact gets a clear rejection.

### 4.3 `VersionBuilder`

`Build(VersionBuildRequest, IProgress<BuildStep>) → VersionArtifact`
- Wraps current `BuildNewVersion` + `CreateVersionPackageForUpload` logic, retargeted at the
  repository staging folder; computes `inputs` hashes before processing and `outputs` hashes
  after `AssetDatabase.Refresh`, writes manifest, calls `repository.Commit`.
- No zip is produced at build time (size estimation for the 600 MB warning uses summed output
  bytes; the authoritative size check still happens on the publish-time zip).
- Pure with respect to editor UI: no dialogs, no Repaint, no progress bars — only IProgress.
- Main-thread (AssetDatabase) — unchanged from today.

### 4.4 `VersionPublisher`

`Publish(artifact, IProgress<PublishStep>, CancellationToken) → PublishResult`
1. `repository.Validate(artifact)`:
   - `OutputsMissing/OutputsModified/NoManifest/Corrupt` → fail with `IntegrityError` carrying a
     **Rebuild** action (rebuild = repopulate form via `PopulateFrom(version.json)` + run Build,
     then the user explicitly publishes again). Never rebuild-and-upload in one silent step —
     that is the live-state bug with extra steps.
   - `SourceDrift` → non-blocking warning dialog: Publish as built / Rebuild / Cancel.
2. Zip exactly `manifest.outputs` to a temp file; verify zip size cap (600 MB); log size.
3. Streaming upload (§4.6) with progress + cancel.
4. On confirmed 2xx only: `repository.MarkPublished(artifact)` → post-publish hooks.
5. Hooks: `IPostPublishHook` — `UnitGitCheckpointHook` (current UnitGitReleasePublisher) and
   `VersionRefetchHook`. Hook failures are reported but never fail the publish (current
   behavior). Cancel during upload → no MarkPublished; a retry that hits 409 because the server
   actually completed is classified as VersionConflict with a "refetch versions" action.

The version-list Upload button and the creator-form Publish button both call this — after
migration there is exactly one publish path (today the list button re-packages from live state).

### 4.5 Operation + error layer

**`OperationRunner`** (one per MCBEditor instance)
- Serializes operations; concurrent attempts are rejected with a message (current implicit
  behavior, made explicit — decision §10).
- Owns progress UI: wraps VersionApplyProgressState + EditorUtility progress bars in one
  implementation; guarantees `finally` cleanup (temp zip/body files, ClearProgressBar, Repaint).
- MCBEditor keeps `isSubmitting/isApplying/isDownloading/isFetching/isDeleting` as thin
  read-only properties delegating to `runner.Current` (~15–20 read sites per flag keep working).
- Coroutine↔Task interop helper (`YieldUntil(Task, CancellationToken)`); network stays
  Task-based, AssetDatabase work stays coroutine/main-thread (EditorCoroutineUtility, as today).

**Error pipeline** — `OperationError { Category, UserMessage, Detail, Actions[] }` +
`IErrorClassifier` chain, first match wins:
`DiskFullClassifier` (Win32 112/39, ENOSPC, message patterns; action: Flush via
`repository.FlushRemovable` — absorbs DiskSpaceService detection) → `ServerDiskFullClassifier`
(507 / "server storage volume") → `AuthClassifier` (401/ACCESS_DENIED; action: open Magic Sync)
→ `VersionConflictClassifier` (409; action: focus version field / refetch) →
`PackageTooLargeClassifier` (413/600 MB; hint: GZip payload flag) → `IntegrityClassifier`
(OutputsMissing/Modified; action: Rebuild) → `GitLockClassifier` (index.lock after retries) →
`NetworkClassifier` (timeout, refused, **cancelled**) → Unknown.
One renderer: warningsModule entry + optional modal with action buttons. Free-space/format
helpers become `DiskUtils`. `DiskSpaceService.cs` is deleted; the Advanced window Disk Space box
calls `repository.FlushRemovable` + `DiskUtils`.

### 4.6 `NetworkService` — streaming upload (full transport refactor, approved §10)

New `SubmitNewVersionStreamingAsync(url, authToken, zipPath, metadataJson, IProgress<float>,
CancellationToken)`:
- Pre-build the complete multipart/form-data body to a temp file (metadata field part + file
  part headers + zip bytes streamed via `FileStream.CopyTo` + closing boundary).
- `UnityWebRequest` with `UploadHandlerFile(bodyPath)` (streams from disk — no 600 MB byte[])
  and explicit `Content-Type: multipart/form-data; boundary=...`.
- Progress: poll `req.uploadProgress`/`uploadedBytes`; cancel: `req.Abort()` on token; replace
  the fixed 300 s timeout with stall detection (no uploaded-bytes movement for N seconds) so big
  uploads on slow links neither hang forever nor get killed mid-flight.
- Same endpoint, field names (`metadata`, `packageFile`), auth header, and response handling —
  wire-compatible with the backend. Old `SubmitNewVersionAsync` is deleted (single caller).
- Temp body file cleanup in `finally`; body file creation is also covered by disk-full
  classification.

### 4.7 Apply layer

**`ApplyService`** — `VersionActions` renamed/slimmed: same coroutines and internals (advanced
mesh, authoring pose, veins, normals, blendshape snapshot/cache sync, FBX backup invariant),
but every version-folder path obtained via `VersionArtifact.ResolveFile` / repository, and all
operations launched through OperationRunner + classifiers. Delete delegates folder + payload
cache + unsubmitted-status removal to the repository.

### 4.8 UI layer

- `CreatorModeModule.UIToolkit.cs` becomes the only form implementation, rebinding to
  VersionFormState; `CreatorModeModule.cs` shrinks to composition + event wiring (target
  < 600 lines from today's ~2 700 + 1 800 split).
- The dead IMGUI form (`Draw()` + Draw* helpers in CreatorModeModule.cs) and the dead
  `MCBEditor.OnInspectorGUI` body it depends on are deleted. Pre-deletion check: grep that no
  other module invokes `creatorModule.Draw()` or `OnInspectorGUI` reflectively.
- `VersionListDrawer.UIToolkit.cs`: visually unchanged; Upload/Apply/Delete wired to
  VersionPublisher/ApplyService/VersionRepository through OperationRunner. The legacy
  `VersionListDrawer.cs` IMGUI list is audited the same way as the form: if dead, delete in the
  same pass; if still reachable (offline view path), repoint its three action callbacks only.
- Advanced window: Generated meshes + Disk Space + UnitGit sections call the new services;
  behavior identical. Upload progress bar + Cancel button surfaced via OperationRunner state.

---

## 5. Flow walkthroughs (old → new)

**Build**: form → `VersionFormState.Validate` → snapshot `VersionBuildRequest` →
OperationRunner(Build) → VersionBuilder.Build (staging → hash inputs → produce outputs → hash
outputs → manifest → repository.Commit) → ApplyService.Apply(artifact) → form re-pinned via
`PopulateFrom` + `formSignature`. Failure → error pipeline (disk-full → flush offer). Crash →
`.building-*` GC'd on next scan; previous artifact untouched.

**Publish (form)**: gated by `FormState.ComputeSignature() == artifact.manifest.formSignature`
→ OperationRunner(Publish) → Validate → (drift? ask) → zip outputs → streaming upload w/
progress+cancel → MarkPublished → hooks → success dialog with checkpoint info.

**Upload from version list**: identical Publish op on the artifact from the repository.
Artifacts with broken outputs get the IntegrityError + Rebuild action instead of today's
silent live-state re-package.

**Apply / Reset / Delete / Flush / Fetch**: same logic as today, repathed through
repository + OperationRunner + classifiers.

**Offline export**: unchanged folder export; now also carries version.json + manifest.json
(harmless, and makes imports self-describing). No zip to leak — resolved by design.

---

## 6. File plan

New (`Editor/Versions/`): `VersionFormState.cs`, `VersionBuildRequest.cs`,
`VersionArtifact.cs` (+ manifest model), `VersionRepository.cs` (+ migration),
`VersionBuilder.cs`, `VersionPublisher.cs` (+ hooks), `ApplyService.cs` (moved VersionActions),
`OperationRunner.cs`, `Errors/` (OperationError, classifiers, renderer), `DiskUtils.cs`.

Modified — complete consumer list (the draft understated this; every known
`GetVersionDataPath`/path-math/scan/index consumer is enumerated):
- `MCBEditor.cs` — busy flags → compat properties; imported/unsubmitted lists served by
  repository scan; delete dead `OnInspectorGUI` body.
- `Features/CreatorModeModule.cs` + `.UIToolkit.cs` — per §4.8; SaveUnsubmittedVersion/
  RemoveUnsubmittedVersion/central-json code deleted.
- `Features/VersionManagement/VersionActions.cs` → `ApplyService` (repathed).
- `Features/VersionManagement/VersionListDrawer.UIToolkit.cs` (+ `.cs` per audit) — action wiring.
- `Features/VersionManagement/VersionManagementModule(.UIToolkit).cs` — list sourcing.
- `Services/FileManagerService.cs` — `CreateVersionPackageForUpload` retargeted to staging
  folder + split from zip step; `ExportOfflineVersionPackage` takes a `VersionArtifact`.
- `Services/AsyncVersionService.cs` — download target paths via repository.
- `Services/NetworkService.cs` — §4.6.
- `Features/CustomVeinsDrawer.cs`, `Services/AnimationPositionOffsetService.cs` — read-only:
  switch to `artifact.ResolveFile(...)` (mechanical).
- `Services/UnitGitReleasePublisher.cs` — becomes `UnitGitCheckpointHook`; its DiskSpaceService
  call → classifier.
- `AdvancedModeModule.cs` — service calls.
- `MCBUtils.cs` — `GetVersionDataPath` made private/moved into repository.

Explicitly exempt (paths passed in, never constructed): `NativeMeshPayloadService` (writes
.bins to paths the builder provides; payload cache enumeration stays its own, called by
repository for flush stats).

Deleted: `Services/DiskSpaceService.cs`, IMGUI creator form + dead OnInspectorGUI path,
central unsubmitted-json read/write (`UserCustomVersionService` write+read after migration),
`SubmitNewVersionAsync` (replaced).

Rough scope: ~3–3.5k new/moved lines, ~2.5k deleted; the big services are mostly relocations.

---

## 7. Corner cases catalog

1. Domain reload between Build and Publish → FormState in SessionState + artifact/manifest on
   disk: Publish recovers (today builtPendingVersion is lost).
2. Editor crash mid-build → staging + manifest-last → old artifact intact, staging GC'd.
3. Crash mid-swap → old folder in `.trash-*` and/or new folder present; scan repairs (prefer
   folder with valid manifest; trash deleted only after final folder validates).
4. Disk full during build/zip/body-file write → classifier + flush offer; flush never touches
   in-progress/applied/unsubmitted; re-check free space after flush.
5. Disk full during git commit → GitLock vs DiskFull distinguished by message; checkpoint
   failure never fails publish.
6. Server 409 → "bump the version" message + refetch action (also covers retry-after-cancel
   where the server actually completed).
7. Auth expiry mid-flow → AuthClassifier; MarkPublished only after confirmed 2xx.
8. User edits FBX/prefab between Build and Publish → `inputs` drift → warning (publish-as-built
   allowed); outputs untouched so the upload is still exactly the build.
9. Version folder file deleted/modified between Build and Publish → `outputs` invalid → hard
   IntegrityError + Rebuild action. No silent rebuild ever.
10. Cancel mid-upload → Abort, temp files cleaned, unsubmitted state unchanged.
11. Two MCB editors / two avatars → per-editor OperationRunner + static in-progress registry
    keyed by artifact path.
12. Parent version deleted server-side after build → publish proceeds (server validates);
    refetch updates dropdown.
13. Migration: legacy json entry with missing folder → dropped with console note; corrupt json
    → logged, file renamed `.bak`, treated as no legacy data.
14. Imported (downloaded) versions have version.json but no manifest → `NoManifest`: applyable
    as today, not publishable (they're published already); excluded from publish UI.
15. meta files: included in `outputs` (today's zip contains them); folder operations always
    pair file+meta; single trailing AssetDatabase.Refresh in repository.
16. Flush while publish awaits upload → in-progress guard blocks it.
17. Main-thread constraints unchanged: builder/apply on main thread, only network IO off-thread.
18. Blender-sync auto-saved internal versions go through VersionBuilder too (same artifact
    invariants, isUnsubmitted=true).

Removed from draft: permanent downgrade support (old MCB will not see post-refactor unsubmitted
versions — accepted, §10).

---

## 8. Implementation order (single branch, always compiling)

1. Data layer: VersionFormState / BuildRequest / Artifact / manifest model + EditMode tests.
2. VersionRepository: scan, validate, staging/commit/swap, flush, migration + tests (pure IO,
   testable with temp dirs).
3. OperationRunner + error pipeline + classifier tests.
4. NetworkService streaming upload (+ multipart body writer test against a recorded request
   shape).
5. VersionBuilder + VersionPublisher (+ hooks).
6. Rewire ApplyService, version list, Advanced window, UnitGit hook, read-only consumers.
7. CreatorFormView binding; delete IMGUI form + dead OnInspectorGUI + DiskSpaceService +
   legacy persistence.
8. Full manual matrix (§9), health checks, then merge.

## 9. Test plan

EditMode tests (new `Tests/Editor` asmdef — none exists today): FormState
validation/signature/suggest; repository scan/migrate/validate/staging-swap/flush (temp dirs +
fake artifacts, simulated crash points between swap steps); manifest hashing incl. meta files;
zip-from-outputs entry-set equality vs `ZipFile.CreateFromDirectory` on a fixture folder;
multipart body writer byte-level format; every classifier (code/message → category + action).

Manual matrix (test avatar project):
{Build, Publish, Upload-from-list, Apply server, Apply local, Reset, Delete, Flush, Export}
× {simple FBX patch, advanced mesh (+GZip on/off), veins, dynamic normals, blendshapes}
× failure injections {delete a .bin before publish, edit source FBX before publish (expect
drift warning), revoke token, stop server, 409 duplicate, simulated disk-full, git index.lock
held, cancel mid-upload}.
Plus: domain reload between Build and Publish; kill editor mid-build and mid-swap; legacy
project with old unsubmitted json (migration); 500 MB+ package upload with progress + cancel.

Per AGENTS.md: run `Tools > My Custom Base (MCB) > Health Checks > All Deterministic`
(`MCBEditorHealthChecks.RunAllOrThrow` in batch mode) before finishing any step that touches
apply/reset, FBX backups, payloads or applied-version caches; otherwise
`dotnet build mcb.Editor.csproj --no-restore` + report that editor checks were not run.

---

## 10. Decision log (all previously open points are now closed)

| Decision | Outcome | Rationale |
|---|---|---|
| Artifact immutability mechanism | **Manifest + deterministic re-zip; no stored package.zip** (user-approved) | Content-exact uploads without export leakage, duplicate disk, or Library-clear fragility; resolves review P1 #1 and #2 |
| Output integrity vs source drift | **Split**: outputs hard-gate with explicit Rebuild action; inputs warn only | Auto-rebuild-on-publish would reintroduce the live-state bug (review P1 #1) |
| Legacy unsubmitted versions | **One-time migration, then legacy file deleted; no adapter, no downgrade support** (user-approved) | Project no-retrocompat rule; permanent adapter kept the live-state path alive (review P1 #4) |
| Upload transport | **Full streaming refactor** with temp-file multipart body, UploadHandlerFile, progress, cancel, stall-based timeout (user-approved) | byte[]+WWWForm cannot honestly support a 600 MB cap (review P1 #5) |
| Storage ownership claim | Repository owns lifecycle + path construction; readers use artifact handles; full consumer list in §6; NativeMeshPayloadService explicitly exempt | Review P1 #3 |
| Safe replace | Same-parent visible staging (`.building-*`), manifest-last, trash-swap; staging must stay AssetDatabase-importable (CopyAsset/CreateAsset/ExportPackage) | Review P2; hidden-folder staging would break asset APIs |
| IMGUI creator form | **Delete** — verified dead (`CreateInspectorGUI` always returns UIToolkit root) | Evidence-based, replaces draft's open question |
| Unsubmitted persistence | Per-folder version.json replaces central index | Eliminates ghost-entry class of bugs |
| Published artifact folders | Keep as flushable local cache after MarkPublished | Draft recommendation, no objection |
| Concurrent operations | Reject with message (no queue) | Matches current behavior, explicit |
