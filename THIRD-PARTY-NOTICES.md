# Third-party dependencies

- OrbitRender: GPL-3.0-only with its ADOFAI/Unity linking exception. Install the
  matching OrbitRender release separately. Source: https://github.com/KGH1113/OrbitRender.
- AdofaiIpc: MIT. Installed as a separate game mod. Source:
  https://github.com/KGH1113/adofai-ipc.
- Harmony: MIT. Provided by UnityModManager. https://github.com/pardeike/Harmony.
- Newtonsoft.Json: MIT. Provided by the game. https://github.com/JamesNK/Newtonsoft.Json.
- UnityModManager: MIT. https://github.com/newman55/unity-mod-manager.
- ImplDmNote overlay: GPL-3.0-only. The optional exporter uses the user's separate
  checkout; no overlay source is copied into TUFReplay. Source:
  https://github.com/KGH1113/ImplResourcePack.
- FFmpeg: executed as a separate process. The license depends on the exact build;
  x264-enabled and version3-enabled builds can be GPL-3.0-or-later. We do not bundle
  FFmpeg binaries. https://ffmpeg.org/legal.html.

Game, Unity, third-party mods, user fonts, images and recorded content are not
redistributed. If a release ever bundles a binary dependency, include its complete
license, exact build configuration and verified corresponding source first.
