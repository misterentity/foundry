"""Geometry tests for the CAD sidecar.

Runs against the real trimesh + manifold3d kernel — these assert the produced SOLID, not just that
the call returned bytes. Mirrors Foundry.Core/Pcb/KiCadScripts/test_build_board.py, which does the
same for the pure pad-assignment logic.

    sidecar/.venv/Scripts/python -m pytest sidecar/test_enclosure.py -q
"""
import math
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import enclosure  # noqa: E402

# Locally the CSG deps may be absent and skipping is fine. In CI they are installed on purpose, so
# FOUNDRY_REQUIRE_CSG=1 turns a skip into a failure — otherwise a broken install would show up as a
# green lane that tested nothing, which is the failure mode these tests exist to prevent elsewhere.
_STRICT = os.environ.get("FOUNDRY_REQUIRE_CSG") == "1"
try:
    import trimesh
    import manifold3d  # noqa: F401
except ImportError as exc:  # pragma: no cover
    if _STRICT:
        raise AssertionError(f"FOUNDRY_REQUIRE_CSG=1 but the CSG kernel is missing: {exc}") from exc
    pytest.skip(f"CSG kernel unavailable: {exc}", allow_module_level=True)

INNER = [62.0, 48.0, 26.0]
WALL = 2.0


def _mesh(schema):
    """Build and return (mesh, stats). Kernel must be manifold — the fallback is not a real solid."""
    data, stats = enclosure.build_stl(schema)
    assert stats["kernel"] == "manifold", f"CSG kernel degraded to {stats['kernel']}"
    import io
    mesh = trimesh.load(io.BytesIO(data), file_type=stats["format"])
    return mesh, stats


def _base_schema(**over):
    s = {"inner": INNER, "wall_mm": WALL, "standoffs": 4, "lid": "screw", "cutouts": [], "vents": []}
    s.update(over)
    return s


def test_plain_enclosure_is_a_watertight_solid():
    mesh, stats = _mesh(_base_schema())
    assert mesh.volume > 0
    assert stats["triangles"] > 0
    # base + lid are exported as one scene; each body must itself be closed.
    for body in mesh.split(only_watertight=False):
        assert body.is_watertight, "every body must be a closed solid or it will not slice"


def test_front_cutout_removes_material():
    plain, _ = _mesh(_base_schema())
    cut, _ = _mesh(_base_schema(cutouts=[
        {"face": "front", "shape": "rect", "size": [9.5, 3.5], "pos": [0, -6], "label": "USB-C"}]))
    assert cut.volume < plain.volume, "a front port must remove material from the wall"


# The regression this file exists for: face:"top" features were cut against the BASE, which is open
# above (oz = H + t). The cutter pierced empty space, so the hole never reached the printed lid and
# the flagship demo shipped a sealed lid with no reset hole and no LED window.
@pytest.mark.parametrize("cutout,expected_area", [
    ({"face": "top", "shape": "circle", "d": 6.0, "pos": [0, 0], "label": "Reset"},
     math.pi * 3.0 ** 2),
    ({"face": "top", "shape": "rect", "size": [12.0, 8.0], "pos": [0, 0], "label": "OLED window"},
     12.0 * 8.0),
])
def test_top_cutout_actually_pierces_the_lid(cutout, expected_area):
    plain, _ = _mesh(_base_schema())
    cut, _ = _mesh(_base_schema(cutouts=[cutout]))

    removed = plain.volume - cut.volume
    assert removed > 0, "a top-face cutout removed NO material — it is not reaching the lid"

    # The lid is a cap (>= wall) plus a locating lip (>= wall + 1) and a centred hole pierces both.
    cap_t = max(2.0, WALL)
    lip_h = max(2.0, WALL + 1.0)
    expected = expected_area * (cap_t + lip_h)
    assert 0.5 * expected < removed < 1.6 * expected, (
        f"removed {removed:.1f} mm^3, expected ~{expected:.1f} mm^3 through cap+lip")


def test_top_cutout_leaves_the_lid_watertight():
    cut, _ = _mesh(_base_schema(cutouts=[
        {"face": "top", "shape": "circle", "d": 6.0, "pos": [10, 5], "label": "LED"}]))
    for body in cut.split(only_watertight=False):
        assert body.is_watertight, "cutting the lid must not leave an open shell"


def test_top_vents_reach_the_lid_too():
    plain, _ = _mesh(_base_schema())
    vented, _ = _mesh(_base_schema(vents=[{"face": "top", "count": 4}]))
    assert vented.volume < plain.volume, "top-face vents must be cut into the lid"


def test_bottom_cutout_still_cuts_the_floor():
    plain, _ = _mesh(_base_schema())
    cut, _ = _mesh(_base_schema(cutouts=[
        {"face": "bottom", "shape": "circle", "d": 5.0, "pos": [0, 0], "label": "drain"}]))
    assert cut.volume < plain.volume, "bottom cutouts belong to the base floor and must still work"


def test_cutout_on_every_face_removes_material():
    """No face may silently swallow a port — that is the exact class of bug this file guards."""
    plain, _ = _mesh(_base_schema())
    for face in ("front", "back", "left", "right", "top", "bottom"):
        cut, _ = _mesh(_base_schema(cutouts=[
            {"face": face, "shape": "circle", "d": 6.0, "pos": [0, 0], "label": face}]))
        removed = plain.volume - cut.volume
        assert removed > 1.0, f"face '{face}' cutout removed only {removed:.2f} mm^3"


