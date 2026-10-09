# World-side field verification (pending)

`ApplyWorld()` in `Packages/com.vannaru.vrc-blender-helper/Editor/VrcBlenderHelperSetup.cs`
was written without the VRChat Worlds SDK installed anywhere in that session,
so the serialized field names below are best-effort guesses, unlike the
avatar-side fields (`VRCPhysBone`, `VRCContactSender/Receiver`,
`VRCAvatarDescriptor`), which were verified against SDK 3.10.4 by inspecting
the actual DLL. Run this check on a machine with the Worlds SDK installed via
ALCOM/VCC before relying on the world setup flow.

## How to verify

1. Open a world project with the VRChat Worlds SDK installed.
2. Add a `VRCSceneDescriptor` and a `VRCStation` to the scene (or use
   existing ones).
3. Select the `VRCStation` GameObject in the Hierarchy, then in the
   Inspector's **⋮ (context) menu → Debug** mode (or right-click the
   component header → "Edit Script" is not it -- use the **⋮ → Debug**
   toggle, or `Reflection` any inspector extension you have). Debug mode
   shows the component's actual serialized field names, not just their
   display labels.
4. Compare against the names this script writes (see table below).
5. Alternatively: run **Tools → VRC Blender Helper → Apply Setup to
   Selected World Root** against a real `.vrchelper.json` and read the
   Console. Each wrong field name logs exactly one line:
   `[VRC Blender Helper] Station '<name>': SDK field '<field>' not found
   (SDK changed?) -- skipped.` -- that is the authoritative signal of what
   to fix. Nothing crashes; a wrong name just means that one setting didn't
   get applied.

## Field names currently written

| C# constant / call site | Serialized field name used | Confidence | Unity Inspector label (expected) |
|---|---|---|---|
| `SceneDescriptorType` | `VRC.SDK3.Components.VRCSceneDescriptor` | Medium | (type name) |
| `ApplySpawns` → `Prop(so, "spawns", ...)` | `spawns` | Medium-high (matches common Udon references to `VRCSceneDescriptor.spawns`) | "Spawns" list |
| `StationType` | `VRC.SDK3.Components.VRCStation` | Medium | (type name) |
| `ApplyStations` → `SetBool(so, "seated", ...)` | `seated` | **Low** -- not confident a plain "Seated" toggle exists on `VRCStation` at all; seating may be implicit rather than a separate field | "Seated" (if it exists) |
| `ApplyStations` → `SetEnum(so, "playerMobility", ...)` | `playerMobility` | Medium | "Player Mobility" (Mobile / Immobilize / Immobilize For Vehicle) |
| `ApplyStations` → `SetBool(so, "disableStationExitCollider", ...)` | `disableStationExitCollider` | Medium | "Disable Station Exit Collider" |
| `ApplyStations` → `SetBool(so, "canUseStationFromStation", ...)` | `canUseStationFromStation` | Medium | "Can Use Station From Station" |

Not modeled at all (out of scope, set manually in Unity if needed):
`Station Enter Player Location`, `Station Exit Player Location` (separate
Transform references, better wired up once the avatar/world's final
hierarchy is in the scene).

## If a name is wrong

Fix the string literal in the matching `SetBool`/`SetEnum`/`Prop` call inside
`ApplyStations` or `ApplySpawns` (both in `VrcBlenderHelperSetup.cs`) to the
real serialized field name from the Debug Inspector, then re-run. No other
changes needed -- the Blender-side JSON shape (`core/unity_bridge.py`'s
`build_world_setup()`) does not need to change for a field *rename*, only if
VRChat adds/removes a setting entirely.

## If `seated` turns out not to exist

If Debug mode shows no such field on `VRCStation`, that setting in
`core/stations.py` (Blender) and `StationEntry.seated` (this sidecar) is
dead weight -- either find the real mechanism VRChat uses for seated vs.
standing stations and point `SetBool`/`SetEnum` at that instead, or drop
`seated` from both sides of the bridge.
