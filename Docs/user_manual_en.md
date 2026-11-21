# JointKeeper User Manual

**Version:** v0.1.0  
**Requirements:** Unity 2022.3 / VRChat SDK 3.7.0+ (VRC Constraints) / NDMF (for non-destructive builds & live preview)

---

## 1. Overview (What It Solves)

**JointKeeper** is a non-destructive (NDMF) Unity editor extension for VRChat avatars that prevents **elbow/knee volume collapse** and **forearm twist collapse** simply by attaching a component.

- **Elbow & Knee Volume Preservation (Anti-Clipping)**
  With standard Linear Blend Skinning (LBS), bending an elbow or knee causes the outer side (elbow tip / kneecap) to collapse inward, leading to base body clipping through sleeves or pants and unnaturally thin joints. On build, JointKeeper automatically generates **VRC Parent Constraint helper bones** and redistributes boundary vertex weights to preserve volume.
- **Forearm Twist Distribution (Optional, Elbow-Only)**
  When the forearm rolls, sleeves often twist abruptly right at the elbow ("candy-wrapper" pinching). Forearm Twist Distribution adds a bend-only base bone and intermediate twist bones (driven by **VRC Aim / Rotation Constraints**) to smoothly spread forearm roll from the elbow to the wrist.
- **Non-Destructive Workflow + Destructive Bake Fallback**
  By default, JointKeeper runs non-destructively via NDMF without modifying your original assets. If you need to avoid conflicts with other tools or bake ahead of time, a reversible **Bake & Replace** workflow is also included.

---

## 2. Quick Start

In most cases, simply **attaching the component** is all you need.

1. In the Hierarchy, select your **outfit root** (or an outfit mesh, or the avatar root).
2. Right-click and choose **`dennokoworks` → `JointKeeper`** (or use `Add Component` → **`dennokoworks` → `Joint Keeper`**).
   - Because JointKeeper is designed to work out of the box, the Inspector starts collapsed to keep your view tidy.
3. Enter **Play Mode** (e.g., Gesture Manager) or **upload to VRChat** — NDMF automatically applies elbow and knee correction during the build.

### Previewing in Edit Mode

1. Expand the `JointKeeper` component in the Inspector.
2. Enable **`Edit Mode Bend Preview (NDMF Preview)`**.
3. Drag the **`Bend Angle (°)`** slider to preview bent elbows and knees live in Scene View without rotating any actual bones in your scene.
4. If the base body still pokes through slightly, increase **`Push at 90° (m)`** to around `0.003`–`0.006`.

---

## 3. Features

### 3.1 Elbows & Knees (Core Settings)

| Setting | Default | Description |
| --- | --- | --- |
| **Left / Right** | ON | Enables or disables volume-preservation correction for each side. |
| **Strength** | `0.85` | How strongly boundary weights are shifted to the helper bone (`0` = off, `1` = maximum). Around `0.85` usually looks most natural. |
| **Push at 90° (m)** | `0.002` | Outward displacement (in meters) at a 90° bend along the elbow/kneecap direction. Increase slightly if the body still clips through. |

### 3.2 Forearm Twist Distribution (Elbow-Only, Default OFF)

Prevents sleeves from twisting abruptly at the elbow when the forearm rolls, spreading the twist smoothly from elbow to wrist. Works alongside the elbow volume helper bones.

| Setting | Default | Description |
| --- | --- | --- |
| **Enable Toggle** | OFF | Enables forearm twist distribution. |
| **Twist Follow** | `1.0` | How much the sleeve follows forearm roll at the wrist end (the elbow end is always `0` = bend only). At `1.0`, the wrist moves identically to before while elbow twist is smoothed along the forearm. Lower values reduce roll along the entire sleeve. |

> **Recommended for:** Long sleeves, 3/4 sleeves, and arm covers with no weights beyond the wrist (`Hand` / fingers).  
> For gloves, sleeves covering the back of the hand, or base body meshes, keep **Twist Follow at `1.0`** to avoid a twist step at the wrist.

### 3.3 Edit Mode Bend Preview (NDMF Preview)

Inspect bent/twisted results live in Scene View without modifying scene transforms or original mesh assets.

