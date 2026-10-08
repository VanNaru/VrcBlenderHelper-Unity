# VRC Blender Helper (Unity)

Unity companion for the **VRC Avatar & World Helper** Blender add-on
([VanNaru/VrcBlenderHelper](https://github.com/VanNaru/VrcBlenderHelper)).

The add-on's **Export for VRChat** button writes `<Avatar>.fbx` plus an
`<Avatar>.vrchelper.json` sidecar. This package reads that sidecar and sets up
your avatar for you:

- **VRCPhysBone** on every bone you marked as a PhysBone root, with your settings
- **VRCContactSender / VRCContactReceiver** on every `Contact_*` object
- **View Position** on the VRC Avatar Descriptor (from the eye bones)

## Install (ALCOM / VCC)

1. Add this repository listing:
   `https://vannaru.github.io/VrcBlenderHelper-Unity/index.json`
   - ALCOM: **Settings → Packages → Add Repository**
   - VCC: **Settings → Packages → Add Repository**
2. Open your avatar project → **Manage** → add **VRC Blender Helper**.

Requires the VRChat Avatars SDK 3.10.0 or newer.

## Use

1. Put the exported `.fbx` **and** its `.vrchelper.json` in the same folder in `Assets/`.
2. Drag the model into the scene and add a VRC Avatar Descriptor (for View Position).
3. Select the avatar root → **Tools → VRC Blender Helper → Apply Setup to Selected Avatar**.

Re-running after a new export updates the existing components instead of
duplicating them, and the whole apply is a single Undo step.

## Releasing (maintainers)

Built on VRChat's [template-package](https://github.com/vrchat-community/template-package).

One-time GitHub setup:
- **Settings → Secrets and variables → Actions → Variables**: add repository
  variable `PACKAGE_NAME` = `com.vannaru.vrc-blender-helper`
- **Settings → Pages → Build and deployment → Source**: **GitHub Actions**

To release: bump `version` in `Packages/com.vannaru.vrc-blender-helper/package.json`,
push, then run the **Build Release** workflow. The **Build Repo Listing**
workflow then republishes the VPM listing automatically.