# The regression that made every exported file unslicable: the preview's explode offset was applied to
# the mesh that was then EXPORTED, so the STL held a lid floating ~7 mm above the base with fully
# overlapping XY. A slicer either rejects the floating body or builds 33 mm of support under it.
def test_print_arrangement_puts_every_body_flat_on_the_plate():
    mesh, _ = _mesh(_base_schema(arrange="print"))
    bodies = mesh.split(only_watertight=False)
    assert len(bodies) >= 2, "expected a base and a lid"
    for b in bodies:
        assert b.bounds[0][2] == pytest.approx(0.0, abs=1e-6), \
            f"body starts at z={b.bounds[0][2]:.2f}, not on the plate"


def test_print_arrangement_separates_the_bodies_in_xy():
    mesh, _ = _mesh(_base_schema(arrange="print"))
    bodies = sorted(mesh.split(only_watertight=False), key=lambda b: b.bounds[0][0])
    a, b = bodies[0], bodies[-1]
    assert a.bounds[1][0] < b.bounds[0][0], "bodies overlap in X — they cannot both be printed"


def test_exploded_arrangement_is_still_stacked_for_the_preview():
    mesh, _ = _mesh(_base_schema(arrange="exploded"))
    bodies = sorted(mesh.split(only_watertight=False), key=lambda b: b.bounds[0][2])
    assert bodies[-1].bounds[0][2] > bodies[0].bounds[1][2], "lid should sit above the base in preview"


def test_arrangement_defaults_to_exploded_so_the_preview_is_unchanged():
    a, _ = _mesh(_base_schema())
    b, _ = _mesh(_base_schema(arrange="exploded"))
    assert a.volume == pytest.approx(b.volume)


# Arrangement must move geometry, never change it — same solid, different placement.
def test_arrangement_does_not_alter_the_geometry():
    p, _ = _mesh(_base_schema(arrange="print"))
    e, _ = _mesh(_base_schema(arrange="exploded"))
    # rel=1e-6, not tighter: the print arrangement rotates the lid, and a rigid-body transform through
    # float64 perturbs the computed volume in the ~1e-8 relative range. Tighter asserts float exactness,
    # which is not what "same solid" means.
    assert p.volume == pytest.approx(e.volume, rel=1e-6)
    assert len(p.faces) == len(e.faces)


# PCB MOUNTING. Before this the case had NOTHING to hold the board: _standoff_posts builds lid screw
# bosses, positioned from the CASE corners and running nearly the full cavity height, so a printed
# enclosure came with a loose PCB — and a port's height above the floor was undefined because no
# geometry established where the board sits.
BOARD = {
    "widthMm": 71.5, "depthMm": 62.9, "thicknessMm": 1.6, "standoffMm": 4.0,
    "holes": [[4.0, 4.0], [67.5, 4.0], [67.5, 58.9], [4.0, 58.9]],
}


def test_pcb_standoffs_add_material_at_the_board_holes():
    plain, _ = _mesh(_base_schema())
    mounted, _ = _mesh(_base_schema(board=BOARD))
    assert mounted.volume > plain.volume, "no PCB standoffs were added"


def test_pcb_standoffs_land_under_the_board_holes():
    mounted, _ = _mesh(_base_schema(board=BOARD))
    base = max(mounted.split(only_watertight=False), key=lambda b: b.volume)

    # holes are in BOARD coords; the case is centred, so subtract half the board extent
    for hx, hy in BOARD["holes"]:
        cx, cy = hx - BOARD["widthMm"] / 2, hy - BOARD["depthMm"] / 2
        # a post occupies this column just above the floor
        near = [f for f in base.triangles_center
                if abs(f[0] - cx) < 3.5 and abs(f[1] - cy) < 3.5
                and WALL < f[2] < WALL + BOARD["standoffMm"] + 0.5]
        assert near, f"no standoff geometry at board hole ({hx}, {hy}) -> case ({cx:.1f}, {cy:.1f})"


def test_pcb_standoffs_stop_below_the_board_plane():
    mounted, _ = _mesh(_base_schema(board=BOARD))
    base = max(mounted.split(only_watertight=False), key=lambda b: b.volume)
    top = WALL + BOARD["standoffMm"]
    # nothing from the standoffs may poke above the plane the board sits on (within the cavity footprint)
    intruding = [f for f in base.triangles_center
                 if abs(f[0]) < BOARD["widthMm"] / 2 - 6 and abs(f[1]) < BOARD["depthMm"] / 2 - 6
                 and top + 0.5 < f[2] < top + BOARD["standoffMm"]]
    assert not intruding, "standoff geometry rises through the board plane"


def test_board_without_holes_adds_nothing():
    plain, _ = _mesh(_base_schema())
    no_holes, _ = _mesh(_base_schema(board={**BOARD, "holes": []}))
    assert no_holes.volume == pytest.approx(plain.volume)


@pytest.mark.parametrize("bad", [None, {}, {"holes": [["x", "y"]], "widthMm": 70, "depthMm": 60, "standoffMm": 4}])
def test_a_malformed_board_never_breaks_the_build(bad):
    mesh, _ = _mesh(_base_schema(board=bad))
    assert mesh.volume > 0


def test_a_bad_cutout_does_not_break_the_build():
    mesh, _ = _mesh(_base_schema(cutouts=[
        {"face": "nonsense", "shape": "??", "size": ["x", None]},
        {"face": "top", "shape": "circle", "d": 6.0, "pos": [0, 0], "label": "Reset"}]))
    assert mesh.volume > 0
