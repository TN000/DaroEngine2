# DaroEngine2 - Audit Report & Fix Tracker (v2)

**Datum:** 2026-02-07
**Stav:** Vsechny 7 vln dokonceno (44 oprav)
**Build:** 0 errors, 0 warnings (full rebuild verified)

---

## Souhrn

Kompletni audit broadcast-ready real-time grafickeho engine. Celkem identifikovano **~58 nalezu** across C++ engine, C# designer, interop, models, UI, build config. Opravy implementovany v 7 vlnach (44 oprav hotovo). Zbylych 4 nalezy jsou feature requesty nebo omezeni treti strany.

---

## Wave 1 - Broadcast-blocking (HOTOVO)

### C3+C6: VideoPlayer mutex deadlocky [CRITICAL] [OPRAVENO]
- **Soubory:** `Engine/VideoPlayer.cpp`, `Engine/VideoPlayer.h`
- **Problem:** `LoadVideo()` drzi `m_Mutex` a vola `UnloadVideo()` ktery znovu zamyka `m_Mutex` → deadlock. Stejne `UpdateFrame()` → `SeekToFrame(0)` pri loop.
- **Oprava:** Extrahovany `UnloadVideoInternal()` a `SeekToFrameInternal()` bez zamku. Verejne metody zamykaji a delegují na interni verze.

### C1: Texture cache nekonecna smycka [CRITICAL] [OPRAVENO]
- **Soubor:** `Designer/Engine/EngineRenderer.cs` (radek ~535)
- **Problem:** `while (_textureCache.Count >= MaxTextureCacheSize)` vola `EvictOldestTexture()`. Pokud vsechny entries maji RefCount > 0, evikce nic neodstrani a smycka bezi navzdy.
- **Oprava:** Po kazdem volani EvictOldestTexture kontrola, zda se count snizil. Pokud ne, break.

### C2: Render thread 100ms join timeout [CRITICAL] [OPRAVENO]
- **Soubor:** `Designer/Engine/EngineRenderer.cs` (metoda `Stop()`)
- **Problem:** `_renderThread.Join(100)` - pokud thread neskonci za 100ms, pokracuje se dale a Shutdown() muze zavolat `Daro_Shutdown()` zatimco render thread jeste bezi → use-after-free.
- **Oprava:** Zvyseno na `Join(5000)` s logem pri prekroceni.

### C7: g_Renderer race condition [CRITICAL] [OPRAVENO]
- **Soubor:** `Engine/DaroEngine.cpp`
- **Problem:** `Daro_BeginFrame()`, `Daro_Render()`, `Daro_Present()` pristupuji k `g_Renderer` bez zamku. Mezitim muze jiny thread zavolat `Daro_Shutdown()` a resetovat `g_Renderer` → use-after-free.
- **Oprava:**
  - `Daro_BeginFrame()`: pridany `lock(g_Mutex)` + null-check `g_Renderer`
  - `Daro_Render()`: null-check `g_Renderer` pod zamkem pred kopirovani vrstev; null-check `g_FrameBuffer` pred Write
  - `Daro_Present()`: pridany null-check `g_Renderer`

### C5: async void save pri Window_Closing [CRITICAL] [OPRAVENO]
- **Soubor:** `Designer/MainWindow.xaml.cs` (metoda `Window_Closing`)
- **Problem:** `Menu_SaveProject(this, null)` je `async void` - fire-and-forget. Okno se zavre pred dokoncenim ukladani → ztrata dat.
- **Oprava:** Nahrazeno synchronnim `SaveProjectAsync(...).GetAwaiter().GetResult()` s try-catch a SaveFileDialog pro novy projekt.

### H3: _isUpdatingUI bez try-finally [HIGH] [OPRAVENO]
- **Soubor:** `Designer/MainWindow.xaml.cs`
- **Problem:** `_isUpdatingUI = true` ... kod ... `_isUpdatingUI = false` - pokud cokoliv mezi throwne, `_isUpdatingUI` zustane `true` a vsechny property change handlery jsou permanentne deaktivovany.
- **Oprava:** `UpdatePropertiesUI()` - cely blok zabalen do try-finally. Podobne `ColorHex_Changed`, `ColorComponent_Changed`, `ColorPicker_Click`, property size-changed position update.

