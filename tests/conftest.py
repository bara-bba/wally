"""Shared fixtures: synthetic monitors and setups for core logic tests."""

import pytest

from wally.core.models import Monitor, MonitorSetup


def make_monitor(edid: str, primary: bool = False, w: int = 1920, h: int = 1080,
                 x: int = 0, y: int = 0) -> Monitor:
    return Monitor(
        device_id=f"\\\\?\\DISPLAY#{edid}#path",
        edid_key=edid,
        width=w,
        height=h,
        x=x,
        y=y,
        is_primary=primary,
        friendly_name=edid,
    )


@pytest.fixture
def laptop_only() -> MonitorSetup:
    return MonitorSetup(monitors=(make_monitor("LAPTOP_PANEL", primary=True),))


@pytest.fixture
def laptop_plus_27() -> MonitorSetup:
    return MonitorSetup(
        monitors=(
            make_monitor("LAPTOP_PANEL", primary=True),
            make_monitor("DELL_27_U2720Q", w=3840, h=2160, x=1920),
        )
    )
