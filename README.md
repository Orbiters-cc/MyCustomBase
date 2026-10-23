# My Custom Base (MCB) by Enzo

## 1.12.13 — 2026-10-23

- MCB finds the avatar's custom base while Orbiters Mixer edits its meshes. A mesh in a Mixer session shows a working
  copy that is no asset: MCB found no model file behind the body and listed no custom base ("No matching avatar assets
  were found for this avatar", on any server, however often you reconnected), and it took an outfit's FBX for the base
  model, which ReFit then offered as the original base. MCB now reads each mesh's model file through the working copy
  (`AvatarSceneMeshProvenanceService.ModelPathOf`, with Mixer 0.1.1 or later); the avatar's base FBX list puts itself
  right the next time MCB's Inspector shows it.
- Mixer's Hierarchy shows **Update** beside the avatar's custom base when MCB's server recommends a newer version than
  the one applied (from the version list MCB read last, read again in the background when there is none). Clicking it
  opens the avatar in MCB's Inspector, which applies that version with its usual confirmation and progress, as its
  version list does. Needs Orbiters Toolkit 0.4.2.

## 1.12.12 — 2026-10-21

- **Export FBX** in a version's details (beside See differences, on versions that ship meshes) saves the version's
  model as an FBX file without applying it. A version that replaces the model (XOR or HDiff) gives the very file its
  creator published, decoded from your original model and checked against its hash; a version of advanced meshes gives
  the avatar's meshes from its models as the version makes them. The version must be downloaded, and the avatar must
  have the original model the version was made for.

- A version built while the avatar sits under another object in the scene records its renderers from the avatar's
  root, where applying a version looks them up: such versions apply (they stopped with "Cannot find the avatar
  renderer"). The create form, the mesh suggestions and the version comparison read the same root.

- With Orbiters Mixer: a custom base's corrective shapes show in Mixer's blendshape list (the Skinned Mesh Renderer
  inspector's target button): the shape a corrective fixes, the corrective fixing a shape and the custom blendshape
  scaling it, from the applied version and from the BlendShape factor links set by hand.

- Work run to its end at once (the deterministic health checks, tests) no longer waits for Unity to finish compiling or
  importing, which it cannot do meanwhile: a script saved during such a run hung the editor for good (`MCBWork.EditorBusy`).
- **Share MCB diagnostics** now goes through Orbiters Toolkit's shared reporter: any account's Orbiters token, VRChat
  cookies, license and API keys, %40 e-mails, VRChat ids, names and user paths with spaces are masked before anything is
  cut; a project path keeps its file, line and reason; a varying warning goes once an hour; exceptions are sent as
  errors; tests' logs are never sent. The tooltip says what is sent.
- **Export FBX** beside Apply in the ReFit option writes the re-fitted assets as they look fitted (the fit applied, the
  version's blendshapes kept at their weights) with their armature to one FBX file that Unity and Blender import as it
  is. Requires Orbiters Toolkit 0.4.1.
- Blender export imports no longer overwrite each other. Each custom base has its own folder, named after its stable
  id instead of the asset's name (`Assets/MCB/generated/blenderExports/<base id>`, `blenderAdvancedExports~/<base id>`,
  `blenderPreviews/<base id>`), so two avatars of one asset, or two bases of the same name, no longer replace each
  other's custom FBX and Avatar. Each import is named after the export's content (`NN_<target>-<hash>.fbx`, 16 hex
  digits, under Windows' 259-character limit), so it never overwrites an earlier import: Undo goes back to the previous
  custom FBX, and importing the same export twice reuses the first copy.
- The copies imports leave behind are cleaned up: once per editor session (30 s after start), and with **Flush
  Removable Data**, the imported exports, Avatars and advanced-mesh previews nothing uses any more go to the Recycle
  Bin, and so do the advanced-mesh export copies. Only MCB's own file names in those folders are touched. A copy stays
  while a saved scene, prefab or asset uses it, while an open avatar does (unsaved changes and the creator's entries
  included), and for the rest of the session once an import used or made it, so Undo still finds it.
- A manifest can only name FBX files in its own export folder: absolute paths, `..`, links and device names are
  refused, so a crafted manifest can no longer copy another file of the computer into `Assets` (and into the next
  version's upload).
- Mixer and Blender edits that were committed are in the versions built after them. Building a version writes the
  meshes Mixer committed on the base's renderers into a new custom FBX of their model, with Orbiters Toolkit's FBX
  export: every object, mesh, blendshape and material slot name of the model, the committed meshes in place of theirs
  (`Assets/MCB/generated/blenderExports/<base id>/NN_<target>-<hash>.fbx`). The model's entry names that FBX from then
  on, so the version holds what the avatar shows, with advanced meshes (XOR or not) and with FBX patches alike. Taking
  the same meshes again reuses the file.
- The build's confirmation names those meshes and offers **Build with them** (the default) or **Build without them**,
  and the warnings say what was taken. Builds without a dialog take them too: MCP's `mcb_authoring` build lists them in
  `takenFromAvatar` and its draft names the new custom FBX. A mesh still being edited in Mixer or Blender stops the
  build until it is committed or reverted. Armature edits (another rest pose, weights on bones the model lacks) and
  models imported with a Scale Factor other than 1 or Convert Units off are refused, with the way in: Export for MCB
  in Blender, then import the export on the Blender card. The Blender card says which meshes the next build takes in.
- Publishing a version built earlier asks first when the avatar shows Mixer meshes its entries do not hold (committed
  after the build, left out of it, or still in a Mixer session): **Publish as built** or cancel and build again. MCP's
  publish preview says the same in `avatarBeyondVersion`.
- The import is safer: a model without a target goes to the creator's first source file (never a SOURCE file the
  creator does not use), several models without a target or two models for one target are refused, a model whose FBX
  leaves out meshes Unity keeps out of Blender (Mixer's `missingUnityTargetMeshNames`) is refused naming them, instead
  of removing them from the base, XMuscles correctives
  are part of the import's Undo step (a failure leaves the avatar unchanged), a failed rollback no longer hides the
  import's own error, the status warns when no renderer took the new meshes, the copied FBX is readable from its first
  import (the version build no longer imports it again to turn Read/Write on), and no full asset refresh runs.
- Mixer's Export for MCB groups meshes as the Blender card does: a base on a child object of the avatar is found, an
  inactive outfit copy of a base mesh is no longer exported with it, Blender learns a changed target at once, and the
  FBX mesh names are read again only once Unity imports the FBX again. Advanced-mesh previews get their dynamic normals
  for avatars placed under another object, and switching the legs-apart option makes a new preview.
- **Import the newest export** goes away once that export is imported, shows only on the base it was made for, and is
  kept per project folder. The Blender card looks up the base's renderers every 3 s and after hierarchy changes instead
  of every half second, builds once per action, and a failure in Mixer's part (or in any inspector section) no longer
  hides the rest of the form. MCB's Mixer integration only compiles with Mixer 0.1.x.
- Building a version turns Read/Write on in the creator's model import settings when they lack it (since 1.12.9): the
  advanced mesh payload reads the meshes.
- Leftovers of the Blender sync that 1.12.11 removed are no longer used: `Assets/MCB/blenderProjects` (its `.blend`
  files make Unity start Blender on every import), `Library/MCB/BlenderSync`, `Library/MCB/BlenderSyncRollback` and
  `Library/MCB/BlenderLaunch`. MCB leaves them alone (a `.blend` may hold your work): delete them once you no longer need
  them.

- The Blender card's **Revert** asks first when the mesh holds Mixer edits not committed, or while Blender edits it:
  Revert also sends the original mesh back to a linked Blender (its positions, shapes and weights; UVs and what was made
  in Blender stay), and a Blender editing the mesh keeps that edit and offers to send it again or take Unity's (Mixer's
  Blender extension of this release). The tooltip and the confirmation say so.
- A Blender export import on an avatar placed under another object gives the new Avatar to that avatar's own Animator
  (no Animator is added to the object above it) and only touches that avatar's renderers. So do Apply Custom FBX, Apply
  Custom Avatar and Generate Avatar in the creator mode. Mixer's Export for MCB keeps each avatar's meshes with its own
  base when several avatars share a parent, and a prop beside them belongs to none.
- The Blender card's messages belong to their base: a failed import of one base no longer shows on another's card, and
  a Mixer message no longer rebuilds every open MCB inspector. An import waits while a version is built, downloaded or
  applied, and says so. The newest export's manifest is read again only when it changes.
- When only some renderers take the imported meshes, the import's status names those that kept their previous meshes
  instead of saying the avatar was updated.
- Undo and Redo of a version switch put back only the files the switch changed, and only while they are as the switch
  left them: an FBX exported again or import settings changed since stay as they are (the Console names them), and
  another base's dynamic normals in the same folder are left alone.
- With Orbiters Mixer, corrective shapes are listed for avatars placed under another object and for a base on a child
  object; an avatar of the same name without MCB no longer shows them, and an animation-only link no longer shows on
  unrelated meshes. BlendShape factor links set by hand in Advanced Mode name their mesh from the avatar, as the build
  reads them.
- The health checks' deliberate failures are no longer sent as diagnostics, and logs below the level sent no longer
  take a stack trace.
- The cleanup of advanced-mesh copies reads scenes and prefabs in pieces instead of whole (they can be hundreds of MB).

## 1.12.11 — 2026-10-19

- Blender goes through Orbiters Mixer: the Blender card of the creator mode opens the scene in Blender with Mixer's link
  (optional dependency `orbiters.mixer`), shows which of the base's meshes Blender or Mixer is editing, and commits or
  reverts those edits. MCB's own Blender sync (the MCB Blender add-on, per-base Blender projects, Magic Sync, the live
  preview and its inbox) is gone, with its Advanced mode settings.
- Blender exports still come back through their manifest: **Import Blender export…** picks a manifest.json and makes
  each exported model the creator's custom FBX for its target (with a generated Avatar) or an advanced-mesh payload
  preview; XMuscles correctives arrive the same way. Imports land under `Assets/MCB/generated/blenderExports/<base>`.
- Mixer tells Blender which meshes belong to which target FBX of the base, so the Mixer extension's **Export for MCB**
  writes that manifest; when it does, the Blender card offers **Import the newest export**.

## 1.12.10 — 2026-10-19

- Requires Orbiters Toolkit 0.4.0: Blender executables are found by the Toolkit's `BlenderInstalls` (the chosen executable is now EditorPrefs `Orbiters.Blender.Executable`).

## 1.12.9 — 2026-10-13

- Cleaning up generated meshes after a reset or a version switch no longer trusts file dates (a checkout writes files in
  any order): it follows every scene, prefab and asset of the project to what it uses, through controllers and the
  animations they play, keeps each file's references in Library so the next cleanup reads only what changed, runs in
  the background with Unity's progress bar (Cancel stops it) and moves unused meshes to the Recycle Bin. A mesh an open
  animation swaps in, and one that comes back after being missing, are kept.
- The version buttons can no longer stay disabled: a path too long for Windows, an unusable folder or a locked FBX when
  a download starts says why and ends the apply, and so does a local build that isn't complete.
- Removing copied veins only takes MCB's own copies (in the version folders, or the texture MCB recorded when it applied
  veins): a creator's own veins stay.
- An exported version no longer carries your computer's paths (in `version.json` and the build manifest), and exporting
  no longer rewrites the version folder's files.
- **Fill from Parent** finds the parent's avatar for each model file, takes the logic prefab from the logic package as
  imported, keeps the blendshapes when the parent has none, asks before replacing what the form holds, and its note
  updates once the parent is downloaded.
- MCP `update_listing` sends the avatar's original FBX paths when it names a new base, and base registration errors
  show the server's message.
- Version validation builds with Orbiters Toolkit's shared VRCFury settings and, as an upload does, skips VRCFury on
  avatars without VRCFury components.
- The Magic Sync Prod/Dev buttons are gone from Advanced settings (the website no longer makes those tokens): sign in
  with Discord or Telegram.
- Needs Orbiters Toolkit 0.3.22.

## 1.12.8 — 2026-10-11

- **Fill from Parent** next to the creator form's Parent Version: fills the new version with the parent's model files,
  avatars, logic prefab, blendshapes and correctives, custom veins, dynamic normals, suggest realistic, advanced mesh,
  HDiff and customization options. The version number, scope, title and changelog stay. A field is only ever set, never
  cleared: published versions do not carry the creator's local files, so the logic prefab, avatar and veins texture
  come from the parent's downloaded version folder, and a note under the dropdown names what could not be filled.
- An original base no longer shows as one of the releases published before per-model patch files (UltiPaw 1.0.0 to
  1.0.6): their expected model hash was the original's, so every untouched original "matched" one and showed it as
  installed after a reset.
- Resetting or switching away from an advanced version took minutes (174 s on a large project): the cleanup of its
  generated meshes searched every scene, prefab and asset of the project, recursively, for references. It now reads
  only the files saved since those meshes were created, skips mesh assets, and follows direct references.
- Custom veins are removed on reset and on switching to a version without them when the material holds a copy of the
  veins texture, such as the one My Avatar's texture optimization makes in its own folder. Veins are recognised by
  content, not only by their location under `Assets/MCB`.
- After a failed **Download and Apply** (for instance a version whose files are missing on the server), the version
  buttons were rebuilt while the download still counted as running and stayed greyed out. They are usable again.
- The creator window no longer leaks its MCB editor on every script reload. Each leaked editor stayed alive with its
  version fetches, hashing and event subscriptions; editors left by earlier reloads are destroyed when the window
  reopens.

## 1.12.7 — 2026-10-11

- Fix **Extraction failed: Could not find a part of the path** when downloading a version into a project at an ordinary
  path (about 40 characters, e.g. `D:\VRChat Stuff\Unity Projects\Ultinova`): the download's staging folder added a
  42-character suffix to an already long version folder, past the 259 characters Unity can use on Windows. Staging
  and replaced folders now add 18 characters at most (Orbiters Toolkit 0.3.21).
- Hashes in MCB's local names are cut to 16 hex digits (`MCBUtils.PathHash`): version folders
  (`u1.0.0d1.0.0-60281cc859dc167a`, was `u1.0.0d1.0.0-source-<64 digits>`), generated advanced meshes and their humanoid
  Avatars, refit caches, and the files a build makes for its other originals. Ultinova's longest version file now
  needs 128 characters past the project folder instead of 188. Nothing is migrated: a version downloaded with an
  earlier MCB is downloaded again when applied (an avatar using it offers **Download and Update** to the same version
  once), saved versions exported before are not recognised, and refits saved for versions that are not applied are
  not found again.
- Before downloading, MCB checks that the version's files fit at the project's location; if not, it says how many
  characters shorter the project's folder path must be instead of failing after the download.
- A saved version exported from a creator's build of several originals installs offline for each of them. The
  server's entry used to win over the local copy of a non-primary original and pointed at that original's own folder,
  so MCB downloaded it again. A published version whose local build no longer holds the original now downloads the
  original's own copy instead of reporting "This local build is incomplete".
- Exporting a saved version keeps the version folder's own `version.json`. It was rewritten with the row's metadata:
  exporting one original's view of a creator's build replaced the build's metadata, hid the build from MCB and made the
  package unrecognisable on import.
- Applying or resetting no longer logs "Shader … is not supported" errors for avatars whose shader MCB never puts veins
  on (lilToon, a missing shader): there is nothing of MCB's to remove there.
- Advanced versions use the avatar's own copy of their original as the pose source when it sits at another path (a
  moved or renamed package folder). For another original (Novabeast V1.2 under a version built on V1.0) the payload's
  own skeleton stays the pose, now an information line instead of two warnings on every apply.
- Restoring pre-MCB backups stages its copies under hidden names (`~`), so Unity no longer imports them, and leaves
  stray `.meta` files, during the restore's refresh. The original-base backup's temporary copy has a short name too.
- Requires Orbiters Toolkit 0.3.21.

## 1.12.6 — 2026-10-10

- The sliders MCB adds to the menu get Orbiters' gauge icon (`OrbitersMenuIcons.Slider`, from Orbiters Toolkit).

## 1.12.5 — 2026-10-09

- The account row and its sign-in buttons are Orbiters Toolkit's (`OrbitersAccountElement`), as in My Avatar: MCB's own
  account and sign-in modules are gone. Signing out still asks first. The sign-in buttons now show in the account row
  under the banner instead of a card above the gallery. MCB still reads the signed-in creator's details (whether Orbiters
  trusts them, for a new custom base's protection settings).
- Source FBX files are taken from a `.unitypackage` through Orbiters Toolkit's `UnityPackageReader`, the one reader every
  Orbiters tool shares: the package's structure is checked as strictly as before an import (tar checksums, record names,
  one GUID per path), and each FBX is hashed while it is extracted instead of being read again afterwards.