### H5: SendLayersToEngine bez try-catch [HIGH] [OPRAVENO]
- **Soubor:** `Designer/MainWindow.xaml.cs` (metoda `SendLayersToEngine`)
- **Problem:** Volano ~40x z UI event handleru. Pokud throwne (napr. z `EnsureLayerResources` nebo `ToNative`), crashne handler a potencialne app.
- **Oprava:** Cely telo metody zabaleno do try-catch s `Logger.Error`.

### H8: float.TryParse bez CultureInfo [HIGH] [OPRAVENO]
- **Soubor:** `Designer/MainWindow.xaml.cs` (~15 mist)
- **Problem:** `float.TryParse(text, out float value)` pouziva default culture. Na ceskem systemu "1.5" se neparsuje (ocekava se "1,5"). Nekonzistence mezi display a parse.
- **Oprava:** Vsechny `float.TryParse` pouzivaji `NumberStyles.Float, CultureInfo.InvariantCulture`. Vsechny odpovidajici `ToString` pouzivaji `CultureInfo.InvariantCulture`.

### H7/C8: VideoManager::Shutdown race [HIGH] [OPRAVENO]
- **Soubor:** `Engine/VideoPlayer.cpp`
- **Problem:** `Shutdown()` vola `m_Players.clear()` bez drzeni `m_ManagerMutex`. `GetPlayer()` nebo `UpdateAll()` muze soucasne pristupovat k mapě.
- **Oprava:** `m_Players.clear()` zabaleno do `lock_guard<mutex>(m_ManagerMutex)`.

### H13: Daro_LockFrameBuffer null params [HIGH] [OPRAVENO]
- **Soubor:** `Engine/DaroEngine.cpp`
- **Problem:** Zadny null-check na vystupni parametry `ppData`, `pWidth`, `pHeight`, `pStride`. Null pointer dereference crash.
- **Oprava:** Pridana kontrola `!ppData || !pWidth || !pHeight || !pStride` pred volanim Lock().

---

## Wave 2 - Reliability (HOTOVO)

### C9: Non-atomic file writes [CRITICAL] [OPRAVENO]
- **Soubory:** `Designer/MainWindow.xaml.cs` (`SaveProjectAsync`), `Designer/Services/AutosaveService.cs`
- **Problem:** `File.WriteAllTextAsync(filePath, json)` - pokud dojde k padu/vypadku proudu behem zapisu, soubor je poskozeny a data ztracena.
- **Oprava:** Write to `.tmp` soubor, pak `File.Move(temp, target, overwrite: true)`. Atomicka operace na NTFS.

### C4: GPU device lost detection [CRITICAL] [OPRAVENO]
- **Soubory:** `Engine/Renderer.h`, `Engine/Renderer.cpp`, `Engine/DaroEngine.h`, `Engine/DaroEngine.cpp`, `Designer/Engine/EngineInterop.cs`, `Designer/Engine/EngineRenderer.cs`
- **Problem:** Zadna detekce `DXGI_ERROR_DEVICE_REMOVED`. GPU driver crash/update zpusobi tichy vypadek renderovani - broadcast bezi s cernym vystupem.
- **Oprava:**
  - `Renderer::CheckDeviceLost()` - vola `m_Device->GetDeviceRemovedReason()`, nastavuje `m_DeviceLost` flag
  - `BeginFrame()` kontroluje na zacatku kazdeho framu, pri detekci okamzite vraci
  - `Daro_IsDeviceLost()` C API export
  - P/Invoke `Daro_IsDeviceLost` v `EngineInterop.cs`
  - `EngineRenderer.PerformRenderOnThread()` kontroluje pred render cyklem, pri detekci zastavi render thread a zobrazi MessageBox

### H19: Layer ID collision po load [HIGH] [OPRAVENO]
- **Soubor:** `Designer/Models/LayerModel.cs`
- **Problem:** Staticky `_nextId` se neinkrementuje podle nactenych ID. Po nacteni projektu s layers ID 1, 5, 100 - novy layer dostane ID 4 → kolize s existujicim ID 5.
- **Oprava:** `SyncNextId(int loadedId)` - thread-safe (CAS loop) aktualizace `_nextId` na `max(current, loadedId + 1)`. Volano z `FromSerializable()` pred vytvorenim kazde vrstvy.

