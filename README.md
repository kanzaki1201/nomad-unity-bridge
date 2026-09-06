# Nomad Sculpt Unity Bridge (One Way)

Live, one-way scene preview from Nomad to the Unity Editor through [Nomad App Linking](https://github.com/stephomi/nomad-link).
Sync multiple objects and assign Unity materials for previz.

[Watch the Nomad Sculpt to Unity demo](Documentation~/nomad-unity-bridge.mp4).

## Requirements

- Unity 6000.3 or later.
- A Nomad version with App Linking support.

## Installation

In Unity Package Manager, click **+** > **Install package from git URL** and paste:

```text
https://github.com/kanzaki1201/nomad-unity-bridge.git
```

## Setup

1. Enable App Linking in Nomad.
2. Create an empty GameObject in the scene you want to preview and attach the `NomadLinkScene` script.
3. In its Inspector, set **Host** to the address of the device running Nomad and **Port** to `48312`.
   Use `127.0.0.1` only when Nomad runs on the same computer.
4. Click **Enable Sync** and accept the pairing request in Nomad when prompted.
5. Assign material assets in **Synced Object Materials**.
   Use materials compatible with your Unity render pipeline.

## Preview materials

- Each synced object has one material field.
- Mesh UV coordinates are preserved for textured Unity materials.
- You can assign, replace, or clear it independently, including on objects that share geometry.
- Mesh updates, transforms, visibility changes, and renames preserve the assignment during the session.
- Preview objects and their material assignments last only for the active session.
- **Disable Sync**, a lost connection, scene closure, script reload, or a Play mode transition clears the preview.
- The pairing token is stored in Unity Editor preferences for the same host and port.

## Limits

- Preview objects and Unity material assignments are session-only and are not persistent.
- Only meshes are supported.
- Vertex colors are not supported.
- MToon materials can appear excessively bright at close range on synced meshes; see the [known issue](https://github.com/kanzaki1201/nomad-unity-bridge/issues/1).
- One scene can own the active sync session at a time, in Edit mode only.
- Unity changes are not sent to Nomad.
- The bridge receives geometry and object state; assign shading in Unity.
- For multiple materials, use separate Nomad objects.

## Upstream

Based on Nomad Link 0.11.43, protocol version 1
([source commit](https://github.com/stephomi/nomad-link/tree/f55dc803dd3224e0cd2e65cfe32633813114111d)).
