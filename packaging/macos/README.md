# macOS 安装窗口资源

`installer-background.png` 使用内置 imagegen 生成，生成提示词：

> Use case: stylized-concept. Asset type: landscape background artwork for a macOS DMG Finder installer window for SVL, a cozy Stardew Valley launcher. Create one polished 3:2 landscape pixel-art scene in warm cream, peach, amber and meadow green, inspired by an inviting 16-bit countryside farm at golden hour. The composition must work behind two real, separately rendered Finder icons: keep two generous pale, quiet empty spaces centered at roughly 25% and 75% of the width, vertically near the middle; leave enough low-contrast empty room for icon labels. In the center, a clear small right-pointing pixel-art arrow indicates dragging from left to right. Decorative pixel foliage, wooden fences, hills, subtle flowers and tiny farm details confined to the outer border and bottom edge; gentle cream sky. High-quality intentional pixel art with crisp edges and a restrained palette. No screenshots, no windows, no text, no letters, no logos, no watermarks, no drawn application icons or folder icons, no hand, no cursor. The supplied art will be cropped to a wide installer window, with real app and Applications folder icons placed over it.

`installer-layout.dsstore` 是预生成的 Finder 窗口与图标布局，引用的映像卷名为
`SVL Installer`，背景位于映像内的 `.background/installer.png`。更改背景路径、
卷名或图标位置时，需要同步更新 `scripts/generate-macos-dmg-layout.py` 并重新生成
布局文件。正常打包直接使用已有布局，不需要 Python、Finder 或图形会话。

布局生成脚本改编自 Chromium，许可详见 `CHROMIUM-LICENSE.txt`。