### H12/H18: Animation property validation bypass [HIGH] [OPRAVENO]
- **Soubor:** `Designer/Models/LayerModel.cs` (metody `ApplyAnimationAtFrame` a `SetPropertyValue`)
- **Problem:** Keyframe interpolace muze produkovat hodnoty mimo platny rozsah (napr. opacity 1.2, color -0.3). Tyto hodnoty jdou primo na GPU → vizualni artefakty na broadcastu.
- **Oprava:** Clamping:
  - `Opacity`: `Math.Clamp(value, 0f, 1f)`
  - `ColorR/G/B`: `Math.Clamp(value, 0f, 1f)`
  - `FontSize`: `Math.Max(value, 1f)`
  - `LineHeight`: `Math.Max(value, 0.1f)`

### H11: MosartServer concurrent write race [HIGH] [OPRAVENO]
- **Soubor:** `Designer/Services/MosartServer.cs`
- **Problem:** `BroadcastAsync()` (z libovolneho threadu) a `SendResponse()` (z client handler threadu) mohou soucasne zapisovat do stejneho `NetworkStream`. NetworkStream.Write neni thread-safe → corrupted data na wire.
- **Oprava:** `lock(stream)` kolem vsech `stream.Write` + `stream.Flush` v obou metodach. Pouziti samotneho stream objektu jako lock target (jednoduche, per-client granularita).

### H10: Global exception handler swallows all [HIGH] [OPRAVENO]
- **Soubor:** `Designer/App.xaml.cs`
- **Problem:** `e.Handled = true` na vsech vyjimkach vcetne fatalnich (OOM, SOE). App bezi v nedefinovanem stavu po fatalni chybe. Stack trace neni logovan v UnhandledException handleru.
- **Oprava:**
  - `OutOfMemoryException` a `StackOverflowException` propusteny (e.Handled = false → crash)
  - Plny stack trace logovan pro oba handlery
  - `IsTerminating` flag logovan v background exception handleru

### Bonus: Logger WARN flush [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/Services/Logger.cs`
- **Problem:** `AutoFlush = false` a flush pouze pro ERROR. WARN zpravy (casto predchazi chybam) se ztrati pri crashu.
- **Oprava:** Flush i pro WARN zpravy.

### Bonus: Autosave non-atomic writes [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/Services/AutosaveService.cs`
- **Problem:** Stejny problem jako C9 - primo zapis do ciloveho souboru.
- **Oprava:** Atomic write pattern: `.tmp` → `File.Move`.

---

## Wave 3 - Polish & Deployment (HOTOVO)

### H15: Spout sender name length validation [HIGH] [OPRAVENO]
- **Soubor:** `Engine/Renderer.cpp` (metoda `EnableSpout`)
- **Problem:** Zadna validace delky jmena Spout senderu. Prilis dlouhe jmeno muze zpusobit buffer overflow ve Spout knihovne (SpoutSenderNames pouziva fixed 256-char buffery).
- **Oprava:** Pridana kontrola `strlen(name) > 255` + null/empty check pred volanim `SetSenderName()`.

### H14: D2D EndDraw device-lost recovery [HIGH] [OPRAVENO]
- **Soubory:** `Engine/Renderer.h`, `Engine/Renderer.cpp`
- **Problem:** `EndDraw()` muze vratit `D2DERR_RECREATE_TARGET` pri device loss, ale kod pouze logoval a pokracoval. D2D render target zustal v nevalidnim stavu → tichy vypadek text/shape renderovani.
- **Oprava:**
  - Nova metoda `RecreateD2DTarget()` - uvolni D2D render target + vsechny cached brushes/formaty, znovu vytvori z DXGI surface
  - 3 mista (RenderCircle, RenderText, RenderBoundingBox): `D2DERR_RECREATE_TARGET` → volani `RecreateD2DTarget()`

### H17: CoInitializeEx pro Media Foundation [HIGH] [OPRAVENO]
- **Soubor:** `Engine/DaroEngine.cpp`
- **Problem:** Media Foundation vyzaduje COM inicializaci. `CoInitializeEx` nebylo volano → MF muze selhat v nekterych kontextech.
- **Oprava:** `CoInitializeEx(nullptr, COINIT_MULTITHREADED)` v `Daro_Initialize()`, `CoUninitialize()` v `Daro_Shutdown()`.

