# JointKeeper User Manual

Version: v0.1.0  
Requirements: Unity 2022.3 / VRChat SDK 3.7.0 or later / NDMF

---

## 1. Overview

JointKeeper is an NDMF-compatible Unity editor extension for VRChat avatars. It reduces elbow and knee collapse when bending, as well as sleeve twisting collapse when rolling the forearm.

- **Elbow & Knee Volume Preservation**
  Reduces inward collapse and body clipping on the outer side of elbows and knees when bent. On build, it creates helper bones driven by VRC Parent Constraints and redistributes weights around the joint.
- **Forearm Twist Distribution**
  Reduces abrupt twisting and pinching at the elbow when the forearm rolls. It adds helper bones driven by VRC Aim and VRC Rotation Constraints to spread the roll from the elbow to the wrist. Disabled by default.
- **Non-Destructive Build and Bake Option**
  Normally runs non-destructively via NDMF without altering original assets. A reversible Bake & Replace option is also available when you need to avoid conflicts with other tools.

---

## 2. Quick Start

1. In the Hierarchy, select an outfit root, an outfit mesh, or the avatar root.
2. Right-click and select `dennokoworks` → `JointKeeper`.
   - You can also add it via `Add Component` → `dennokoworks` → `Joint Keeper`.
   - The Inspector starts collapsed when added.
3. Enter Play Mode or upload your avatar. NDMF applies elbow and knee correction during the build.

### Previewing in Edit Mode

1. Expand the `JointKeeper` component.
2. Enable `Edit Mode Bend Preview`.
3. Move the `Bend Angle` slider to check the result in Scene View. Scene bones and original meshes are not modified.
4. If the body still pokes through slightly, increase `Push at 90°` to around `0.003`–`0.006` m.

---

## 3. Features

### 3.1 Elbows & Knees

| Setting | Default | Description |
| --- | --- | --- |
| Left / Right | On | Enables or disables correction for each side. |
| Strength | `0.85` | Amount of weight shifted to the helper bone. `0` disables the effect and `1` is the maximum. |
| Push at 90° | `0.002` m | Outward push distance at a 90° bend. Increase if body clipping remains. |

### 3.2 Forearm Twist Distribution

Elbow-only feature, disabled by default. Can be used together with elbow volume helper bones.

| Setting | Default | Description |
| --- | --- | --- |
| Enable Toggle | Off | Turns forearm twist distribution on or off. |
| Twist Follow | `1.0` | How much the wrist end follows forearm roll. The elbow end is always `0`. At `1.0`, wrist motion stays the same while elbow twist is distributed along the forearm. Lower values reduce roll across the sleeve. |

Intended for sleeves with no weights beyond the wrist, such as long sleeves, 3/4 sleeves, and arm covers. For gloves, sleeves covering the back of the hand, or base body meshes, keep `Twist Follow` at `1.0` to avoid a seam at the wrist.

### 3.3 Edit Mode Bend Preview

- **Bend Angle**: Previews elbow and knee bending from `0°` to `150°`.
- **Forearm Twist**: Previews forearm roll from `-90°` to `+90°`.
- **Compare Before**: Displays the original mesh at the same bend angle for comparison.
- **Heatmap View**:
  - `Off`: Standard view.
  - `HelperWeight`: Color-codes helper bone weight distribution.
  - `FourBoneDiagnostics`: Color-codes adjustments made for VRChat's 4-bone-per-vertex limit.
- **Reset**: Resets bend angle, twist angle, and Before comparison to `0°`.

### 3.4 Scene View Guides

When the component is selected, each active joint shows the following guides:

- **Cyan ellipsoid**: Influence range
- **Green circle**: Boundary where falloff begins
- **Orange arrow**: Outward direction of the joint
- **Red line**: Bend axis

### 3.5 Destructive Bake

Generates helper bones and `.asset` meshes directly in your project and swaps them onto the renderers. Mesh `.asset` files are saved in the same folder as the original mesh.

