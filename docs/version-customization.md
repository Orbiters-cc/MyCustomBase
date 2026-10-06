---
title: Configure MCB Version Customization
section: How To
order: 90
audience: creator, dev
stage: alpha
id: orbiters.mcb.version-customization
domain: mcb
type: how-to
owner: orbiters-mcb
lastVerified: 2026-10-06
relations: orbiters.mcb.original-base-versions, orbiters.tools.mcb-operating-contract
---

# Configure MCB Version Customization

**Create MCB Version** groups version settings in cards: supported original bases,
material slots, dynamic normals, modes, twisting bones, physic and squishy interaction. Each card summarizes its
state and expands only when you edit it. Settings are saved with the built version.
Protection and Discord role access belong to the custom base asset.

**Release status:** local development implementation. These package and backend
changes and the Ultirex port have not been published or deployed. This guide does
not announce a release.

## Protection and access

Protection is set on the custom base asset, and every version follows it. Trusted
creators and Orbiters administrators see **Protection and access** in **Create
custom base**, and in the asset's **Edit** panel (saved immediately):

- **Verify a Discord role**: only members holding one of the asset's Discord role
  rules can download its versions. The owner and administrators can always download
  them. A version cannot be published until the asset has at least one rule.
- **Protect with XOR encryption**: on by default. Each supported original receives
  its own copy, encrypted with that original model, so only owners of the original
  can use it. You can turn it off only while **Verify a Discord role** is on.

With XOR protection off, MCB builds one unencrypted package for every original.
It applies to any avatar with the version's skeleton, including avatars that
already use another custom base built on the same original. Users download and
apply it exactly like an encrypted version. **Supported original bases** and
**Support new version** are hidden: there is no per-original copy to choose.

Unencrypted versions need advanced mesh replacement. MCB stores very large meshes
in one codec (ZSTD) so a package stays below the 600 MB upload limit.

The Discord role is the only download check for an unencrypted version: choose an
ownership role that proves the member owns the original base.

If you change the asset's protection, rebuild unpublished versions: publishing a
version built with the previous protection is refused.

### Discord role access

A rule gives an access level to members who hold an **ownership role** in a Discord
server, and gives them the **destination role** in a server you manage. These are
the same rules as the asset's Access tab on Orbiters. Add them under **Discord role
access** in **Create custom base** (created right after the asset) or in the asset's
**Edit** panel (saved immediately).

Server and role lists are searchable. Only named roles appear: name a server's roles
on Orbiters from the asset's Access tab (**Missing a role?**). The destination
server needs a connected bot with Manage Roles.

A member without the required role sees **Requires the … Discord role** instead of
the download button.

## Supported original bases

With XOR protection, the card lists the originals registered for the asset. Select
**Choose** to search and select them; each receives its own encrypted copy, and your
selection is reused for the next version.

Without XOR protection, avatars need the bones that every registered original
shares. Bones only some originals have, such as whiskers or a reparented chest bone,
are created when the version is applied. Accessory models registered as originals
(for example head feathers only) do not narrow this skeleton.

## Material slots

Unity keeps materials by submesh index. MCB matches them by the material slot names
the model files declare. Each custom slot takes the avatar's material from the
original slot of the same name, whatever piece layout the user's original has.
Blender's `.001` duplicate suffixes are ignored.

The card is folded by default and summarizes the matched, changed and hidden slots;
select its header to open it. It lists every custom renderer and slot. Change a slot's original slot when
the custom model's slot name is misleading; changed slots are highlighted. **Match
by names** clears your choices. For example, the Ultirex Body slot named `BodyMatt`
holds the claws and teeth, so it maps to `MiscMatt`.

- **Hidden original pieces**: renderers of the user's original model that the custom
  model replaces, such as separate claws or eye meshes of an all-pieces original.
  Applying hides them; resetting shows them again. Only renderers of the avatar's
  original model are hidden, never clothing. **Hide pieces the custom model
  replaces** adds the original's pieces missing from the custom model. Type piece
  names from other original versions and press Enter.
- **Fallback materials**: materials for slots an original lacks, such as reduced
  Quest models. Add them to the logic prefab's dependencies.

Custom renderers missing from the user's avatar are created next to its original
pieces. Resetting or switching versions restores the original materials and carries
materials the user changed on custom slots back to the original slots of the same
name.

## Modes

Modes let users switch parts of the version in MCB: a genre, dog ears, a body
modification. Modes belong to categories:

- **Pick one** categories, such as **Genre**, hold exactly one active mode. Mark the
  one new users receive with **Make default**.
- **Combine** categories, such as **Body modification**, let users turn each mode on
  or off. **On by default** decides the state new users receive.