### M15: Stale state on project load [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/MainWindow.xaml.cs` (metoda `LoadProjectAsync`)
- **Problem:** Pri nacteni noveho projektu se nezastavil playback a neresetnul timeline. Playback timer bezel dal s novym projektem → neocekavane chovani.
- **Oprava:** Stop playback timeru a reset UI tlacitka pred loadem. Reset `CurrentFrame = 0` a `IsPlaying = false` po deserializaci.

### M16+M17: FrameBuffer shared memory security [MEDIUM] [OPRAVENO]
- **Soubory:** `Engine/FrameBuffer.h`, `Engine/FrameBuffer.cpp`
- **Problem:** (1) `CreateFileMappingW` bez security descriptoru - libovolny proces muze pripojit. (2) Hardcoded jmeno "DaroFrameBuffer" - kolize mezi instancemi.
- **Oprava:**
  - Jmeno nyni `DaroFrameBuffer_<PID>` (per-process unikatni)
  - SDDL security descriptor `D:(A;;GA;;;CO)` - pristup pouze pro Creator Owner
  - Fallback na default descriptor pokud SDDL selze

### M18: Template/scene path traversal audit [MEDIUM] [OPRAVENO]
- **Soubory:** `Designer/PlayoutWindow.xaml.cs`, `Designer/Services/PlaylistDatabase.cs`
- **Problem:** Nektera mista ctou soubory z cest ziskanych z databaze nebo prichozich prikazu bez validace.
- **Oprava:**
  - `PlayoutWindow.xaml.cs`: pridana `PathValidator.IsPathAllowed()` kontrola pred pristupem k `storedItem.LinkedScenePath`
  - `PlaylistDatabase.cs`: refaktorovana privatni `IsPathAllowed()` aby delegovala na centralizovany `PathValidator` s fallbackem na vlastni allowed paths

### B1: Engine build hardening (Release) [BUILD] [OPRAVENO]
- **Soubor:** `Engine/DaroEngine.vcxproj`
- **Problem:** Release config nemela optimalizace, /GUARD:CF, ani vyssi warning level.
- **Oprava:**
  - Warning level: Level3 → Level4
  - Optimization: MaxSpeed (/O2)
  - Control Flow Guard: enabled
  - Link SetChecksum: true (PE checksum pro integrity verification)

### B2: Assembly version numbering [BUILD] [OPRAVENO]
- **Soubory:** `Designer/Designer.csproj`, `Engine/DaroEngine.rc` (novy)
- **Problem:** Zadne verze v assemblies/DLL. Nelze sledovat deployed buildy.
- **Oprava:**
  - Designer: Version/AssemblyVersion/FileVersion 2.0.0 v csproj
  - Engine: novy resource file `DaroEngine.rc` s VERSIONINFO (2.0.0.0), pripojeny do vcxproj

## Wave 4 - Hardening & Cleanup (HOTOVO)