- **Bake & Replace**: Creates helper bones and baked meshes, swaps them in, and disables the component so NDMF builds will not apply it twice.
- **Re-Bake**: Reverts the previous bake and bakes again with current settings.
- **Revert**: Restores original meshes and bones, and deletes generated helper bones and `.asset` files.
- **Finalize & Remove Component**: Removes `JointKeeper` while keeping baked meshes and helper bones. `Revert` is no longer available afterward.
- **Generated Assets & Helper Bones**: Lists created mesh assets and helper bones.

Forearm Twist Distribution is not supported by Bake & Replace and is applied only during NDMF builds.

### 3.6 Language Switch and Version Check

The Inspector header includes a Japanese/English language toggle and a version check. Click `↻` to recheck for updates.

---

## 4. Advanced Features

Expand `Advanced` in the Inspector to adjust detailed settings.

### 4.1 Target Mesh Settings

- **Detection Mode**:
  - `AutoDetect`: Targets child `SkinnedMeshRenderer` components automatically.
  - `ManualList`: Targets only meshes listed in `Target Renderers`.
- **Excluded Renderers**: Meshes to skip in `AutoDetect` mode, such as base body meshes when placed on the avatar root.

### 4.2 Unlink Left / Right

Off by default, mirroring left-side settings to the right side. Enable to configure asymmetric left and right outfits separately.

### 4.3 Joint Rig & Influence Range Settings

| Setting | Default | Description |
| --- | --- | --- |
| Bend Follow Ratio | `0.50` | How much the helper bone follows lower-limb rotation. `0.50` is halfway between upper and lower limbs. |
| Blend Curve Power | `1.2` | Steepness of weight falloff away from the joint center. Higher values keep the effect closer to the crease. |
| Pivot Offset | `0, 0, 0` m | Adjusts the center of the helper bone and influence range. |
| Outward Angle Offset | `0°` | Rotates the orange arrow direction around the limb axis. |
| Axial Radius | `0.35` | Influence range along the limb length. |
| Radial Radius | `0.45` | Influence range across the limb width. Increase for loose sleeves or wide pants. |
| Inner Falloff Ratio | `0.25` | Distance where falloff starts toward the edge. Matches the green circle. |
| Inner Cutoff | `-0.15` | Threshold where correction drops to minimum on the inner side of the joint. Higher values keep the effect closer to the outer side. |
| Outer Full Threshold | `0.35` | Threshold where correction reaches full strength on the outer side. |
| Inner Min Retention | `0.0` | Minimum correction kept on the inner side of the bend. Usually kept at `0`. |
| Boundary Weight Threshold | `0.01` | Minimum weight required on both upper and lower bone groups for a vertex to be processed. |

- **Show Scene View Handles**: Enables drag handles in Scene View for pivot position and outward direction.
- **Recalculate Joint Frames**: Re-measures actual bend and forearm roll axes from the avatar's Humanoid rig.

### 4.4 Forearm Twist Distribution Settings

| Setting | Default | Description |
| --- | --- | --- |
| Segments | `3` | Number of segments to divide forearm roll into, from `1` to `4`. Use `2` to reduce constraint count. |
| Start Position / End Position | `0.0` / `1.0` | Range along the forearm where roll is distributed. `0` is the elbow and `1` is the wrist. |
| Distribution Curve / Curve Power | `Linear` / `1.0` | How roll is distributed: `Linear` is uniform, `Power` shifts twist toward the wrist, and `SmoothStep` eases both ends. |
| Bend-Only Elbow Helper | On | Keeps the elbow volume helper from picking up forearm roll. |
| Apply Even With Existing Twist Rig | Off | Applies even if the mesh already uses constraint-driven forearm twist bones. Existing twist bone weights are unchanged. |
| Knot Snap Threshold | `0.15` | Snaps vertices near segment boundaries to a single bone to save weight slots. |

### 4.5 Twist Bone Classification, 4-Bone Limit & Diagnostics

- **Auto-Detect Twist Bones**: Classifies existing twist and helper bones into upper and lower groups from hierarchy and constraint relationships.
- **Distal Source Mode / Override**: Changes how the lower-limb reference bone is selected. Normally left at default.
- **Extra Proximal / Extra Distal / Excluded Bones**: Manually adds unclassified bones or excludes dangling bones.
- **4-Bone Tier 2 Snap Threshold / Tier 3 Noise Threshold**: Thresholds for consolidating minor weights when a vertex reaches VRChat's 4-bone limit.
- **Twist Bone Detection & Diagnostics**: Shows how bones are classified and whether the forearm roll axis was measured.

