"""Unit tests for Wally core logic (no Windows APIs touched)."""

from wally.core.models import (
    DesktopAssignment,
    MonitorRule,
    SetupRule,
    WallpaperFit,
    WallyConfig,
)
from wally.core import rules, store
from tests.conftest import make_monitor
from wally.core.models import MonitorSetup


# --- fingerprint ---------------------------------------------------------

def test_fingerprint_is_order_independent():
    a = MonitorSetup((make_monitor("A"), make_monitor("B")))
    b = MonitorSetup((make_monitor("B"), make_monitor("A")))
    assert a.fingerprint == b.fingerprint


def test_fingerprint_distinguishes_setups(laptop_only, laptop_plus_27):
    assert laptop_only.fingerprint != laptop_plus_27.fingerprint


def test_fingerprint_is_stable_across_calls(laptop_plus_27):
    assert laptop_plus_27.fingerprint == laptop_plus_27.fingerprint


# --- rule resolution -----------------------------------------------------

def test_new_setup_reports_all_unassigned(laptop_plus_27):
    result = rules.resolve_setup(WallyConfig(), laptop_plus_27)
    assert result.is_new_setup is True
    assert len(result.unassigned) == 2
    assert result.actions == ()


def test_explicit_per_monitor_rule_resolves(laptop_plus_27):
    rule = SetupRule(
        setup_fingerprint=laptop_plus_27.fingerprint,
        per_monitor=(
            MonitorRule("LAPTOP_PANEL", "C:/w/panel.jpg", WallpaperFit.FILL),
            MonitorRule("DELL_27_U2720Q", "C:/w/dell.jpg", WallpaperFit.SPAN),
        ),
    )
    config = WallyConfig().with_setup_rule(rule)
    result = rules.resolve_setup(config, laptop_plus_27)
    assert result.is_new_setup is False
    assert result.unassigned == ()
    paths = {a.image_path for a in result.actions}
    assert paths == {"C:/w/panel.jpg", "C:/w/dell.jpg"}


def test_default_image_covers_unlisted_monitor(laptop_plus_27):
    rule = SetupRule(
        setup_fingerprint=laptop_plus_27.fingerprint,
        per_monitor=(MonitorRule("LAPTOP_PANEL", "C:/w/panel.jpg"),),
        default_image="C:/w/default.jpg",
    )
    config = WallyConfig().with_setup_rule(rule)
    result = rules.resolve_setup(config, laptop_plus_27)
    assert result.unassigned == ()
    dell = [a for a in result.actions if a.device_id.endswith("path")
            and "DELL" in a.device_id][0]
    assert dell.image_path == "C:/w/default.jpg"


# --- rotation ------------------------------------------------------------

def test_rotation_prefers_unused():
    config = WallyConfig(rotation_pool=("a.jpg", "b.jpg", "c.jpg"))
    assert rules.next_rotation_image(config, ("a.jpg",)) == "b.jpg"


def test_rotation_wraps_when_all_used():
    config = WallyConfig(rotation_pool=("a.jpg", "b.jpg"))
    assert rules.next_rotation_image(config, ("a.jpg", "b.jpg")) == "a.jpg"


def test_rotation_empty_pool_returns_none():
    assert rules.next_rotation_image(WallyConfig(), ()) is None


# --- virtual desktop assignment ------------------------------------------

def test_desktop_assignment_lookup():
    config = WallyConfig().with_desktop_assignment(
        DesktopAssignment("guid-1", "C:/w/one.jpg")
    )
    assert rules.resolve_desktop(config, "guid-1").image_path == "C:/w/one.jpg"
    assert rules.resolve_desktop(config, "guid-2") is None


# --- immutability --------------------------------------------------------

def test_with_setup_rule_does_not_mutate_original():
    original = WallyConfig()
    rule = SetupRule(setup_fingerprint="fp1")
    updated = original.with_setup_rule(rule)
    assert original.setup_rules == ()
    assert len(updated.setup_rules) == 1


def test_with_monitor_replaces_not_duplicates():
    rule = SetupRule(setup_fingerprint="fp")
    r1 = rule.with_monitor(MonitorRule("A", "one.jpg"))
    r2 = r1.with_monitor(MonitorRule("A", "two.jpg"))
    assert len(r2.per_monitor) == 1
    assert r2.per_monitor[0].image_path == "two.jpg"


# --- persistence round-trip ----------------------------------------------

def test_config_json_round_trip(tmp_path):
    config = (
        WallyConfig(new_desktop_behavior="auto_assign",
                    rotation_pool=("x.jpg", "y.jpg"))
        .with_setup_rule(
            SetupRule(
                setup_fingerprint="fp",
                per_monitor=(MonitorRule("A", "a.jpg", WallpaperFit.FIT),),
                default_image="d.jpg",
            )
        )
        .with_desktop_assignment(DesktopAssignment("g1", "g.jpg"))
    )
    path = tmp_path / "config.json"
    store.save(config, path)
    loaded = store.load(path)
    assert loaded == config


def test_load_missing_file_returns_empty(tmp_path):
    assert store.load(tmp_path / "nope.json") == WallyConfig()


def test_load_corrupt_file_raises(tmp_path):
    bad = tmp_path / "bad.json"
    bad.write_text("{ not json", encoding="utf-8")
    try:
        store.load(bad)
        assert False, "expected ValueError"
    except ValueError:
        pass
