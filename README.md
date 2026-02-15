# DaroEngine2

**Real-time broadcast graphics engine for live production**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-lightgrey.svg)]()
[![DirectX 11](https://img.shields.io/badge/DirectX-11-green.svg)]()

---

## What is DaroEngine2?

DaroEngine2 is an open-source real-time graphics engine built for broadcast and live production workflows. It combines a native C++ DirectX 11 rendering engine with a C# WPF designer application, allowing you to create animated graphics, build reusable templates, manage playlists, and send the final output to OBS (or any Spout-compatible application) at 1920x1080 @ 50 FPS.

Think of it as an open-source alternative to commercial broadcast graphics systems — design your lower thirds, titles, and overlays in the visual editor, then play them out live.

<!-- screenshots here -->

---

## Key Features

- **Visual Designer** — WYSIWYG editor with timeline, keyframe animation, and real-time preview
- **Layer System** — Rectangles, circles, text, images, video, masks, and groups (up to 64 layers)
- **Template System** — Create reusable templates with fill forms and named property bindings (transfunctioners)
- **Playout Engine** — Playlist management with cue/take/clear workflow, auto-advance, and looping
- **Spout Output** — Send rendered frames to OBS, vMix, CasparCG, or any Spout receiver
- **Video Support** — MP4, MOV, ProRes 4444, Apple Animation with alpha channel support
- **Mosart Automation** — TCP command interface for newsroom automation integration
- **REST API Middleware** — ASP.NET Core service for external system integration (Octopus NRCS, etc.)
- **Edge Antialiasing** — Shader-based smoothing with configurable quality levels
- **3D Transforms** — Full rotation on all axes with anchor point control

---

## Quick Start

### Prerequisites

- **Windows 10/11** (x64)
- **Visual Studio 2022** or later with:
  - C++ Desktop Development workload
  - .NET Desktop Development workload
- **.NET 10 SDK**
- **DirectX 11 compatible GPU**

### Build

```bash
# Clone the repository
git clone https://github.com/TN000/DaroEngine2.git
cd DaroEngine2

# Build the full solution
msbuild DaroEngine2.slnx /p:Configuration=Debug /p:Platform=x64
```

**Output:**
- Engine DLL: `bin/Debug/DaroEngine.dll`
- Designer App: `Designer/bin/Debug/net10.0-windows/DaroDesigner.exe`

### First Launch

1. Run `DaroDesigner.exe`
2. File > New Scene to create a new project
3. Add layers using the layer panel
4. Animate with the timeline at the bottom
5. Switch to **PLAYOUT** tab for live playback

---

## How It Works

```
┌─────────────────────────────────────────────────────────┐
│                    DaroDesigner (WPF)                    │
│                                                         │
│  ┌──────────┐   ┌──────────┐   ┌────────────────────┐  │
│  │  Design   │──>│ Template │──>│      Playout       │  │
│  │  Scene    │   │  Fill    │   │  Cue > Take > Air  │  │
│  └──────────┘   └──────────┘   └─────────┬──────────┘  │
│                                           │              │
└───────────────────────────────────────────┼──────────────┘
                                            │ P/Invoke
                                ┌───────────▼──────────┐
                                │  DaroEngine.dll      │
                                │  (C++ / DirectX 11)  │
                                └───────────┬──────────┘
                                            │ Spout
                                ┌───────────▼──────────┐
                                │  OBS / vMix / etc.   │
                                └──────────────────────┘
```

### Workflow

1. **Create a Scene** — Add layers (text, images, shapes, video) and animate them on the timeline
2. **Build a Template** — Design a fill form that maps input fields to scene properties
3. **Fill & Add to Playlist** — Load a template, fill in the fields, add to the playout playlist
4. **Take On Air** — Cue the next item and take it live
5. **Spout to OBS** — The rendered output streams to OBS via Spout in real time

---

## Getting Output into OBS

1. Install the [Spout plugin for OBS](https://github.com/Off-World-Live/obs-spout2-plugin)
2. In DaroDesigner, go to the **PLAYOUT** tab
3. Check **Enable Spout** and set a sender name (default: `DaroPlayout`)
4. In OBS, add a **Spout2 Capture** source
5. Select the sender name from the dropdown
6. Your graphics are now live in OBS with full alpha transparency

---

## Architecture

| Component | Technology | Description |
|-----------|-----------|-------------|
| **DaroEngine** | C++ / DirectX 11 | Native rendering engine compiled as a DLL. Handles all GPU rendering, text layout (DirectWrite), video decoding (Media Foundation + FFmpeg), and Spout output. |
| **DaroDesigner** | C# / WPF | Visual designer application. Scene editor, timeline, template maker, and playout engine. Communicates with the engine via P/Invoke. |
| **GraphicsMiddleware** | ASP.NET Core | REST API service with SQLite database. Bridges external systems (newsroom, automation) with the playout engine. |

### Engine API

The engine exports a C API with `Daro_*` prefix and `__stdcall` calling convention. Key functions:

- **Lifecycle:** `Daro_Initialize()`, `Daro_Shutdown()`
- **Rendering:** `Daro_BeginFrame()`, `Daro_EndFrame()`, `Daro_Present()`
- **Layers:** `Daro_SetLayerCount()`, `Daro_UpdateLayer()`, `Daro_ClearLayers()`
- **Playback:** `Daro_Play()`, `Daro_Stop()`, `Daro_SeekToFrame()`
- **Textures:** `Daro_LoadTexture()`, `Daro_UnloadTexture()`
- **Spout:** `Daro_EnableSpoutOutput()`, `Daro_ConnectSpoutReceiver()`
- **Frame Buffer:** `Daro_LockFrameBuffer()`, `Daro_UnlockFrameBuffer()`

See `Engine/DaroEngine.h` for the full API reference.

---

## Mosart / Automation Integration

DaroDesigner includes a TCP server (port 5555) for newsroom automation integration:

```
Octopus NRCS ──> GraphicsMiddleware (REST :5000) ──> Mosart ──> DaroDesigner (TCP :5555)
```

Commands: `CUE`, `PLAY`, `STOP`, `PAUSE`, `CONTINUE` — see [CONTRIBUTING.md](CONTRIBUTING.md) for protocol details.

---

## Roadmap

Community contributions welcome in any of these areas:

- [ ] Multi-resolution support (4K, custom resolutions)
- [ ] NDI output (alternative to Spout)
- [ ] GPU-accelerated text effects (drop shadows, outlines, gradients)
- [ ] Plugin system for custom layer types
- [ ] Web-based remote control panel
- [ ] Linux / cross-platform rendering backend (Vulkan)
- [ ] Audio support
- [ ] Ticker / crawl layer type
- [ ] Clock / countdown layer type
- [ ] Data binding (RSS feeds, APIs, spreadsheets)
- [ ] Multi-language UI

Have an idea? [Open an issue](../../issues) to discuss it.

---

## Contributing

We welcome contributions! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on setting up your development environment, code style, and how to submit pull requests.

---

## License

This project is licensed under the MIT License — see [LICENSE](LICENSE) for details.