- The server address comes only from Orbiters Toolkit: `MCBUtils`' unused `SERVER_BASE_URL`, `API_BASE_URL`,
  `TOKEN_ENDPOINT` and its own copy of the development switch's key are gone.
- The bone position offsets find the avatar's MCB component with the blendshape links' lookup instead of a copy of it;
  the unused `BlendShapeLinkService.ApplyVersionLinks` is gone.
- Requires Orbiters Toolkit 0.3.19.

## 1.12.4 — 2026-10-08

- Fix avatars placed under another object in the scene (an "Avatars" folder object) losing their armature and meshes on
  version apply or reset: the avatar root is now the object with the avatar descriptor, never the top of the scene.
- Fix switching from one FBX version to another leaving models the first one patched and the next one does not (a head
  FBX patched by A only kept A's bytes, and a later reset missed it): those models get their original bytes and import
  settings back first.
- Reset puts back each model's own import settings, kept (as `<model>.fbx.originalimport~`) before a version first gave
  it its own Avatar. Models the version never touched (hair, props, generic models) are left as they are; before, every
  base FBX was switched to the version folder's humanoid Avatar.
- Fix HDiff versions failing to apply, and creators' patches falling back to full XOR files, when the project or avatar
  folder has non-ASCII characters (Japanese Booth folders, accented user names): paths reach HDiff as UTF-8.
- Original-base backups are written to a temporary file, verified and then moved into place; a reset refuses a backup
  whose bytes differ from the ones recorded when it was made, and a damaged cached original key is replaced. When the
  base package was re-imported while a version was applied, the newly imported original replaces the stale backup (the
  old one is kept aside) instead of being overwritten by it.
- Fix "default avatar.asset" not built for a version when the creator's FBX had an original-base backup.
- Applied advanced versions stay recognised after a Unity editor upgrade (their shared meshes sit in a folder named
  after the editor version they were made with).
- **Delete local files** refuses while the version is applied or its files are used (scenes, assets, a model's Avatar).
- Plain advanced versions made of several FBX models reset every model their renderers come from, not only the first.
- A replaced renderer the user deleted or renamed no longer blocks every reset or switch of an XOR advanced version: the
  others are restored and a "Renderers skipped" warning names it (as plain versions already did).
- Creators can no longer build an XOR advanced version while their base FBX is not its original: reset it first.
- Reset and **Clear cache** remove the generated advanced meshes and humanoid Avatars nothing uses any more (decrypted
  meshes no longer stay in the project); those a version switch's Undo still needs are kept.
- Undoing a version switch older than the last five, whose files MCB no longer keeps, leaves the files as they are and
  says so instead of silently restoring only the scene.
- Custom veins removal only takes off MCB's own veins texture and the keywords and toggles MCB switched on: a detail
  normal map, `_NORMALMAP` or `USE_NORMAL_MAPS` set by the user or My Avatar stays.
- Version switches wait while a refit runs on the avatar (My Avatar or the ReFit panel). A fit taken back in any tool
  stays taken back after switching versions, and only fits made for a version are saved with it.
- Mode locks run before the clothing links at upload, so refitted clothing, Follow Body Blendshapes and accessories take
  the locked values too; a mode that overrides animations a later build step added (face tracking) logs a warning.
- Version downloads interrupted by a script reload no longer leave their files in the system temp folder.
- The version list is read from the cache while a version is applied (it was always fetched again, and empty offline).
- Version compare checks the original model before decoding and the decoded model against its hash.
- The MCB build step also finds the custom base component on a child of the avatar. The live animator controller no
  longer picks My Avatar's face tracking test copy.
- Requires Orbiters Toolkit 0.3.18.

## 1.12.3 — 2026-10-07

- Fix legs folded inward in VRChat on versions exported from a posed armature (Ultirex 5.1, whose model has its legs
  spread 26° in a star pose): the avatar's humanoid definition now rests at the pose its meshes are bound in, which is
  where applying the version puts the limbs, instead of the model's exported pose. Partial meshes (feathers) no longer
  decide it: the payload whose meshes weigh on the most humanoid bones defines the avatar. Avatars get the corrected
  definition at their next upload or version apply.
- Fix eyes that move the wrong blendshapes in VRChat after a version changed the face mesh: the avatar descriptor's
  eyelid blendshapes (blink, looking up, looking down) are indices into the mesh, and Ultirex's mesh has "Throat" and
  "TailSkinny" where Rexouium had "LookUp" and "LookDown". Applying or resetting a version now keeps them on the same
  shapes by name (a shape the new mesh lacks is turned off). Avatars that already have a version applied: apply it again.
- Fix twisted feet in VRChat on versions whose model rolled bones the base's animations rotate (Ultirex 5.1: its toes
  rest up to 180° turned from Rexouium's, so the toe curl of Rexouium and of the Ultirex logic turned the paws over).
  At upload, rotation animations written for the original base model (the base FBX with the avatar's skeleton) turn
  each bone the same way from its rest pose on the version's skeleton. Animations made for the version stay as they are.
- Fix a prop's model taken for the avatar's base model: Adjerry91's face tracking template adds a debug panel with its own
  FBX, and the detected base FBX files listed it first, so versions could treat it as the model they apply to and the
  upload stripped its blendshapes. Detection now lists the model with the avatar's skeleton first (the FBX with the most
  of the avatar's bones, then the one its meshes are skinned to) and leaves out model files with none of them (bone-less
  panels, static props). Avatars made of several models keep every model that carries the skeleton.
- Physic and Squishy chains are tuned per version: a version can store its PhysBone pull, momentum (spring), stiffness,
  gravity, immobile and angle limits (`physicSettings`, `squishySettings` in its customization). Versions that store
  none keep the values used so far.
- The About window is now the Toolkit's shared one (requires Orbiters Toolkit 0.3.17).

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
  alone; **Textured** uses the avatar's own materials, each on the submesh of its slot name (`MaterialSlotNames`), whatever
  submesh order each model has. The clothes button adds the avatar's other renderers.
- It compares the version with **the version before it** in its history (the parent its creator chose, else the highest
  published version below it for the same original), or with the **original** base for its first version. **Original**
  and **your avatar now** are offered too only when they show other meshes: the avatar now when it has another version or
  customization. Parts are shown with the renderer's blendshape values, and each version's new blendshapes at the value
  its creator chose.
- The list of meshes that change flies the camera to each change; blendshapes added, reshaped or removed are listed.
- **Apply** goes through the usual confirmation. The versions it reads are downloaded first when needed; when the
  version before cannot be downloaded or read, the original takes its place and the window says why.

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

Correctives baked with XMuscles in Blender (XMuscle Orbit Helper) come with each Blender export (`manifest.xmuscle`)
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