Select **Add a genre**, **Add a body modification** or **Add a category**, then **Add
mode** in a category. Each mode has three lists:

- **Blendshapes**: select **Add blendshapes**, choose a mesh, search and select any
  number of its blendshapes, then **Add selected at 100**. Adjust each value with its
  slider or field. A blendshape any mode sets returns to 0 unless an active mode sets
  it.
- **Objects**: drop an object from the avatar or the logic prefab on **Add an object**
  and choose **Enabled** or **Disabled**. Logic objects use the installed
  `mcb logic/` prefix. Objects keep their original state unless an active mode sets
  them.
- **Animations**: drop an animation clip on **Add an animation**, such as the toggle
  animation the original avatar used. Its first frame applies while the mode is on:
  bone poses, blendshapes and objects. The clip ships with the version.

For example, Ultirex has a **Genre** category (Male, Female) whose Female mode sets
`Body` / `ulti female` to `100` and enables `mcb logic/Dynamics/BreastPhysics_female`,
and a **Body modification** category whose **Dog ears** mode plays the ear pose
animation of the original Floppy Ears toggle.

Users switch modes in the installed version's **Modes** card: a tile per mode, a
check on the active genre and a switch on combinable modes. The avatar changes as
soon as they select a tile. Choices are remembered per asset across version changes:
modes keep their state when the next version has a mode with the same **Stable ID**,
a missing genre falls back to the default, and new modes start with their default.
Keep IDs stable when only changing a label.

Modes are fixed in the built avatar. MCB removes competing animation curves for the
blendshapes and objects active modes set (and everything a **Pick one** category sets)
and adds a final constant override on the build copy. Bone poses from animations stay
in the avatar's scene pose, so the avatar's own animations of those bones keep
working. Modes are not in-game toggles.

## Dynamic normals

Select **Set up dynamic normals** to choose, for each custom mesh, the blendshapes
that recalculate their normals. The searchable picker is the one ReFit uses.
**Flexings** and **Muscles** select names containing `flex` or `muscle`; they only
add to the explicit selection. Shapes such as `ulti female` or `belly suck` can be
selected without naming rules. Missing selected shapes fail validation.

## Twisting bones

Select **Set twisting bones** to open the custom model as a ghost with its skeleton.
Click bones on the model or in the **Bones** list; **Weighted only** limits the
list to bones that deform a mesh, and the search finds any bone. **Symmetry**
selects the mirrored bone too. Drag to turn and scroll to zoom.

Each selected bone needs the bone it aims at and an up reference. A bone with exactly
one child aims at it; other bones need an explicit choice. Expand a bone to pick
them, choose the **Ultirex**, **Linear** or a **Custom** weight curve, and change the
up direction. **Confirm** saves; **Cancel** leaves the version unchanged.

Twists are generated only on the upload/play copy, after VRCFury merges clothing
armatures. Every skin weighted to the bone is processed, including clothing; skins
that only list the bone keep their mesh. Each bone adds a skinned bone, an up helper
and a VRC aim constraint.

Bone paths are matched first. Originals that reparent a named joint resolve it
through one unique skinned bone of that name; ambiguous matches fail.

A version may move bones to another parent, as Ultirex puts each ankle under a
`TopFut` bone. The avatar's own animations of those bones keep working: the upload
and play copy's controllers follow the moved bones.

## Physic

Bones whose name contains `physic` (any case) are secondary-motion chains: muscles or
soft parts that react to the avatar's movement, such as `Left triceps physic` or
`Left ass physic base` with its `Left ass physic tip`. Every bone of a chain needs the
word, its tip included. Select **Support physic** to let users of the version turn
them on; the card lists the chains found in the custom models.

Users switch **Physic** in the version options; it is off by default. When on, the
build adds PhysBones to the chains. Chains that share a parent bone share one
PhysBone rooted at that parent, which stays still. These PhysBones are always enabled
and depend on no synced parameter, so every player in the world simulates them. When
off, the build removes the chains' bones and moves their weights to the nearest
remaining parent, so the avatar carries no extra bones. A bone that another component
uses, such as a contact, a constraint or a PhysBone of the logic, is kept.

Each switch shows how many PhysBones it adds or how many bones it removes. XRay Gizmos
0.2.8 shows the whole avatar against VRChat's PC limits in its **Avatar budget** panel
over the Scene view, with the custom base's share apart from the avatar's. Players who
hide Very Poor avatars see none of an avatar's PhysBones, colliders and contacts: keep
PhysBones and contacts within 32 each for the motion to reach them.

## Squishy interaction

Bones whose name contains `interaction` (any case) are squishy chains of two bones, a
base and a tip, such as `Left thigh interaction base` and `Left thigh interaction tip`:
soft parts players can press. A bone named with both `interaction` and `physic` is
squishy. Select **Support squishy** to let users of the version turn them on; the card
lists the chains found in the custom models.