- **Bend Angle (°)** (`0°`–`150°`): Preview flexion angle for elbows and knees.
- **Forearm Twist (°)** (`-90°`–`+90°`): Preview forearm roll to verify twist bone compatibility and Forearm Twist Distribution.
- **Compare Before (Original)**: Temporarily shows the unmodified original mesh at the same bend angle for instant A/B comparison.
- **Heatmap View**:
  - `Off`: Standard material rendering.
  - `HelperWeight`: Color-codes helper bone weight distribution on the mesh.
  - `FourBoneDiagnostics`: Color-codes VRChat 4-bone-per-vertex consolidation tiers (Tier 1–3).
- **Reset**: Resets bend angle, twist angle, and Before comparison back to `0°`.

### 3.4 Scene View Gizmos

When the component is selected, each enabled joint displays visual guides in Scene View:

- **Cyan Ellipsoid**: Influence region (ROI).
- **Green Circle**: Inner falloff boundary where correction begins tapering off (`Inner Falloff Ratio`).
- **Orange Arrow**: Outward direction (pointing out of the elbow tip / kneecap).
- **Red Line**: Joint hinge (flexion) axis.

### 3.5 Destructive Bake (Bake & Replace)

Pre-bakes helper bones and `.asset` meshes directly into your scene and project (saved next to the original mesh) when avoiding tool conflicts.

- **Bake & Replace**: Generates helper bones and baked `.asset` meshes, swaps them in, and disables the component automatically so NDMF builds will not double-apply.
- **Re-Bake**: Reverts the previous bake and bakes again with your current slider values.
- **Revert**: Restores the original meshes and bone arrays, deletes generated helper bones and `.asset` files, and returns to non-destructive mode.
- **Finalize & Remove Component (Cannot Revert)**: Removes `JointKeeperComponent` while permanently keeping the baked meshes and helper bones.
- **Generated Assets & Helper Bones**: Lists generated `.asset` meshes and helper bones; click any field to ping it in the Project or Hierarchy window.

> Note: **Forearm Twist Distribution** is currently not supported by `Bake & Replace` (applied during NDMF builds only).

### 3.6 Localization & Version Check

Available in the Inspector header:

- **Language Switch (`JA` / `EN`)**: Instantly toggles Inspector labels and tooltips between Japanese and English.
- **Update Checker**: Displays the current version and checks for updates (click `↻` to recheck; shows a link to the releases page when an update is available).

---

## 4. Advanced Features

Expand **`Advanced`** in the Inspector to access fine-grained controls.

### 4.1 Target Mesh Settings

- **Detection Mode (`AutoDetect` / `ManualList`)**
  - `AutoDetect`: Automatically targets child `SkinnedMeshRenderer` components under the GameObject.
  - `ManualList`: Processes only meshes explicitly added to **`Target Renderers (Manual)`**.
- **Excluded Renderers**: Meshes to skip in `AutoDetect` mode (e.g., excluding base body meshes when attached to the avatar root).

### 4.2 Unlink Left / Right

When OFF (default), the right side automatically mirrors the left side (`Pivot Offset.z` and `Outward Angle Offset` are sign-inverted). Turn ON to configure asymmetric left/right outfits independently.

### 4.3 Joint Rig & Influence Region (ROI) Parameters

| Setting | Default | Description |
| --- | --- | --- |
| **Bend Follow Ratio** | `0.50` | Ratio at which the helper bone follows lower-limb rotation (`0.5` = exact midpoint). |
| **Blend Curve Power (Gamma)** | `1.2` | Sharpness of helper weight falloff away from the joint seam. Higher values concentrate correction closer to the crease. |
| **Pivot Offset (Axial, Out, Hinge)** | `(0, 0, 0)` | Fine-tunes helper pivot and ROI center in meters (X: limb axis, Y: outward, Z: hinge axis). |
| **Outward Angle Offset (deg)** | `0°` | Rotates the outward direction (orange arrow) around the limb axis. |
| **Axial Radius (Limb Ratio)** | `0.35` | Influence radius along limb length as a ratio of segment length (vertical extent of cyan ellipsoid). |
| **Radial Radius (Limb Ratio)** | `0.45` | Influence radius perpendicular to limb axis. Increase for baggy sleeves or wide pants. |
| **Inner Falloff Ratio** | `0.25` | Normalized distance where falloff begins toward the ROI edge (green circle). |
| **Inner Cutoff** | `-0.15` | Dot-product threshold where correction drops to minimum on the inner crease side. |
| **Outer Full Threshold** | `0.35` | Dot-product threshold where outer-side correction reaches 100% strength. |
| **Inner Min Retention** | `0.0` | Minimum correction retained on the inner crease side. Keep near `0` for natural inner folding. |
| **Boundary Weight Threshold** | `0.01` | Minimum weight required on both proximal (`G_P`) and distal (`G_D`) groups to qualify as a boundary vertex. |

