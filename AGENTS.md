# AGENTS.md

## Language / Unity Constraints
- Unity compiles this package with `/langversion:9.0`.
- Do not introduce C# 10+ features (including global using) or compiler override changes.

## Architecture / Code Quality
- Before adding feature-local helpers, search for existing package utilities and service-layer helpers. Reuse or extend the existing shared path when the behavior is not truly feature-specific.
- Backend/API URL construction must stay centralized. Use `MCBUtils.getApiUrl(...)` for API origins and `MCBUtils.ResolveApiUrl(...)` for backend-relative paths or backend URLs returned by the server. Do not add UI-specific URL normalization helpers for individual panels, banners, thumbnails, or similar resources.
- If `MCBUtils` stops being the intuitive home for request URL logic, move the shared URL/request helpers into a clearly named common service in the same change and update call sites. Do not leave parallel URL-building logic split across UI modules.
- Keep UI modules focused on UI state and presentation. Network request policy, URL generation, caching, serialization, and cross-feature behavior belong in shared services/utilities.
- Avoid narrowly named helpers when the behavior is generic. A helper named for one screen or resource should only exist when its rules are genuinely unique to that screen/resource.

## Editor UI Direction
- MCB editor UI is transitioning to Unity UI Toolkit with the shared styled surfaces used by the gallery and account modules.
- Build every new UI element and every changed UI surface with UI Toolkit and the package USS style sheets.
- Do not add new IMGUI UI unless the user explicitly asks for it or the touched surface has not been migrated yet and cannot be safely migrated in the requested change.

## Health Checks
- When touching version apply/reset code, FBX backup handling, native mesh payloads, advanced mesh paths, dynamic normals, material mutations, slider creation, blendshape preservation, or applied-version caches, run the deterministic editor health checks before finishing.
- Preferred Unity menu path: `Tools > My Custom Base (MCB) > Health Checks > All Deterministic`.
- Preferred batch-mode entry point: `MCBEditorHealthChecks.RunAllOrThrow`.
- If Unity cannot be launched, run at least `dotnet build mcb.Editor.csproj --no-restore` from the Unity project root and clearly report that the editor health checks were not run.

## BlendShape Link System

### Goal
- Drive corrective activation from an already-animated source (`toFix`) to a target (`fixedBy`), multiplied by a factor.
- Supported target types:
  - `Blendshape`
  - `Animation`
- Apply this only to VRCFury-generated temporary controllers at build/preprocess time.
- Never mutate the original user-authored controllers directly.

### Two Link Sources

1. Manual link (Advanced Mode test drawer)
- UI: `Editor/Features/BlendShapeLinkTestDrawer.cs`
- Saved as persistent config in `MyCustomBase.blendShapeFactorLinks`.
- Fields:
  - target renderer path
  - `toFixType`, `toFix`
  - `fixedByType`, `fixedBy`
  - factor parameter name
  - enabled flag

2. Current-version links (version JSON corrective definitions)
- Source data: `customBlendshapes[].correctives` (mapped internally to `correctiveBlendshapes`).
- Runtime/build fallback cache: `MyCustomBase.appliedVersionBlendshapeLinksCache`.
- Links are generated per renderer when either side uses `Blendshape` (all skinned meshes, not only Body).
- Links with `Animation -> Animation` are generated once (no renderer path dependency).
- Factor selection:
  - If driver blendshape is an active slider: use slider global param.
  - If not: use a constant factor parameter with default value equal to current driver blendshape weight (0..1).

### VRCFury Interaction
- Sliders are created in `Editor/Services/VRCFuryService.cs`.
- For each slider toggle, force:
  - `content.useGlobalParam = true`
  - `content.globalParam = VRCFuryService.GetSliderGlobalParamName(sliderName)`
- This guarantees stable param names for version-based links.

### Build/Play Hook Execution
- Hook class: `Editor/Services/BlendShapeLinkPostVrcfuryHook.cs`
- Interface: `IVRCSDKPreprocessAvatarCallback`
- Order: `-9000` (after VRCFury, which runs at `-10000`).
- Runs for:
  - Upload build preprocess
  - Play mode preprocess path
