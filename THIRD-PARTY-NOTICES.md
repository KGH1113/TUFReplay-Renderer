# Third-party dependencies

- OrbitRender embedded engine: GPL-3.0-only, originally by imnyang, with fork
  modifications by KGH1113 and contributors.
  Source: https://github.com/KGH1113/OrbitRender, baseline commit `aba675b`.
  The modified engine source is included in `src/Engine`; it is compiled into
  TUFReplay-Renderer.dll. The original license and ADOFAI/Unity linking exception
  are included in ORBIT-LICENSE.md and ORBIT-LICENSE-EXCEPTION.
- AdofaiIpc: MIT. Installed as a separate game mod. Source:
  https://github.com/KGH1113/adofai-ipc.
- Harmony: MIT. Provided by UnityModManager. https://github.com/pardeike/Harmony.
- Newtonsoft.Json: MIT. Provided by the game. https://github.com/JamesNK/Newtonsoft.Json.
- UnityModManager: MIT. https://github.com/newman55/unity-mod-manager.
- ImplDmNote overlay: GPL-3.0-only. The optional renderer communicates with the
  separately installed desktop app over AdofaiIpc; no overlay source is copied
  into TUFReplay. Source:
  https://github.com/KGH1113/ImplResourcePack.
- FFmpeg: executed as a separate process. The license depends on the exact build;
  x264-enabled and version3-enabled builds can be GPL-3.0-or-later. We do not bundle
  FFmpeg binaries. https://ffmpeg.org/legal.html.

Game and Unity assemblies, third-party mods, user images and recorded content are not
redistributed. If a release ever bundles a binary dependency, include its complete
license, exact build configuration and verified corresponding source first.

## MapleStory Typeface

TUFReplay-Renderer includes the MapleStory typeface provided by NEXON Korea
Corporation in its render progress UI assets.

- Copyright: NEXON Korea Corporation. All rights reserved.
- Source: https://maplestory.nexon.com/Media/Font
- The typeface is provided free of charge for personal and commercial use and may
  be bundled or embedded with this copyright notice.
- The font itself is not sold, modified, or redistributed as a standalone paid
  product by TUFReplay-Renderer.

## TextMesh Pro resources

The Unity project includes the official TextMesh Pro essential resources from
Unity's uGUI package, under the Unity Companion License for Unity-dependent
projects (`UGUI-LICENSE.md`). Runtime references use the game's Unity.TextMeshPro assembly.
The bundled UI uses a MapleStory SDF atlas. The project includes Liberation Sans
as the unmodified TMP fallback resource; its SIL Open Font License 1.1 and
Google/Red Hat copyright notice are preserved in `LIBERATION-SANS-OFL.txt`.