- **Show Scene View Handles (Pivot / Outward)**: Displays interactive Position and Rotation Disc handles in Scene View.
- **Recalculate Joint Frames (Sample Muscle Axes)**: Samples exact elbow/knee hinge axes and forearm roll axes from Humanoid muscles on a hidden temporary clone.

### 4.4 Forearm Twist Distribution Advanced Parameters

| Setting | Default | Description |
| --- | --- | --- |
| **Segments** | `3` | Number of segments (`1`–`4`) to spread roll over. Thinning at 90° roll is ~3.4% with `3` and ~7.6% with `2`. Use `2` to lower constraint count. |
| **Start Position / End Position** | `0.0` / `1.0` | Start and end of twist distribution as a ratio of forearm length (`0` = elbow, `1` = wrist). |
| **Distribution Curve / Curve Power** | `Linear` / `1.0` | Choose `Linear` (uniform), `Power` (exponent `p` concentrates twist toward wrist), or `SmoothStep`. |
| **Bend-Only Elbow Helper** | ON | Switches the elbow helper's distal source to the bend-only base bone `N` so it does not pick up forearm roll (recommended). |
| **Apply Even With Existing Twist Rig** | OFF | Force-applies to meshes weighted to existing constraint-driven forearm twist bones (existing twist bone weights are left untouched). |
| **Knot Snap Threshold** | `0.15` | Snaps vertices near a knot boundary onto a single bone to save weight slots. |

### 4.5 Twist Bone Classification, 4-Bone Limit & Diagnostics

- **Auto-Detect Twist Bones**: Classifies existing twist and rigid child bones into proximal (`G_P`) and distal (`G_D`) groups via hierarchy and constraint dependencies.
- **Distal Source Mode / Override**: Controls how the lower-limb constraint source is selected (normally leave at `AutoLowerBone`).
- **Extra Proximal (`G_P`) / Extra Distal (`G_D`) / Excluded Bones**: Manually add custom helper bones or exclude dangling accessories/gimmicks.
- **4-Bone Tier 2 Snap Threshold (`0.05`) / Tier 3 Noise Threshold (`0.03`)**: Safely consolidates vertex weights when hitting VRChat's 4-bone-per-vertex limit without dropping important bones.
- **Twist Bone Detection & Diagnostics**: Displays bone classification (`G_P` / `G_D`), constraint sources, and measured forearm roll axis diagnostics.

---

## 5. Model & Rig Requirements / Caveats

Please keep the following points in mind regarding your avatar and outfit models:

