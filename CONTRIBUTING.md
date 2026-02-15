# Contributing to DaroEngine2

Thanks for your interest in contributing to DaroEngine2! This document covers everything you need to get started.

## Development Environment Setup

### Prerequisites

- **Windows 10/11** (x64)
- **Visual Studio 2022** or later with:
  - **C++ Desktop Development** workload (for the engine)
  - **.NET Desktop Development** workload (for the designer)
- **.NET 10 SDK**
- **DirectX 11** compatible GPU

### Building

```bash
# Build the full solution
msbuild DaroEngine2.slnx /p:Configuration=Debug /p:Platform=x64
```

The Designer's post-build step automatically copies `DaroEngine.dll` to its output folder.

### Optional: FFmpeg Support

For ProRes 4444 and Apple Animation video decoding with alpha:

```powershell
.\setup-ffmpeg.ps1
```

This downloads FFmpeg libraries to `ThirdParty/ffmpeg/`. The engine auto-detects FFmpeg at build time via `__has_include`.

---

## Code Style

### C++ (Engine)

- **Exported functions:** `Daro_` prefix with `__stdcall` convention
- **Classes:** PascalCase (`Renderer`, `VideoPlayer`)
- **Member variables:** `m_` prefix (`m_Device`, `m_Context`)
- **Global variables:** `g_` prefix (`g_Renderer`)
- **Constants:** `DARO_` prefix, UPPER_SNAKE_CASE (`DARO_MAX_LAYERS`)
- **Indentation:** Tabs

### C# (Designer)

- **Private fields:** `_` prefix (`_isRunning`, `_engineRenderer`)
- **Namespace:** `DaroDesigner`
- **Methods/Properties:** PascalCase
- **Local variables:** camelCase
- **Indentation:** 4 spaces

### General

- Keep changes focused — one logical change per PR
- Match the style of surrounding code
- Use `CultureInfo.InvariantCulture` for all numeric parsing/formatting
- P/Invoke structs must match C++ layout exactly (`Pack = 1`, `CharSet.Unicode`)

---

## Project Structure

```
DaroEngine2/
├── Engine/                  # C++ DirectX 11 rendering engine
│   ├── DaroEngine.h         # Public API declarations
│   ├── SharedTypes.h         # DaroLayer struct and constants
│   ├── Renderer.cpp          # DirectX 11 rendering
│   ├── VideoPlayer.cpp       # Video decoding (MF + FFmpeg)
│   └── FrameBuffer.cpp       # Shared memory for preview
├── Designer/                 # C# WPF application
│   ├── MainWindow.xaml.cs    # Main UI
│   ├── Engine/
│   │   ├── EngineInterop.cs  # P/Invoke declarations
│   │   └── EngineRenderer.cs # Thread-safe engine wrapper
│   └── Models/
│       ├── LayerModel.cs     # Layer properties and animation
│       ├── ProjectModel.cs   # Scene serialization
│       └── AnimationModel.cs # Keyframe animation
├── GraphicsMiddleware/       # ASP.NET Core REST API
└── ThirdParty/               # External dependencies
```

---

## How to Submit a Pull Request

1. **Fork** the repository
2. **Create a branch** from `main` for your change:
   ```bash
   git checkout -b feature/my-feature
   ```
3. **Make your changes** — keep commits focused and well-described
4. **Test your changes:**
   - Build succeeds: `msbuild DaroEngine2.slnx /p:Configuration=Debug /p:Platform=x64`
   - Designer launches and renders preview correctly
   - If you changed the engine API, verify P/Invoke declarations still match
5. **Push** and open a pull request against `main`
6. **Describe** what you changed and why in the PR description

---

## Areas Where Help Is Needed

Looking for something to work on? Here are areas where contributions would be especially valuable:

- **Testing** — Manual testing on different GPU hardware and Windows versions
- **Documentation** — Tutorials, video guides, example templates
- **NDI Output** — Adding NDI as an alternative to Spout
- **Cross-platform** — Investigating Vulkan backend for Linux support
- **Performance** — GPU profiling and optimization
- **Accessibility** — Keyboard navigation and screen reader support in the Designer

Check the [issues](../../issues) for more specific tasks.

---

## Reporting Bugs

When filing a bug report, please include:

- Windows version and GPU model
- Steps to reproduce
- Expected vs actual behavior
- Relevant log output (check `DaroDesigner.log`)
- Screenshot or screen recording if it's a visual issue

---

## Mosart TCP Protocol Reference

For automation integration development, here's the command protocol:

```
Format: GUID|COMMAND\r\n

Commands:
  0 = CUE      → OK:CUED:{takes}:{name}
  1 = PLAY     → OK:PLAYING:{name}
  2 = STOP     → OK:STOPPED
  3 = CONTINUE → OK:PLAYING
  4 = PAUSE    → OK:PAUSED

Error: ERROR:{message}
```

Port: 5555 (configurable via `AppConstants.MosartPort`)

---

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
