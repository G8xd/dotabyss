# Third-party software & content

This project **uses** the software listed below but does **not** redistribute it.
Nothing here is vendored into the repository; you download each item
yourself during setup (see the root `README.md`). Every item is governed by its
own license, not by this project's MIT license.

This project also ships **no game content**. The game *ドットアビス X (Dot Abyss X)*
and all of its assets are copyright their respective owners; you must own the game
and supply your own copy.

---

## Runtime / SDK (Unity side)

| Component | Purpose | License | Where to get it |
|---|---|---|---|
| **Live2D Cubism SDK for Unity** (`5-r.5`) | Renders the Live2D models (`.moc3`) and animations in the player. Includes the proprietary **Live2D Cubism Core** native binaries. | **Live2D**: Cubism Components under the *Live2D Open Software License*; **Cubism Core** under the *Live2D Proprietary Software License*. Business users (>10M JPY/yr revenue) also need a *Cubism SDK Release License*. | <https://www.live2d.com/en/sdk/download/unity/> |
| **Unity** `6000.3.8f1` (URP) | Engine for the player/build. | Unity Software License (per your Unity plan). | <https://unity.com/releases/editor/archive> |

> **Why it isn't vendored:** the Live2D Cubism Core is proprietary and its license
> does not permit republishing the SDK in a third-party source repository. Download
> it from Live2D (accepting their EULA) and drop it into `unity/Assets/Live2D/Cubism/`.

## Datamining / build tools (`tools/bin/`, fetched by you)

| Tool | Version | Purpose | License | Where to get it |
|---|---|---|---|---|
| **AssetRipper** (GUI Free) | `1.3.14` | Headless export of ripped prefabs / AnimatorControllers / clips (`da rip`). | See project (free edition). | <https://github.com/AssetRipper/AssetRipper/releases/tag/1.3.14> |
| **Cpp2IL** | `2022.1.0-pre-release.21` | IL2CPP → C# dump of the game code, for reverse-engineering behavior (`da dump`). | LGPL-3.0. | <https://github.com/SamboyCoding/Cpp2IL/releases/tag/2022.1.0-pre-release.21> |
| **vgmstream** (`vgmstream-cli`) | `r2117` | Decodes game audio streams. | Mixed (see `vgmstream/COPYING`); some bundled codec libraries have their own terms. | <https://github.com/vgmstream/vgmstream> |
| **AssetStudioMod** (`AssetStudioModCLI`, net8) | `0.19.0` | Optional/legacy alternative export path (the pipeline now uses `da live2d`/`da rip`). **Bundles FMOD** (`fmod.dll`), proprietary and **not freely redistributable** — another reason it is not vendored here. Needs the .NET 8 runtime. | AssetStudioMod: MIT-style; **FMOD: proprietary (Firelight Technologies)**. | <https://github.com/aelurum/AssetStudio/releases/tag/v0.19.0> · FMOD: <https://www.fmod.com/> |

## Python dependencies (`tools/requirements.txt` / `pyproject.toml`, installed via `uv`)

| Package | Purpose | License |
|---|---|---|
| **UnityPy** | Read Unity AssetBundles (images, text, ACB, Live2D). | MIT |
| **imageio-ffmpeg** | Bundled ffmpeg for HCA→OGG audio decode/encode. | BSD-2-Clause (wrapper); ffmpeg is LGPL/GPL. |
| **wannacri** | Optional USM video demux. | MIT |

## Tooling (dev only)

| Tool | Purpose | License |
|---|---|---|
| **uv** | Python environment & dependency manager. | Apache-2.0 / MIT |
| **ruff** | Python linter + formatter. | MIT |
| **ty** | Python type checker. | MIT |

---

### Notes

* Exact tool versions used are pinned in `README.md` / `tools/fetch_tools.py`. Update
  these tables and the pins together when you bump a dependency.
* If you believe any content in this repository infringes your rights, please open an
  issue; the maintainers will respond promptly.
