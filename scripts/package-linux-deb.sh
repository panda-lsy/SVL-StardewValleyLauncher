#!/usr/bin/env sh
set -eu

# 将自包含 Linux ZIP 转成 Debian/Ubuntu 安装包，不需要安装 .NET SDK。
if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
    echo "用法: sh scripts/package-linux-deb.sh <Linux ZIP> [输出目录] [版本]" >&2
    exit 2
fi

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
archive=$1
output_dir=${2:-"$repo_root/artifacts"}
version=${3:-1.2.0.0}

for command in dpkg dpkg-deb unzip install mktemp; do
    if ! command -v "$command" >/dev/null 2>&1; then
        echo "缺少打包工具: $command" >&2
        exit 1
    fi
done
if [ ! -f "$archive" ]; then
    echo "Linux 发布包不存在: $archive" >&2
    exit 1
fi
if ! dpkg --validate-version "$version" >/dev/null 2>&1; then
    echo "Debian 版本号无效: $version" >&2
    exit 1
fi

mkdir -p -- "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
destination="$output_dir/svl-launcher_${version}_amd64.deb"
if [ -e "$destination" ]; then
    echo "目标文件已存在，请使用新的输出目录: $destination" >&2
    exit 1
fi

stage=$(mktemp -d "${TMPDIR:-/tmp}/svl-deb-build.XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
package="$stage/package"
install -d -m 0755 "$package/DEBIAN" "$package/usr/bin" \
    "$package/usr/lib/svl" "$package/usr/share/applications" \
    "$package/usr/share/icons/hicolor/256x256/apps"

# 只提取预期的可执行文件，避免归档中的其他路径影响安装位置。
unzip -p "$archive" SVL.Avalonia > "$package/usr/lib/svl/SVL.Avalonia"
if [ ! -s "$package/usr/lib/svl/SVL.Avalonia" ]; then
    echo "Linux 发布包没有可执行文件" >&2
    exit 1
fi
chmod 0755 "$package/usr/lib/svl/SVL.Avalonia"
ln -s ../lib/svl/SVL.Avalonia "$package/usr/bin/svl"
install -m 0644 "$repo_root/packaging/linux/svl.desktop" "$package/usr/share/applications/svl.desktop"
install -m 0644 "$repo_root/SVL.Avalonia/Assets/Icons/icon.png" "$package/usr/share/icons/hicolor/256x256/apps/svl.png"
installed_size=$(du -sk "$package/usr" | cut -f 1)

cat > "$package/DEBIAN/control" <<EOF
Package: svl-launcher
Version: $version
Section: games
Priority: optional
Architecture: amd64
Maintainer: panda-lsy <shengxia23@hainnu.edu.cn>
Installed-Size: $installed_size
Depends: ca-certificates, libc6 (>= 2.35), libgcc-s1, libgssapi-krb5-2, libicu78 | libicu76 | libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, libstdc++6, tzdata, zlib1g, libx11-6, libice6, libsm6, libfontconfig1
Homepage: https://github.com/panda-lsy/SVL-StardewValleyLauncher
Description: Stardew Valley launcher and mod manager
 SVL manages Stardew Valley instances, SMAPI, mods and modpacks.
EOF

dpkg-deb --build --root-owner-group "$package" "$stage/svl.deb"
# 禁止覆盖已有产物，即使有其他打包进程同时运行也一样。
(set -C; cat "$stage/svl.deb" > "$destination")
printf '[ok] Ubuntu/Debian 安装包 -> %s\n' "$destination"