1. **Valid Humanoid Rig Required**
   - The avatar must have a valid **Humanoid Animator** with `UpperArm` / `LowerArm` / `UpperLeg` / `LowerLeg` mapped.
   - For **Forearm Twist Distribution**, a `Hand` bone and non-zero `LowerArm` roll response under Humanoid muscle twist are required (if an avatar's `LowerArm` barely rolls on forearm twist, Forearm Twist Distribution will have minimal effect and a warning appears in Diagnostics).
2. **Joint Boundary Weight Distribution**
   - Elbow/knee volume preservation acts on vertices inside the ROI that carry weights on **both** proximal (`G_P`) and distal (`G_D`) bone groups (`>= Boundary Weight Threshold`, default `0.01`).
   - Rigid parts with hard `100% / 0%` splits at the joint seam (armor, mechanical parts) or meshes with no vertices near elbows/knees (hair, shoes) are automatically skipped.
3. **Existing Twist Bone Rigs**
   - Constraint-driven twist bones are automatically detected.
   - Elbow/knee volume preservation coexists cleanly with existing twist rigs without breaking twist motion.
   - Forearm Twist Distribution automatically skips meshes already weighted to existing constraint-driven forearm twist bones to avoid double-twisting (can be overridden with `Apply Even With Existing Twist Rig`).
4. **Meshes Weighted Beyond the Wrist + Forearm Twist**
   - For gloves, sleeves covering the back of the hand, or base body meshes, setting `Twist Follow` below `1.0` causes a twist step at the wrist (the Inspector warns when hand weights are detected). Keep `Twist Follow = 1.0`.
5. **PhysBones on the Forearm**
   - PhysBone chains attached to the forearm (ribbons, cuffs) continue following the original bone roll and may slightly separate from surrounding sleeve vertices when Forearm Twist Distribution is enabled.
6. **Modular Avatar (`Merge Armature` / `Scale Adjuster`) Compatibility**
   - Fully supports outfits with Blender **100x scale Armatures** and `MA Scale Adjuster` in both NDMF builds and Edit Mode Preview.
   - If you use destructive **Bake & Replace** on an outfit with `MA Scale Adjuster` and later change Scale Adjuster values, click **`Re-Bake`** to update the baked bindposes.

---

## 6. Q&A (Bone Addition Conditions, Performance & Specifications)

### Q1. How many bones and constraints are added, and under what conditions?

- **Elbow & Knee Volume Preservation (Default)**
  - Adds **1 bone + 1 `VRCParentConstraint`** per enabled joint.
  - **Automatic Sharing (Deduplication):** If multiple outfits (or body + outfits) use `JointKeeper` with identical rig parameters on the same joint (`Bend Follow Ratio`, `Push at 90°`, `Pivot Offset`, `Outward Angle Offset`, and source bones), **they share a single helper bone**.
  - With default settings across your entire avatar, only **up to 4 bones / 4 VRC Constraints** are added in total.
  - Additional helper bones are created only when different components specify different rig parameters for the same joint. Joints with zero affected vertices create **0 bones**.

- **Forearm Twist Distribution (Optional, Default OFF)**
  - Per enabled elbow: **1 aim target (no constraint) + 1 bend-only base bone `N` (1 `VRCAimConstraint`) + intermediate twist bones**.
  - Intermediate twist bone count is **`Segments - 1`** (or **`Segments`** when `Twist Follow < 1.0`), each with 1 `VRCRotationConstraint`.
  - With defaults (`Segments = 3`, `Twist Follow = 1.0`), this adds **4 bones / 3 VRC Constraints per elbow** (**8 bones / 6 VRC Constraints** for both arms).
  - Just like elbow helpers, `N` and intermediate bones with identical roll ratios `k` are **automatically shared** across all meshes on the same arm.

### Q2. How does it affect VRChat Performance Rank?

- JointKeeper exclusively creates native **VRC Constraints** (`VRCParentConstraint`, `VRCAimConstraint`, `VRCRotationConstraint`), which have much lower CPU overhead than legacy Unity Constraints and are compatible with Quest / Mobile.
- With default settings (Elbows & Knees only, Forearm Twist OFF), adding at most **+4 bones / +4 VRC Constraints** has negligible impact on Performance Rank.
- When using Forearm Twist Distribution, lowering **`Segments` to `2`** reduces the cost to 3 bones / 2 VRC Constraints per elbow (6 bones / 4 VRC Constraints for both arms).

### Q3. Can I attach `JointKeeper` to multiple outfits and the avatar root at the same time?

Yes. When multiple `JointKeeper` components cover the same mesh and joint, only one is applied using this priority order (no double-application):

1. `ManualList` > `AutoDetect`
2. Closer (deeper) component in the hierarchy > parent (avatar root) component

This lets you place a default `JointKeeper` on the avatar root and override specific outfits with their own `JointKeeper` component.

### Q4. Is it compatible with Modular Avatar and Avatar Optimizer (AAO)?

Yes. JointKeeper runs in NDMF's `Transforming` phase **after** `Modular Avatar` and **before** `Avatar Optimizer (AAO)`. Merged armatures and scale adjusters are already resolved when JointKeeper runs, and AAO can freely optimize the resulting meshes and bones afterward.

### Q5. Does Edit Mode Bend Preview modify my avatar or scene?

No. Edit Mode Bend Preview runs on temporary NDMF Preview shadow hierarchies and never modifies your scene transforms or original mesh assets. Turning the preview OFF restores the normal view immediately.

### Q6. What should I check if no effect appears or no helper bones are created?

Please verify:

1. The component is placed under an avatar with a valid **Humanoid Animator**.
2. The target mesh actually has blended weights on both upper and lower limb bones near the elbow/knee (check with `Heatmap View → HelperWeight`).
3. The component is not already in **Baked** state (NDMF builds skip baked components; click `Re-Bake` after changing sliders).
4. Check `Advanced → Twist Bone Detection & Diagnostics` and the Unity Console for warnings or skip reasons.

---

## Support & Bug Reports

For bug reports, questions, or feature requests, please contact **dennokoworks** via **BOOTH message**.