- Applies:
  - version-based links
  - manual links

### Animator Mutation Strategy
- Core service is split (partial class):
  - `Editor/Services/BlendShapeLinkService.cs` (planning/lookup/signature build)
  - `Editor/Services/BlendShapeLinkService.LinkResolution.cs` (manual link resolution/validation)
  - `Editor/Services/BlendShapeLinkService.Rewrite.cs` (state machine + blendtree rewrite)
  - `Editor/Services/BlendShapeLinkService.VariantOps.cs` (clip variant creation/curve ops)
- Collect only VRCFury temp controllers (`com.vrcfury.temp`).
- For each matching motion (states and nested blendtrees):
  - clone clip as variant
  - apply corrective based on type combination:
    - `Blendshape -> Blendshape`: destination blendshape curve copied from source blendshape curve
    - `Animation -> Blendshape`: destination blendshape forced to 100 in matching animation clips
    - `Blendshape -> Animation`: overlay animation curves blended by source blendshape activation
    - `Animation -> Animation`: overlay animation curves copied to matching animation clips
  - create wrapper 1D blend tree with factor parameter:
    - child 0 threshold 0: original clip
    - child 1 threshold 1: variant clip
  - rewrite state/tree motion refs to wrapper
- Wrapper stacking:
  - if a wrapper for the same factor already exists, recurse into the wrapper variant child so multiple links can stack safely.
- Ensure factor parameter exists as Float in each processed controller.
- Wrapper/variant assets are attached as sub-assets to the temporary controller.

### Animation Matching Rules (`toFixType = Animation`)
- Never rely on VRCFury temp asset paths.
- Matching priority:
  - 1) Semantic signature match against the reference `toFix` animation clip:
    - same animated binding (`path`, `type`, `propertyName`)
    - same sampled values (epsilon-tolerant)
  - 2) Normalized clip name match (`clip.name`)
  - 3) Normalized source file name match (`*.anim` name)
- Normalization:
  - case-insensitive
  - strips `.anim`
  - removes non-alphanumeric characters

### Clone / Upload Robustness
- Preprocess may run on cloned avatar objects with editor-only components missing.
- `BlendShapeLinkService.FindCustomBase` includes fallback lookup by root name against scene `My Custom Base` instances.
- Version links additionally fall back to serialized cache (`appliedVersionBlendshapeLinksCache`) when `appliedCustomBaseVersion` is unavailable.

### Version Cache Synchronization
- `Editor/Features/VersionManagement/VersionActions.cs` synchronizes version blendshape cache:
  - set on apply/match
  - clear on reset/custom-version application
- This keeps upload preprocess deterministic even when full version objects are not present.

### Advanced Mode Debug UX
- Drawer shows:
  - manual test controls (enable/disable + save config)
  - live list of active version links
  - per-link typed endpoints (`toFixType:toFix -> fixedByType:fixedBy`)
  - factor parameter name
  - exact constant factor value for non-slider links
- Link debug data comes from `BlendShapeLinkService.GetActiveVersionLinkDebugInfo`.

### Important Rules
- Do not patch default/read-only VRChat package controllers.
- Do not patch authoring controllers directly.
- Keep link operations idempotent and safe across repeated preprocess calls.
- Keep naming deterministic for parameters and generated assets.

## Custom Base FBX Backup Invariant
- For a custom base version B/C applied over a default base A, every affected `*.fbx.old` file must always remain a copy of A.
- Applying any custom FBX or downloaded/unsubmitted version may create `*.fbx.old` if it is missing, but must never overwrite or move an existing `*.fbx.old`.
- Resetting to Base Default copies `*.fbx.old` back over `*.fbx` and leaves `*.fbx.old` in place.
- The only valid state without `*.fbx.old` is the untouched default-base state where `*.fbx` itself is A.
- XOR `.bin` patches are defined against A, so all version switching logic must read from `*.fbx.old` when it exists.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify` or `$graphify`, invoke the `skill` tool with `skill: "graphify"` before doing anything else.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