### H16: Spout receiver name length validation [HIGH] [OPRAVENO]
- **Soubor:** `Engine/Renderer.cpp` (metoda `ConnectSpoutReceiver`)
- **Problem:** Stejny problem jako sender (#59) - zadna validace delky jmena Spout receiveru. Empty string nebo prilis dlouhe jmeno → buffer overflow ve Spout knihovne.
- **Oprava:** Pridana kontrola `senderName[0] == '\0'` (empty) a `strlen > 255` pred volanim `spout.CreateReceiver()`.

### H20: VideoPlayer AddRef na raw D3D pointers [HIGH] [OPRAVENO]
- **Soubory:** `Engine/VideoPlayer.cpp`
- **Problem:** `VideoPlayer::Initialize()` a `VideoManager::Initialize()` ukladaji raw `ID3D11Device*` a `ID3D11DeviceContext*` bez volani `AddRef()`. Pokud Renderer uvolni device drive, VideoPlayer ma dangling pointer → crash.
- **Oprava:**
  - `VideoPlayer::Initialize()`: `m_Device->AddRef()`, `m_Context->AddRef()`
  - `VideoPlayer::Shutdown()`: `m_Context->Release()`, `m_Device->Release()` pred nullovanim
  - `VideoManager::Initialize()`: AddRef pro oba, s Release na failure path (MFStartup)
  - `VideoManager::Shutdown()`: Release pred nullovanim

### M20: Context menu event handler leak [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/MainWindow.xaml.cs`
- **Problem:** `Keyframe_RightClick` a `TextKeyframe_RightClick` vytvarely novy `ContextMenu` s novymi `MenuItem` a subscribovaly Click handlery pri kazdem right-clicku. Handlery se nikdy neodhlasily → leak.
- **Oprava:** Pridany `menu.Closed` event handler ktery odhlasi vsechny Click handlery (SetKey_Click, DeleteKey_Click, CreateTransfunctioner_Click).

### M21: GetSpoutReceiverId thread safety [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/Engine/EngineRenderer.cs` (metoda `GetSpoutReceiverId`)
- **Problem:** Cteni `_spoutReceiverCache` dictionary bez zamku. Jiny thread muze soucasne modifikovat cache → `InvalidOperationException`.
- **Oprava:** Zabalen do `lock (_spoutCacheLock)` (stejny zamek jako ostatni cache operace).

### M22: GC.SuppressFinalize chybi v Dispose [MEDIUM] [OPRAVENO]
- **Soubory:** `Designer/Engine/EngineRenderer.cs`, `Designer/Services/AutosaveService.cs`
- **Problem:** `Dispose()` bez `GC.SuppressFinalize(this)` - GC zbytecne finalizuje uz disposed objekty. Potencialne double-dispose nebo pristup k uvolnenym zdrojum.
- **Oprava:** Pridano `GC.SuppressFinalize(this)` do obou `Dispose()` metod.

### M23: Engine ID overflow protection [MEDIUM] [OPRAVENO]
- **Soubor:** `Engine/Renderer.cpp`, `Engine/VideoPlayer.cpp`
- **Problem:** `m_NextTextureId`, `m_NextReceiverId`, `m_NextVideoId` inkrementovany bez kontroly preteceni. Po ~2 milionech operaci `int` overflow do zapornych cisel → neplatne ID.
- **Oprava:** Pattern `if (id <= 0) m_NextXxxId = id = 1;` pro vsechny tri ID generatory.

### M24: Debug.WriteLine v render thread [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/Engine/EngineRenderer.cs` (metoda `RenderThreadProc`)
- **Problem:** Catch blok v render threadu pouzival `Debug.WriteLine` ktery je stripnuty v Release buildech. Chyby render threadu v produkci jsou neviditelne.
- **Oprava:** Nahrazeno `Logger.Error()` ktery funguje i v Release buildech.

## Wave 5 - Defensive Hardening (HOTOVO)

### H25: g_LayerToMasks race condition [HIGH] [OPRAVENO]
- **Soubor:** `Engine/DaroEngine.cpp`
- **Problem:** `g_LayerToMasks` deklarovano jako globalni `static`, ale pristupovano pouze z `Daro_Render()` bez zamku. Komentar rikl "thread-local data" ale mapa nebyla thread-local. Pri hypotetickém multi-thread volani → data corruption.
- **Oprava:** Presunuto z globalni promenne na `static thread_local` uvnitr `Daro_Render()`. Odpovida puvodnimu zameru a je thread-safe.

### H26: Missing m_D2DFactory null check [HIGH] [OPRAVENO]
- **Soubor:** `Engine/Renderer.cpp` (metody `RenderText`, `RenderBoundingBox`)
- **Problem:** `RenderText` kontroluje `m_D2DRenderTarget` a `m_DWriteFactory`, ale ne `m_D2DFactory`. Na radcich 908 a 941 se vola `m_D2DFactory->CreateEllipseGeometry/CreatePathGeometry` bez null-check. Pokud D2D inicializace castecne selhala → null dereference crash.
- **Oprava:** Pridano `!m_D2DFactory` do vstupni kontroly obou metod.

### M25: MapStaging output param null check [MEDIUM] [OPRAVENO]
- **Soubor:** `Engine/Renderer.cpp` (metoda `MapStaging`)
- **Problem:** `ppData` a `pRowPitch` dereferencovany bez null-check. Takze pridana i kontrola `m_Context`.
- **Oprava:** Pridano `if (!ppData || !pRowPitch || !m_Context) return false;` na zacatek metody.

### M26: VideoPlayer TotalFrames int overflow [MEDIUM] [OPRAVENO]
- **Soubor:** `Engine/VideoPlayer.cpp` (metoda `LoadVideo`)
- **Problem:** `static_cast<int>(m_Duration * m_FrameRate)` muze pretect pro velmi dlouha videa (hodiny). Undefined behavior pri int overflow.
- **Oprava:** Clamp pres `double` kontrolu: `(totalFramesD > INT_MAX) ? INT_MAX : static_cast<int>(totalFramesD)`.

### M27: WARP software renderer fallback not logged [MEDIUM] [OPRAVENO]
- **Soubor:** `Engine/Renderer.cpp` (metoda `CreateDevice`)
- **Problem:** Pokud hardware D3D11 device creation selze a fallback na WARP software renderer se pouzije, zadny log output. Uzivatel nevi ze bezi na software rendereru s degradovanym vykonem.
- **Oprava:** `OutputDebugStringA` pri HW selhani a pri WARP uspesnem fallbacku s WARNING tagem.

### M28: float.TryParse bez CultureInfo v TransfunctionerBindingModel [MEDIUM] [OPRAVENO]
- **Soubor:** `Designer/Models/TransfunctionerBindingModel.cs`
- **Problem:** `float.TryParse(value, out float floatVal)` na radku 176 pouziva default culture. Na ceskem systemu "1.5" se neparsuje. Stejne `ToString("F2")` na radku 165 produkuje culture-specificke decimalni oddelovace.
- **Oprava:** `float.TryParse` s `NumberStyles.Float, CultureInfo.InvariantCulture`. `ToString("F2", CultureInfo.InvariantCulture)`.

## Wave 6 - Mosart Command Serialization (HOTOVO)

### H29: Mosart command lock neserializuje plnou operaci [HIGH] [OPRAVENO]
- **Soubor:** `Designer/PlayoutWindow.xaml.cs` (Mosart integration region)
- **Problem:** `HandleMosartCommandAsync` drzelo `_mosartCommandLock`, ale pro CUE a PLAY prikazy synchronni handler `HandleMosartCommand` uvnitr `Dispatcher.InvokeAsync` spustil `Task.Run(async () => ...)` - fire-and-forget. Lock se uvolnil jakmile synchronni handler vratil, ale skutecna prace (DB lookup + UI dispatch) jeste bezela na pozadi. Dva rychle za sebou poslane CUE/PLAY prikazy z Mosartu mohly racovat:
  1. Oba postupne ziskaji lock (prvni ho uvolni pred dokoncenim)
  2. Oba spusti paralelni Task.Run
  3. Race na pridavani do playlistu a cuovani
  V broadcast prostredi je toto kriticke - Mosart posila prikazy rychle v sekvenci.
- **Oprava:** Kompletni restrukturace Mosart command handling:
  - Odstraneny fire-and-forget `Task.Run()` uvnitr synchronnich handleru
  - `HandleMosartCue` → `HandleMosartCueAsync` (plne async, awaitable)
  - `HandleMosartPlay` → `HandleMosartPlayAsync` (plne async, awaitable)
  - `HandleMosartCommandAsync` nyni primo `await`-uje kazdy handler
  - Lock se drzi po CELOU dobu operace vcetne DB lookupu a UI dispatche
  - STOP a PAUSE zustavaji synchronni (jen UI operace, zadne I/O)
  - `HandleMosartCommand` (synchronni dispatcni metoda) odebrána - logika presunuta primo do `HandleMosartCommandAsync`

## Wave 7 - Video Sync & Deployment (HOTOVO)

### A1: Video-engine timeline desync [ARCHITECTURAL] [OPRAVENO]
- **Soubory:** `Designer/Engine/EngineRenderer.cs`, `Designer/MainWindow.xaml.cs`, `Designer/PlayoutEngineWindow.xaml.cs`
- **Problem:** Video vrstvy se prehravaly nezavisle na timeline Designeru. `Daro_SeekVideo()` existovalo v C API i P/Invoke, ale nikdy se nevolalo. VideoPlayer pouzival vlastni wall-clock timing (QueryPerformanceCounter), Designer pouzival CurrentFrame. Vysledek: pri scrubbovani timeline video zustalo na starem miste, pri prehravani mohlo driftovat.
- **Oprava:**
  - Nova metoda `EngineRenderer.SeekVideo(int videoId, int frame)` obalujici `Daro_SeekVideo`
  - Nova metoda `SyncVideoLayersToFrame(int frame)` v MainWindow - iteruje video vrstvy a vola SeekVideo
  - Volano z `OnFrameChanged()` po `SendLayersToEngine()` - kazdou zmenu framu synchronizuje video
  - PlayoutEngineWindow.RenderFrame: pridany SeekVideo volani po UpdateLayersBatch pro vsechny video vrstvy

### D1: global.json pro pin .NET SDK verze [DEPLOYMENT] [OPRAVENO]
- **Soubor:** `global.json` (novy)
- **Problem:** Zadny `global.json` - build pouzije jakykoliv nainstalovany .NET SDK. Riziko ze preview SDK produkuje neocekavane chovani.
- **Oprava:** `global.json` s `"version": "10.0.102"` a `"rollForward": "latestPatch"` - pin na stabilni .NET 10 s automatickym patchem.

### D2: Self-contained deployment profil [DEPLOYMENT] [OPRAVENO]
- **Soubor:** `Designer/Properties/PublishProfiles/win-x64.pubxml` (novy)
- **Problem:** Designer pouzival framework-dependent deployment. Na ciste masine potrebuje nainstalovanou .NET runtime.
- **Oprava:** Publish profil pro self-contained win-x64 deployment:
  - `SelfContained=true` - .NET runtime je soucasti balicku
  - `PublishSingleFile=true` - jeden EXE soubor
  - `PublishReadyToRun=true` - AOT pre-kompilace pro rychlejsi start
  - `EnableCompressionInSingleFile=true` - mensi vysledny soubor
  - Pouziti: `dotnet publish -p:PublishProfile=win-x64`

### D3: Staticke linkovani VC++ runtime [DEPLOYMENT] [OPRAVENO]
- **Soubor:** `Engine/DaroEngine.vcxproj`
- **Problem:** Release build pouzival dynamicke CRT (/MD, default). Na cilovych stanicich vyzadoval VC++ Redistributable instalaci.
- **Oprava:** Pridano `<RuntimeLibrary>MultiThreaded</RuntimeLibrary>` do Release ClCompile sekce. CRT je nyni staticky vlinkovan do DaroEngine.dll - zadna externi zavislost.

---

### Zbyvajici nalez (neimplementovatelne):

1. **Undo/Redo system** - feature request, ne bug
2. **Seek inaccuracy** - inherentni omezeni Media Foundation pro long-GOP kodeky
3. **MainWindow god class** (~3800 lines) - refactoring na MVVM (samostatny projekt)
4. **SpoutDX 67ms Sleep** na sender disconnect - v third-party kodu

---

## Soubory modifikovane v tomto auditu

### Wave 1:
- `Engine/VideoPlayer.h` - pridany interni metody deklarace
- `Engine/VideoPlayer.cpp` - UnloadVideoInternal, SeekToFrameInternal, Shutdown race fix
- `Engine/DaroEngine.cpp` - g_Renderer null-checks, BeginFrame lock, LockFrameBuffer param checks
- `Designer/Engine/EngineRenderer.cs` - texture cache loop fix, 5s join timeout
- `Designer/MainWindow.xaml.cs` - async save fix, try-finally, try-catch, CultureInfo

### Wave 2:
- `Engine/Renderer.h` - CheckDeviceLost, IsDeviceLost, m_DeviceLost
- `Engine/Renderer.cpp` - CheckDeviceLost implementace, BeginFrame guard
- `Engine/DaroEngine.h` - Daro_IsDeviceLost deklarace
- `Engine/DaroEngine.cpp` - Daro_IsDeviceLost implementace
- `Designer/Engine/EngineInterop.cs` - P/Invoke Daro_IsDeviceLost
- `Designer/Engine/EngineRenderer.cs` - device lost detection v render thread
- `Designer/Models/LayerModel.cs` - SyncNextId, value clamping
- `Designer/Services/MosartServer.cs` - lock(stream) around writes
- `Designer/Services/AutosaveService.cs` - atomic writes
- `Designer/Services/Logger.cs` - WARN flush
- `Designer/App.xaml.cs` - OOM/SOE passthrough, stack trace logging

### Wave 3:
- `Engine/Renderer.h` - RecreateD2DTarget deklarace
- `Engine/Renderer.cpp` - RecreateD2DTarget implementace, D2DERR_RECREATE_TARGET handling (3 mista), Spout name validation
- `Engine/DaroEngine.cpp` - CoInitializeEx/CoUninitialize
- `Engine/FrameBuffer.h` - PID-based shared memory name
- `Engine/FrameBuffer.cpp` - security descriptor, per-process name
- `Engine/DaroEngine.vcxproj` - /W4, /O2, /GUARD:CF, SetChecksum, ResourceCompile
- `Engine/DaroEngine.rc` - novy soubor, VERSIONINFO resource
- `Designer/Designer.csproj` - version numbering
- `Designer/MainWindow.xaml.cs` - stale state reset pri project load
- `Designer/PlayoutWindow.xaml.cs` - PathValidator kontrola pred scene load
- `Designer/Services/PlaylistDatabase.cs` - delegace na centralni PathValidator

### Wave 4:
- `Engine/Renderer.cpp` - Spout receiver name validation, texture/receiver ID overflow protection
- `Engine/VideoPlayer.cpp` - AddRef/Release pro D3D device/context (VideoPlayer + VideoManager), video ID overflow
- `Designer/Engine/EngineRenderer.cs` - GetSpoutReceiverId lock, GC.SuppressFinalize, Logger v catch
- `Designer/MainWindow.xaml.cs` - context menu Closed event handler cleanup
- `Designer/Services/AutosaveService.cs` - GC.SuppressFinalize

### Wave 5:
- `Engine/DaroEngine.cpp` - g_LayerToMasks → function-local thread_local
- `Engine/Renderer.cpp` - m_D2DFactory null checks (RenderText, RenderBoundingBox), MapStaging param validation, WARP fallback logging
- `Engine/VideoPlayer.cpp` - TotalFrames overflow clamp
- `Designer/Models/TransfunctionerBindingModel.cs` - CultureInfo pro float parse/format

### Wave 6:
- `Designer/PlayoutWindow.xaml.cs` - Mosart CUE/PLAY command serialization: HandleMosartCueAsync, HandleMosartPlayAsync, odstranen HandleMosartCommand

### Wave 7:
- `Designer/Engine/EngineRenderer.cs` - nova metoda SeekVideo()
- `Designer/MainWindow.xaml.cs` - SyncVideoLayersToFrame() volana z OnFrameChanged()
- `Designer/PlayoutEngineWindow.xaml.cs` - SeekVideo volani v RenderFrame()
- `global.json` - novy soubor, pin .NET SDK 10.0.102
- `Designer/Properties/PublishProfiles/win-x64.pubxml` - novy soubor, self-contained publish profil
- `Engine/DaroEngine.vcxproj` - RuntimeLibrary=MultiThreaded pro Release

---

## Celkovy souhrn

| Wave | Pocet oprav | Stav |
|------|------------|------|
| Wave 1 - Broadcast-blocking | 9 | HOTOVO |
| Wave 2 - Reliability | 9 | HOTOVO |
| Wave 3 - Polish & Deployment | 8 | HOTOVO |
| Wave 4 - Hardening & Cleanup | 7 | HOTOVO |
| Wave 5 - Defensive Hardening | 6 | HOTOVO |
| Wave 6 - Mosart Serialization | 1 | HOTOVO |
| Wave 7 - Video Sync & Deploy | 4 | HOTOVO |
| **Celkem** | **44** | **HOTOVO** |

**Build:** 0 errors, 0 warnings (full rebuild verified po kazde vlne)

---

## Build prikaz

```bash
MSYS_NO_PATHCONV=1 "/c/Program Files/Microsoft Visual Studio/18/Insiders/MSBuild/Current/Bin/amd64/MSBuild.exe" "D:\realtime\DaroEngine2\DaroEngine2.slnx" /p:Configuration=Debug /p:Platform=x64
```

Vystupy:
- Engine: `bin/Debug/DaroEngine.dll`
- Designer: `Designer/bin/Debug/net10.0-windows/DaroDesigner.dll`