Users switch **Squishy** in the version options; it is off by default, independent of
**Physic**. When on, the build adds PhysBones that collide with players' hands: a touch
squashes the chain (PhysBone squish) and it springs back, without stretching. Grabbing
and posing are off. As for physic, chains that share a parent share one always-enabled
PhysBone that depends on no synced parameter. When off, the build removes the bones and
moves their weights to the nearest remaining parent.

## Unused blendshapes

Uploads leave out the custom base's blendshapes that nothing uses: a shape stays when
an animation of the avatar drives it (sliders, modes, correctives, gestures, clothing
links), when the avatar descriptor uses it (visemes, jaw flap, eyelids), or when it is
a standard MMD morph on `Body`, which dance worlds animate by name. A removed shape that
is set to a weight is baked into the mesh, so the avatar looks the same. Clothing keeps
all its shapes, and play mode keeps every shape.

A sculpted body carries most of its size in blendshapes: Ultirex's 380 unused shapes
took 448 MB and put it over VRChat's 500 MB uncompressed limit. Without them, it
uploads at 266 MB (106 MB to download).

## Reuse an existing store asset

A custom base version belongs to an Orbiters asset. If a listing already exists
with Gumroad or Jinxxy integration, reuse that asset ID instead of creating a second
listing. Link its avatar base as well as its original source files.

## Local preview and publishing

**See differences** reads a complete local build directly. An incomplete local build
asks you to rebuild; it does not request an unpublished download.

Publishing checks the immutable build. If a source file such as the logic prefab
changed after the build, MCB names it and offers to publish the stored build or
cancel; rebuild to include the change. Encrypted versions with several originals
upload each original as a separate package through the source-support endpoint;
an unencrypted version is one package.

<audience include="dev">

## MCP authoring

With MCP for Unity connected, use `mcb_authoring`. It uses the current MCB login;
requests and responses never contain a token.

| Action | Result |
| --- | --- |
| `inspect` | Scene target IDs, local version identities and draft/registration schemas. |
| `create_base` | Register a new custom base, or set `data.existingAssetId` to link original support to an owned listing. |
| `import_fbx` | Import an FBX into a new `Assets/` path; differing existing content is rejected. |
| `configure` | Save a typed draft: asset, source/custom models, logic prefab, version metadata, exposed shapes, customization (`physic` included), `customVeins` (a texture path) and `suggestRealistic` (renderer paths). |
| `build` | Build the saved draft into a local artifact; returns its identity, protection and required skeleton size. |
| `apply` / `reset` | Apply the exact local version identity to the target avatar, or reset it. |
| `set_mode` | Turn an installed mode on (`enabled`, default true) or off by stable ID; a **Pick one** category switches by turning another mode on. |
| `set_asset_protection` | Set an owned asset's protection: `data` `{ "assetId": 7, "protection": { "xor": false, "discordRole": true } }`. |
| `preview_publish` | Validate and fingerprint an artifact; return its files and a confirmation code. |
| `confirm_publish` | Publish the unchanged artifact after explicit user approval of the preview. |
| `status` | Poll the job ID of a pending operation. |

`build` always uses the saved draft, even when an open creator form loaded another
version meanwhile. Builds follow the protection of the draft's asset. A trusted
creator sets it with `set_asset_protection` or `create_base` (`protection` in the
registration); the server rejects it for other creators, and rejects unencrypted or
Discord-verified uploads while the asset has no Discord role rule.

Build, apply, reset, import and registration return pending jobs. Keep polling the
same job after a client timeout. A Unity domain reload clears in-memory jobs, so
inspect local artifacts before retrying. Confirmation codes expire after 15 minutes,
are single-use, and are invalidated when the artifact changes. Obtain human approval
before `confirm_publish`.

Typed `extraCustomization` entries are `modes` (`categories` with `exclusive`;
`options` with `category`, `default`, `blendshapes`, `gameObjects` and `animations`
as clip GUIDs), `twistBones`,
`dynamicNormalBlendshapes`, `rendererLayout` (`renderers` with slot names per
submesh, `hide`, `fallbacks` with material GUIDs) and the `physic` flag. Unknown sibling entries survive
serialization. Assets carry `protection`; version metadata records the `protection` it was built with and, for unencrypted versions,
`skeleton` (required bone names); the server lists `discordRoleGranted` and
`discordRoles` for the signed-in member, skips the original-model hash check for
unencrypted downloads, and matches them in discovery by the avatar's transform names.

The twist weight-distribution implementation derives from Haï's MIT-licensed
Prefabulous Universal; the package includes its license and attribution.

</audience>
