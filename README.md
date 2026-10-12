# VRC Blender Helper (Unity)

Unity companion for the **VRC Avatar & World Helper** Blender add-on
([VanNaru/VrcBlenderHelper](https://github.com/VanNaru/VrcBlenderHelper)).

The add-on's **Export for VRChat** button writes `<name>.fbx` plus an
`<name>.vrchelper.json` sidecar -- for an avatar or a world. This package
reads that sidecar and sets things up for you.

Avatar sidecar:
- **VRCPhysBone** on every bone you marked as a PhysBone root, with your settings
- **VRCContactSender / VRCContactReceiver** on every `Contact_*` object
- **View Position** on the VRC Avatar Descriptor (from the eye bones)

World sidecar:
- **Spawns** on the scene's VRC Scene Descriptor, from your `Spawn_*` markers
- A **Light Probe Group**, from your `LightProbe_*` marker positions
- **VRCStation** on every `Station_*` object, with your settings
- A **Reflection Probe**, on every `ReflectionProbe_*` object, with your settings
- A **BoxCollider** on every `TeleportTarget_*`/`TeleportTrigger_*` object, sized
  and trigger-configured from Blender -- this prepares the scene geometry only;
  the Console tells you the exact `TeleportTo(...)` call to wire up yourself in
  an UdonBehaviour, since this package doesn't author Udon/UdonSharp scripts

## Install (ALCOM / VCC)

1. Add this repository listing:
   `https://vannaru.github.io/VrcBlenderHelper-Unity/index.json`
   - ALCOM: **Settings → Packages → Add Repository**
   - VCC: **Settings → Packages → Add Repository**
2. Open your project → **Manage** → add **VRC Blender Helper**.

Requires the VRChat Avatars SDK 3.10.0+ for the avatar side, or the VRChat
Worlds SDK for the world side.

## Use

1. Put the exported `.fbx` **and** its `.vrchelper.json` in the same folder in `Assets/`
   (the Blender add-on's Preferences has a "Unity Project Assets Path" field that
   makes Export for VRChat default into `Assets/VRChat/Avatars/<name>/` or
   `.../VRChat/Worlds/<name>/` automatically, so every export's files land
   together without picking a folder each time).
2. **Automatic setup**: importing (or re-importing) that `.fbx` creates/updates
   a same-named `<name>.prefab` right next to it, already carrying everything
   below except Spawns -- PhysBones, Contacts, View Position*, Stations,
   Reflection Probes, and Teleporter colliders. Drag **that Prefab** (not the
   raw `.fbx`) into your scene.
   - \* View Position only applies once a VRC Avatar Descriptor exists on the
     Prefab -- add one, then re-import (or run step 3) once to pick it up.
3. Still manual either way:
   - Avatar: add a VRC Avatar Descriptor if you haven't, then re-import or
     run **Tools → VRC Blender Helper → Apply Setup to Selected Avatar** to
     pick up View Position.
   - World: make sure a VRC Scene Descriptor exists somewhere in the scene,
     then run **Tools → VRC Blender Helper → Apply Setup to Selected World
     Root** against the dragged-in Prefab instance for Spawns -- this is the
     one piece that genuinely can't happen at import time, since it needs a
     Scene Descriptor that only exists once the world is actually placed in
     a scene.

Re-running (either automatically on reimport, or via the menu items) updates
the existing components in place instead of duplicating them, and the whole
apply is a single Undo step.

> The world-side VRCStation/VRCSceneDescriptor field names were written
> without an installed Worlds SDK to verify them against (unlike the
> avatar side, checked against SDK 3.10.4). If applying a world setup logs
> "SDK field 'x' not found" warnings, check the real component's Debug
> Inspector for the correct serialized name and fix it in
> `VrcBlenderHelperSetup.cs`.

## Releasing (maintainers)

Built on VRChat's [template-package](https://github.com/vrchat-community/template-package).

One-time GitHub setup:
- **Settings → Secrets and variables → Actions → Variables**: add repository
  variable `PACKAGE_NAME` = `com.vannaru.vrc-blender-helper`
- **Settings → Pages → Build and deployment → Source**: **GitHub Actions**

To release: bump `version` in `Packages/com.vannaru.vrc-blender-helper/package.json`,
push, then run the **Build Release** workflow. The **Build Repo Listing**
workflow then republishes the VPM listing automatically.
