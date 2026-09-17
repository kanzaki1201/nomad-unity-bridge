# Changelog

## 0.1.1

### Added

- Receive Nomad composited vertex paint: `color` (`rgbm8`) and `opacity` (`uint8_norm`) channels over `mesh_full`, `mesh_delta` and `mesh_attributes`, written to `Mesh.colors`.
- Preserve mesh UV coordinates and seams from `mesh_full`.

### Fixed

- Unsupported mesh deltas keep the current preview and session instead of removing it.
- Preview meshes with UVs refresh tangents.

## 0.1.0

- Initial release.
