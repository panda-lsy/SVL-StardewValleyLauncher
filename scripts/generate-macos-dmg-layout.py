#!/usr/bin/env python3
# Finder 元数据生成逻辑改编自 Chromium（Copyright 2026 The Chromium Authors）。
# 其 BSD 许可文本见 packaging/macos/CHROMIUM-LICENSE.txt。
"""生成打包用 Finder 元数据；构建 DMG 不依赖 Python 或 Finder。

仅修改布局时需要：python -m pip install ds_store==1.3.3 mac_alias==2.2.3
布局格式参考 Chromium 的 generate_dsstore.py：
https://chromium.googlesource.com/chromium/src/+/9c363bb2863cd723978a8a245719389a7bf1c20e/chrome/installer/mac/generate_dsstore.py
"""

import argparse
from datetime import datetime, timezone
from pathlib import Path

from ds_store import DSStore
from mac_alias import (
    ALIAS_EJECTABLE_DISK,
    ALIAS_HFS_VOLUME_SIGNATURE,
    ALIAS_KIND_FILE,
    ALIAS_NO_CNID,
    Alias,
    TargetInfo,
    VolumeInfo,
)


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description="生成 macOS 安装器 Finder 布局")
    parser.add_argument("--output", type=Path, default=root / "packaging" / "macos" / "installer-layout.dsstore")
    output = parser.parse_args().output
    if output.exists():
        raise SystemExit(f"布局文件已存在，请先备份后移走：{output}")

    stamp = datetime(2000, 5, 7, 21, 10, tzinfo=timezone.utc)
    volume = VolumeInfo(
        name="SVL Installer",
        creation_date=stamp,
        fs_type=ALIAS_HFS_VOLUME_SIGNATURE,
        disk_type=ALIAS_EJECTABLE_DISK,
        attribute_flags=0,
        fs_id=b"\0\0",
        posix_path="/Volumes/SVL Installer",
    )
    background = TargetInfo(
        kind=ALIAS_KIND_FILE,
        filename="installer.png",
        folder_cnid=ALIAS_NO_CNID,
        cnid=ALIAS_NO_CNID,
        creation_date=stamp,
        creator_code=b"\0\0\0\0",
        type_code=b"\0\0\0\0",
        folder_name=None,
        posix_path="/.background/installer.png",
        carbon_path="SVL Installer:.background:installer.png",
    )
    icon_view = {
        "viewOptionsVersion": 1,
        "backgroundType": 2,
        "backgroundImageAlias": Alias(volume=volume, target=background).to_bytes(),
        "gridOffsetX": 0.0,
        "gridOffsetY": 0.0,
        "gridSpacing": 100.0,
        "arrangeBy": "none",
        "showIconPreview": True,
        "showItemInfo": False,
        "labelOnBottom": True,
        "textSize": 14.0,
        "iconSize": 112.0,
        "backgroundColorRed": 1.0,
        "backgroundColorGreen": 1.0,
        "backgroundColorBlue": 1.0,
    }
    window = {
        "ShowStatusBar": False,
        "WindowBounds": "{{120, 120}, {660, 468}}",
        "ContainerShowSidebar": False,
        "PreviewPaneVisibility": False,
        "SidebarWidth": 180,
        "ShowTabView": False,
        "ShowToolbar": False,
        "ShowPathbar": False,
        "ShowSidebar": False,
    }
    with DSStore.open(str(output), "w+") as store:
        store["."]["vSrn"] = ("long", 1)
        store["."]["bwsp"] = window
        store["."]["icvp"] = icon_view
        store["."]["icvl"] = (b"type", b"icnv")
        store["Stardew Valley Launcher.app"]["Iloc"] = (165, 228)
        store["Applications"]["Iloc"] = (495, 228)
    print(f"已生成 Finder 布局：{output}")


if __name__ == "__main__":
    main()