---

## 5. Model & Rig Caveats

1. **Humanoid Rig Required**
   A valid Humanoid Animator is required in the parent hierarchy. For Forearm Twist Distribution, a `Hand` bone must be assigned and `LowerArm` must rotate when the Humanoid forearm twist muscle moves.
2. **Boundary Weight Distribution**
   Elbow and knee volume preservation targets vertices inside the influence range with weights of at least `0.01` on both upper and lower bone groups. Rigid parts split cleanly at the joint or meshes with no vertices near elbows or knees are skipped automatically.
3. **Existing Twist Bones**
   Existing constraint-driven twist bones are detected automatically. Forearm Twist Distribution skips meshes already weighted to existing forearm twist bones to prevent double-twisting.
4. **Meshes Weighted Beyond the Wrist**
   For gloves, sleeves covering the back of the hand, or base body meshes, setting `Twist Follow` below `1.0` creates a twist step at the wrist. Keep `Twist Follow` at `1.0`.
5. **PhysBones on the Forearm**
   PhysBone chains on the forearm continue following the original bone roll and may drift slightly from surrounding sleeve vertices when Forearm Twist Distribution is enabled.
6. **Modular Avatar Compatibility**
   Supports `MA Merge Armature`, `MA Scale Adjuster`, and Blender 100x-scale outfit armatures. If you change `MA Scale Adjuster` values after running destructive Bake & Replace, click `Re-Bake`.

---

## 6. Q&A

### Q. How many bones and constraints are added, and when do they increase?

**Elbow & Knee Volume Preservation**
- Adds 1 helper bone and 1 VRC Parent Constraint per enabled joint.
- Meshes and outfits sharing identical rig settings on the same joint reuse the same helper bone. With default settings across multiple outfits, at most 4 bones and 4 constraints are added.
- Additional helper bones are created only when components use different rig settings such as push distance, follow ratio, pivot offset, or outward angle on the same joint.
- No bones are added for joints with no affected boundary vertices.

**Forearm Twist Distribution**
- Per enabled elbow, adds 1 aim target bone, 1 bend-only base bone, and intermediate twist bones.
- Intermediate twist bone count is `Segments - 1`, or `Segments` when `Twist Follow` is below `1.0`.
- With default settings, this adds 4 bones and 3 VRC Constraints per arm, or 8 bones and 6 VRC Constraints for both arms. Meshes on the same arm share bones with matching roll ratios.

### Q. How does this affect VRChat Performance Rank?

All generated constraints are native VRC Constraints. Default elbow and knee settings add at most 4 bones and 4 constraints. When using Forearm Twist Distribution, setting `Segments` to `2` reduces the total for both arms to 6 bones and 4 constraints.

### Q. Can I attach JointKeeper to multiple outfits and the avatar root at once?

Yes. If multiple components target the same mesh and joint, only one is applied in this priority order:

1. `ManualList` takes priority over `AutoDetect`.
2. A component closer to the mesh in the hierarchy takes priority over one on a parent object.

### Q. Is it compatible with Modular Avatar and Avatar Optimizer?

Yes. JointKeeper runs in NDMF's `Transforming` phase after Modular Avatar and before Avatar Optimizer.

### Q. Does Edit Mode Bend Preview modify my avatar?

No. It runs on temporary NDMF Preview objects and does not alter scene bones or original mesh assets.

### Q. What should I check if nothing changes or no helper bones are created?

1. Confirm the component is under an avatar with a Humanoid Animator.
2. Check whether the mesh has vertices weighted to both upper and lower bones near the elbow or knee using `Heatmap View` → `HelperWeight`.
3. Check whether the component is already in Baked state. NDMF builds skip baked components; use `Re-Bake` after changing settings.
4. Check `Twist Bone Detection & Diagnostics` and the Unity Console for warnings.

---

## Support & Bug Reports

For bug reports or inquiries, please contact **dennokoworks** via **BOOTH message**.
