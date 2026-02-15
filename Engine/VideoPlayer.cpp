// Engine/VideoPlayer.cpp
#include "VideoPlayer.h"
#include <Windows.h>
#include <map>
#include <memory>
#include <cstdio>

// File-based logger for video diagnostics (OutputDebugString not always visible)
void VideoLog(const char* msg)
{
    OutputDebugStringA(msg);

    // Also write to file next to DLL for easy viewing
    static FILE* sLogFile = nullptr;
    static bool sOpened = false;
    if (!sOpened)
    {
        sOpened = true;
        // Get DLL directory
        char dllPath[MAX_PATH] = {};
        HMODULE hMod = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCSTR)&VideoLog, &hMod);
        if (hMod) GetModuleFileNameA(hMod, dllPath, MAX_PATH);
        // Replace DLL name with log name
        char* lastSlash = strrchr(dllPath, '\\');
        if (lastSlash) strcpy_s(lastSlash + 1, MAX_PATH - (lastSlash - dllPath + 1), "DaroVideo.log");
        else strcpy_s(dllPath, "DaroVideo.log");
        fopen_s(&sLogFile, dllPath, "w");
        if (sLogFile)
        {
            fprintf(sLogFile, "[DaroVideo] Log started\n");
            fflush(sLogFile);
        }
    }
    if (sLogFile)
    {
        fputs(msg, sLogFile);
        fflush(sLogFile);
    }
}

// ============================================================================
// VideoPlayer Implementation
// ============================================================================

VideoPlayer::VideoPlayer()
{
    QueryPerformanceFrequency(&m_Frequency);
    QueryPerformanceCounter(&m_LastFrameTime);
}

VideoPlayer::~VideoPlayer()
{
    Shutdown();
}

bool VideoPlayer::Initialize(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (!device || !context) return false;

    m_Device = device;
    m_Context = context;

    // AddRef to prevent dangling pointers if Renderer releases while VideoPlayer is alive
    m_Device->AddRef();
    m_Context->AddRef();

    return true;
}

void VideoPlayer::Shutdown()
{
    UnloadVideo();

    // Release AddRef'd references
    if (m_Context) { m_Context->Release(); m_Context = nullptr; }
    if (m_Device) { m_Device->Release(); m_Device = nullptr; }
}

// Maximum file size for video loading (4 GB)
static const LONGLONG MAX_VIDEO_FILE_SIZE = 4LL * 1024LL * 1024LL * 1024LL;

// Maximum video resolution (8K)
static const int MAX_VIDEO_DIMENSION = 8192;

bool VideoPlayer::LoadVideo(const char* filePath)
{
    std::lock_guard<std::mutex> lock(m_Mutex);

    if (!m_Device || !filePath)
    {
        VideoLog("[DaroVideo] LoadVideo: null device or path\n");
        return false;
    }

    // Security: Prevent path traversal
    if (strstr(filePath, "..") != nullptr)
    {
        VideoLog("[DaroVideo] Security: Path traversal attempt blocked in video loading\n");
        return false;
    }

    UnloadVideoInternal();

    m_FilePath = filePath;

    char dbg[512];
    sprintf_s(dbg, "[DaroVideo] LoadVideo: %s\n", filePath);
    VideoLog(dbg);

    // Convert to wide string for file checks and MF
    int wlen = MultiByteToWideChar(CP_UTF8, 0, filePath, -1, nullptr, 0);
    if (wlen <= 0)
    {
        VideoLog("[DaroVideo] LoadVideo: MultiByteToWideChar failed\n");
        return false;
    }
    std::wstring wpath(wlen, 0);
    MultiByteToWideChar(CP_UTF8, 0, filePath, -1, &wpath[0], wlen);

    // Check file size before loading to prevent memory exhaustion
    WIN32_FILE_ATTRIBUTE_DATA fileInfo;
    if (!GetFileAttributesExW(wpath.c_str(), GetFileExInfoStandard, &fileInfo))
    {
        VideoLog("[DaroVideo] LoadVideo: File not found or inaccessible\n");
        return false;
    }

    LARGE_INTEGER fileSize;
    fileSize.HighPart = fileInfo.nFileSizeHigh;
    fileSize.LowPart = fileInfo.nFileSizeLow;

    if (fileSize.QuadPart > MAX_VIDEO_FILE_SIZE)
    {
        VideoLog("[DaroVideo] LoadVideo: File exceeds 500 MB limit\n");
        return false;
    }

    // Try Media Foundation first (hardware-accelerated, handles H.264/H.265/WMV)
    if (LoadVideoMF(wpath.c_str()))
    {
        VideoLog("[DaroVideo] LoadVideo: SUCCESS via Media Foundation\n");
        return true;
    }

    // MF failed - try FFmpeg fallback (handles MOV, Animation codec, ProRes, etc.)
    if (LoadVideoFFmpeg(filePath))
    {
        VideoLog("[DaroVideo] LoadVideo: SUCCESS via FFmpeg\n");
        return true;
    }

    VideoLog("[DaroVideo] LoadVideo: FAILED - neither MF nor FFmpeg could open the file\n");
    return false;
}

bool VideoPlayer::LoadVideoMF(const wchar_t* wpath)
{
    char dbg[512];

    // Create Source Reader with hardware acceleration
    IMFAttributes* attributes = nullptr;
    HRESULT hr = MFCreateAttributes(&attributes, 2);
    if (FAILED(hr))
    {
        sprintf_s(dbg, "[DaroVideo] MF: MFCreateAttributes failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        return false;
    }

    // Enable video processing and hardware acceleration
    attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
    attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);

    hr = MFCreateSourceReaderFromURL(wpath, attributes, &m_Reader);
    attributes->Release();

    if (FAILED(hr) || !m_Reader)
    {
        sprintf_s(dbg, "[DaroVideo] MF: MFCreateSourceReaderFromURL failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        m_Reader.Reset();
        return false;
    }

    VideoLog("[DaroVideo] MF: Source reader created OK\n");

    // Configure output format - try ARGB32 first (has alpha), fall back to RGB32
    ComPtr<IMFMediaType> outputType;
    hr = MFCreateMediaType(&outputType);
    if (FAILED(hr))
    {
        sprintf_s(dbg, "[DaroVideo] MF: MFCreateMediaType failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        m_Reader.Reset();
        return false;
    }

    outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_ARGB32);  // BGRA with alpha

    hr = m_Reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, outputType.Get());
    if (FAILED(hr))
    {
        // ARGB32 not supported - fall back to RGB32 (BGRX, no alpha)
        sprintf_s(dbg, "[DaroVideo] MF: ARGB32 not supported (hr=0x%08X), trying RGB32\n", (unsigned)hr);
        VideoLog(dbg);

        outputType.Reset();
        hr = MFCreateMediaType(&outputType);
        if (FAILED(hr)) { m_Reader.Reset(); return false; }

        outputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);  // BGRX without alpha

        hr = m_Reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, outputType.Get());
        if (FAILED(hr))
        {
            sprintf_s(dbg, "[DaroVideo] MF: RGB32 also failed hr=0x%08X\n", (unsigned)hr);
            VideoLog(dbg);
            m_Reader.Reset();
            return false;
        }

        m_NeedsAlphaFix = true;  // Must set alpha=0xFF during frame copy
        VideoLog("[DaroVideo] MF: Using RGB32 format (will fix alpha)\n");
    }
    else
    {
        m_NeedsAlphaFix = false;
        VideoLog("[DaroVideo] MF: Using ARGB32 format (native alpha)\n");
    }

    // Get actual media type after configuration
    ComPtr<IMFMediaType> actualType;
    hr = m_Reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &actualType);
    if (FAILED(hr))
    {
        sprintf_s(dbg, "[DaroVideo] MF: GetCurrentMediaType failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        m_Reader.Reset();
        return false;
    }

    // Get video dimensions
    UINT32 width = 0, height = 0;
    hr = MFGetAttributeSize(actualType.Get(), MF_MT_FRAME_SIZE, &width, &height);
    if (FAILED(hr))
    {
        sprintf_s(dbg, "[DaroVideo] MF: MFGetAttributeSize failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        m_Reader.Reset();
        return false;
    }

    // Check for excessive video resolution
    if (width > MAX_VIDEO_DIMENSION || height > MAX_VIDEO_DIMENSION || width == 0 || height == 0)
    {
        sprintf_s(dbg, "[DaroVideo] MF: Invalid resolution %ux%u\n", width, height);
        VideoLog(dbg);
        m_Reader.Reset();
        return false;
    }

    m_Width = static_cast<int>(width);
    m_Height = static_cast<int>(height);

    // Get frame rate
    UINT32 numerator = 0, denominator = 1;
    hr = MFGetAttributeRatio(actualType.Get(), MF_MT_FRAME_RATE, &numerator, &denominator);
    if (SUCCEEDED(hr) && denominator > 0 && numerator > 0)
    {
        m_FrameRate = static_cast<double>(numerator) / static_cast<double>(denominator);
        m_FrameDuration = 1.0 / m_FrameRate;
    }
    else
    {
        m_FrameRate = 25.0;  // Default
        m_FrameDuration = 0.04;
    }

    // Get duration
    PROPVARIANT var;
    PropVariantInit(&var);
    hr = m_Reader->GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION, &var);
    if (SUCCEEDED(hr) && var.vt == VT_UI8)
    {
        // Duration in 100-nanosecond units
        m_Duration = static_cast<double>(var.uhVal.QuadPart) / 10000000.0;
        double totalFramesD = m_Duration * m_FrameRate;
        m_TotalFrames = (totalFramesD > INT_MAX) ? INT_MAX : static_cast<int>(totalFramesD);
    }
    PropVariantClear(&var);

    sprintf_s(dbg, "[DaroVideo] MF: %dx%d @ %.1f fps, duration=%.1fs, totalFrames=%d\n",
        m_Width, m_Height, m_FrameRate, m_Duration, m_TotalFrames);
    VideoLog(dbg);

    // Create texture
    if (!CreateTexture())
    {
        VideoLog("[DaroVideo] MF: CreateTexture failed\n");
        m_Reader.Reset();
        m_Width = 0; m_Height = 0;
        return false;
    }

    VideoLog("[DaroVideo] MF: Texture created OK\n");

    m_Loaded = true;
    m_UsingFFmpeg = false;
    m_CurrentFrame = 0;
    m_CurrentTime = 0.0;
    m_EndOfStream = false;
    m_AccumulatedTime = 0.0;
    QueryPerformanceCounter(&m_LastFrameTime);

    // Decode first frame immediately so video is visible even before Play()
    bool firstFrame = DecodeNextFrame();
    sprintf_s(dbg, "[DaroVideo] MF: First frame decode %s, SRV=%p\n",
        firstFrame ? "OK" : "FAILED", m_SRV.Get());
    VideoLog(dbg);

    // Auto-play and loop by default for broadcast use
    m_Playing = true;
    m_Loop = true;
    return true;
}

bool VideoPlayer::LoadVideoFFmpeg(const char* filePath)
{
    if (!FFmpegDecoder::IsAvailable())
    {
        VideoLog("[DaroVideo] FFmpeg: Not available (headers not compiled in)\n");
        return false;
    }

    VideoLog("[DaroVideo] FFmpeg: Trying FFmpeg fallback...\n");

    m_FFmpegDecoder = std::make_unique<FFmpegDecoder>();
    if (!m_FFmpegDecoder->Open(filePath))
    {
        m_FFmpegDecoder.reset();
        return false;
    }

    m_Width = m_FFmpegDecoder->GetWidth();
    m_Height = m_FFmpegDecoder->GetHeight();
    m_Duration = m_FFmpegDecoder->GetDuration();
    m_FrameRate = m_FFmpegDecoder->GetFrameRate();
    m_TotalFrames = m_FFmpegDecoder->GetTotalFrames();
    m_FrameDuration = (m_FrameRate > 0) ? 1.0 / m_FrameRate : 0.04;

    char dbg[512];
    sprintf_s(dbg, "[DaroVideo] FFmpeg: %dx%d @ %.1f fps, duration=%.1fs, totalFrames=%d, hasAlpha=%d\n",
        m_Width, m_Height, m_FrameRate, m_Duration, m_TotalFrames, m_FFmpegDecoder->HasAlpha());
    VideoLog(dbg);

    // Create texture
    if (!CreateTexture())
    {
        VideoLog("[DaroVideo] FFmpeg: CreateTexture failed\n");
        m_FFmpegDecoder.reset();
        m_Width = 0; m_Height = 0;
        return false;
    }

    m_Loaded = true;
    m_UsingFFmpeg = true;
    m_NeedsAlphaFix = !m_FFmpegDecoder->HasAlpha();
    m_CurrentFrame = 0;
    m_CurrentTime = 0.0;
    m_EndOfStream = false;
    m_AccumulatedTime = 0.0;
    QueryPerformanceCounter(&m_LastFrameTime);

    // Decode first frame
    if (m_FFmpegDecoder->DecodeNextFrame())
    {
        CopyBufferToTexture(m_FFmpegDecoder->GetFrameData(), m_FFmpegDecoder->GetFrameStride());
    }

    sprintf_s(dbg, "[DaroVideo] FFmpeg: First frame decoded, SRV=%p\n", m_SRV.Get());
    VideoLog(dbg);

    // Auto-play and loop by default for broadcast use
    m_Playing = true;
    m_Loop = true;
    return true;
}

void VideoPlayer::UnloadVideo()
{
    std::lock_guard<std::mutex> lock(m_Mutex);
    UnloadVideoInternal();
}

void VideoPlayer::UnloadVideoInternal()
{
    // Must be called with m_Mutex already held
    m_Playing = false;
    m_Loaded = false;
    m_EndOfStream = false;
    m_FrameCopied = false;
    m_NeedsAlphaFix = false;
    m_UsingFFmpeg = false;
    m_CurrentFrame = 0;
    m_CurrentTime = 0.0;

    m_FFmpegDecoder.reset();
    m_SRV.Reset();
    m_Texture.Reset();
    m_Reader.Reset();

    m_Width = 0;
    m_Height = 0;
    m_Duration = 0.0;
    m_TotalFrames = 0;
}

bool VideoPlayer::CreateTexture()
{
    if (!m_Device || m_Width <= 0 || m_Height <= 0) return false;

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = m_Width;
    desc.Height = m_Height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DYNAMIC;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    HRESULT hr = m_Device->CreateTexture2D(&desc, nullptr, &m_Texture);
    if (FAILED(hr)) return false;

    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc = {};
    srvDesc.Format = desc.Format;
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;

    hr = m_Device->CreateShaderResourceView(m_Texture.Get(), &srvDesc, &m_SRV);
    return SUCCEEDED(hr);
}

void VideoPlayer::Play()
{
    std::lock_guard<std::mutex> lock(m_Mutex);
    if (!m_Loaded) return;
    m_Playing = true;
    m_AccumulatedTime = 0.0;
    QueryPerformanceCounter(&m_LastFrameTime);
}

void VideoPlayer::Pause()
{
    std::lock_guard<std::mutex> lock(m_Mutex);
    m_Playing = false;
}

void VideoPlayer::Stop()
{
    std::lock_guard<std::mutex> lock(m_Mutex);
    m_Playing = false;
    if (!m_Loaded) return;

    if (m_UsingFFmpeg)
    {
        if (m_FFmpegDecoder)
        {
            m_FFmpegDecoder->SeekToTime(0);
            if (m_FFmpegDecoder->DecodeNextFrame())
                CopyBufferToTexture(m_FFmpegDecoder->GetFrameData(), m_FFmpegDecoder->GetFrameStride());
        }
        m_CurrentFrame = 0;
        m_CurrentTime = 0.0;
        m_EndOfStream = false;
        return;
    }

    // MF path
    if (!m_Reader) return;
    PROPVARIANT var;
    PropVariantInit(&var);
    var.vt = VT_I8;
    var.hVal.QuadPart = 0;
    HRESULT hr = m_Reader->SetCurrentPosition(GUID_NULL, var);
    PropVariantClear(&var);

    if (SUCCEEDED(hr))
    {
        m_CurrentFrame = 0;
        m_CurrentTime = 0.0;
        m_EndOfStream = false;
        DecodeNextFrame();
    }
}

void VideoPlayer::SeekToFrame(int frame)
{
    if (!m_Loaded) return;

    std::lock_guard<std::mutex> lock(m_Mutex);
    SeekToFrameInternal(frame);
}

void VideoPlayer::SeekToFrameInternal(int frame)
{
    // Must be called with m_Mutex already held
    if (!m_Loaded) return;

    frame = (std::max)(0, (std::min)(frame, m_TotalFrames > 0 ? m_TotalFrames - 1 : 0));
    double targetTime = (m_FrameRate > 0) ? frame / m_FrameRate : 0.0;

    if (m_UsingFFmpeg)
    {
        if (m_FFmpegDecoder)
        {
            m_FFmpegDecoder->SeekToFrame(frame);
            if (m_FFmpegDecoder->DecodeNextFrame())
                CopyBufferToTexture(m_FFmpegDecoder->GetFrameData(), m_FFmpegDecoder->GetFrameStride());
        }
        m_CurrentFrame = frame;
        m_CurrentTime = targetTime;
        m_EndOfStream = false;
        return;
    }

    // MF path
    if (!m_Reader) return;

    PROPVARIANT var;
    PropVariantInit(&var);
    var.vt = VT_I8;
    var.hVal.QuadPart = static_cast<LONGLONG>(targetTime * 10000000.0);  // 100-ns units

    HRESULT hr = m_Reader->SetCurrentPosition(GUID_NULL, var);
    PropVariantClear(&var);

    if (SUCCEEDED(hr))
    {
        m_CurrentFrame = frame;
        m_CurrentTime = targetTime;
        m_EndOfStream = false;

        // Decode frame at new position
        DecodeNextFrame();
    }
}

void VideoPlayer::SeekToTime(double seconds)
{
    if (m_FrameRate > 0)
    {
        SeekToFrame(static_cast<int>(seconds * m_FrameRate));
    }
}

bool VideoPlayer::UpdateFrame()
{
    std::lock_guard<std::mutex> lock(m_Mutex);

    if (!m_Loaded || !m_Playing) return false;

    // Calculate elapsed time
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    double elapsed = static_cast<double>(now.QuadPart - m_LastFrameTime.QuadPart) / m_Frequency.QuadPart;
    m_LastFrameTime = now;

    m_AccumulatedTime += elapsed;

    // Check if we need to advance to next frame
    if (m_AccumulatedTime < m_FrameDuration)
    {
        return false;  // Not time for next frame yet
    }

    bool decoded = false;

    if (m_UsingFFmpeg && m_FFmpegDecoder)
    {
        // FFmpeg decode path
        while (m_AccumulatedTime >= m_FrameDuration && !m_EndOfStream)
        {
            m_AccumulatedTime -= m_FrameDuration;

            if (m_FFmpegDecoder->DecodeNextFrame())
            {
                CopyBufferToTexture(m_FFmpegDecoder->GetFrameData(), m_FFmpegDecoder->GetFrameStride());
                m_CurrentFrame++;
                m_CurrentTime = (m_FrameRate > 0) ? m_CurrentFrame / m_FrameRate : 0.0;
                decoded = true;
            }
            else if (m_FFmpegDecoder->IsEndOfStream())
            {
                m_EndOfStream = true;
                if (m_Loop)
                {
                    SeekToFrameInternal(0);
                    m_Playing = true;
                    m_EndOfStream = false;
                }
                else
                {
                    m_Playing = false;
                }
                break;
            }
        }
    }
    else
    {
        // MF decode path
        while (m_AccumulatedTime >= m_FrameDuration && !m_EndOfStream)
        {
            m_AccumulatedTime -= m_FrameDuration;
            decoded = DecodeNextFrame();

            if (m_EndOfStream)
            {
                if (m_Loop)
                {
                    SeekToFrameInternal(0);
                    m_Playing = true;
                    m_EndOfStream = false;
                }
                else
                {
                    m_Playing = false;
                }
                break;
            }
        }
    }

    return decoded;
}

bool VideoPlayer::DecodeNextFrame()
{
    if (!m_Reader) return false;

    DWORD streamIndex = 0;
    DWORD flags = 0;
    LONGLONG timestamp = 0;
    ComPtr<IMFSample> sample;

    HRESULT hr = m_Reader->ReadSample(
        MF_SOURCE_READER_FIRST_VIDEO_STREAM,
        0,
        &streamIndex,
        &flags,
        &timestamp,
        &sample
    );

    if (FAILED(hr))
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] DecodeNextFrame: ReadSample failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        return false;
    }

    if (flags & MF_SOURCE_READERF_ENDOFSTREAM)
    {
        m_EndOfStream = true;
        return false;
    }

    if (sample)
    {
        CopyFrameToTexture(sample.Get());
        m_CurrentTime = static_cast<double>(timestamp) / 10000000.0;
        m_CurrentFrame = static_cast<int>(m_CurrentTime * m_FrameRate);
        return true;
    }

    VideoLog("[DaroVideo] DecodeNextFrame: ReadSample returned null sample (no error, no EOS)\n");
    return false;
}

void VideoPlayer::CopyFrameToTexture(IMFSample* sample)
{
    if (!sample || !m_Texture || !m_Context) return;

    ComPtr<IMFMediaBuffer> buffer;
    HRESULT hr = sample->ConvertToContiguousBuffer(&buffer);
    if (FAILED(hr))
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] CopyFrame: ConvertToContiguousBuffer failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        return;
    }

    BYTE* srcData = nullptr;
    DWORD srcLength = 0;

    hr = buffer->Lock(&srcData, nullptr, &srcLength);
    if (FAILED(hr) || !srcData)
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] CopyFrame: buffer->Lock failed hr=0x%08X srcData=%p\n", (unsigned)hr, srcData);
        VideoLog(dbg);
        return;
    }

    D3D11_MAPPED_SUBRESOURCE mapped;
    hr = m_Context->Map(m_Texture.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (SUCCEEDED(hr))
    {
        // Copy row by row (handles different row pitches)
        UINT srcPitch = m_Width * 4;  // BGRA = 4 bytes per pixel
        if (srcPitch == 0) { m_Context->Unmap(m_Texture.Get(), 0); buffer->Unlock(); return; }

        BYTE* dstRow = static_cast<BYTE*>(mapped.pData);
        BYTE* srcRow = srcData;

        // Validate source buffer is large enough to prevent overflow
        DWORD requiredSize = (DWORD)m_Height * srcPitch;
        int rowsToCopy = (srcLength >= requiredSize) ? m_Height : (int)(srcLength / srcPitch);

        for (int y = 0; y < rowsToCopy; y++)
        {
            memcpy(dstRow, srcRow, srcPitch);

            // Fix alpha: force opaque if format lacks alpha (RGB32) or user doesn't want alpha
            if (m_NeedsAlphaFix || !m_VideoAlpha)
            {
                for (int x = 3; x < (int)srcPitch; x += 4)
                {
                    dstRow[x] = 0xFF;
                }
            }

            dstRow += mapped.RowPitch;
            srcRow += srcPitch;
        }

        m_Context->Unmap(m_Texture.Get(), 0);
        m_FrameCopied = true;  // Track that we have valid frame data
    }
    else
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] CopyFrame: Map failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
    }

    buffer->Unlock();
}

void VideoPlayer::CopyBufferToTexture(const uint8_t* srcData, int srcStride)
{
    if (!srcData || !m_Texture || !m_Context || srcStride <= 0) return;

    D3D11_MAPPED_SUBRESOURCE mapped;
    HRESULT hr = m_Context->Map(m_Texture.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);
    if (FAILED(hr)) return;

    int rowBytes = m_Width * 4;
    BYTE* dstRow = static_cast<BYTE*>(mapped.pData);
    const BYTE* srcRow = srcData;

    for (int y = 0; y < m_Height; y++)
    {
        memcpy(dstRow, srcRow, rowBytes);

        // Fix alpha: force opaque if format lacks alpha or user doesn't want alpha
        if (m_NeedsAlphaFix || !m_VideoAlpha)
        {
            for (int x = 3; x < rowBytes; x += 4)
            {
                dstRow[x] = 0xFF;
            }
        }

        dstRow += mapped.RowPitch;
        srcRow += srcStride;
    }

    m_Context->Unmap(m_Texture.Get(), 0);
    m_FrameCopied = true;
}

// ============================================================================
// VideoManager Implementation
// ============================================================================

VideoManager& VideoManager::Instance()
{
    static VideoManager instance;
    return instance;
}

bool VideoManager::Initialize(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (m_Initialized) return true;
    if (!device || !context)
    {
        VideoLog("[DaroVideo] VideoManager::Initialize: null device or context\n");
        return false;
    }

    m_Device = device;
    m_Context = context;

    // AddRef to prevent dangling pointers
    m_Device->AddRef();
    m_Context->AddRef();

    // Initialize Media Foundation
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr))
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] VideoManager::Initialize: MFStartup failed hr=0x%08X\n", (unsigned)hr);
        VideoLog(dbg);
        m_Context->Release(); m_Context = nullptr;
        m_Device->Release(); m_Device = nullptr;
        return false;
    }

    VideoLog("[DaroVideo] VideoManager::Initialize: MFStartup OK\n");
    m_Initialized = true;
    return true;
}

void VideoManager::Shutdown()
{
    {
        std::lock_guard<std::mutex> lock(m_ManagerMutex);
        m_Players.clear();
    }

    if (m_Initialized)
    {
        MFShutdown();
        m_Initialized = false;
    }

    // Release AddRef'd references
    if (m_Context) { m_Context->Release(); m_Context = nullptr; }
    if (m_Device) { m_Device->Release(); m_Device = nullptr; }
}

// Maximum number of videos that can be loaded simultaneously
static const size_t MAX_LOADED_VIDEOS = 32;

int VideoManager::LoadVideo(const char* filePath)
{
    if (!m_Initialized || !filePath)
    {
        char dbg[256];
        sprintf_s(dbg, "[DaroVideo] VideoManager::LoadVideo: not initialized=%d or null path\n", !m_Initialized);
        VideoLog(dbg);
        return 0;
    }

    std::lock_guard<std::mutex> lock(m_ManagerMutex);

    // Limit number of loaded videos to prevent memory exhaustion
    if (m_Players.size() >= MAX_LOADED_VIDEOS)
    {
        VideoLog("[DaroVideo] Maximum video limit reached (32 videos)\n");
        return 0;
    }

    auto player = std::make_unique<VideoPlayer>();
    if (!player->Initialize(m_Device, m_Context))
    {
        VideoLog("[DaroVideo] VideoManager::LoadVideo: player->Initialize failed\n");
        return 0;
    }

    if (!player->LoadVideo(filePath))
    {
        VideoLog("[DaroVideo] VideoManager::LoadVideo: player->LoadVideo failed\n");
        return 0;
    }

    int id = m_NextVideoId++;
    if (id <= 0) m_NextVideoId = id = 1; // Wraparound protection
    m_Players[id] = std::move(player);

    char dbg[256];
    sprintf_s(dbg, "[DaroVideo] VideoManager::LoadVideo: SUCCESS id=%d, total players=%zu\n", id, m_Players.size());
    VideoLog(dbg);
    return id;
}

void VideoManager::UnloadVideo(int videoId)
{
    std::lock_guard<std::mutex> lock(m_ManagerMutex);
    auto it = m_Players.find(videoId);
    if (it != m_Players.end())
    {
        m_Players.erase(it);
    }
}

VideoPlayer* VideoManager::GetPlayer(int videoId)
{
    std::lock_guard<std::mutex> lock(m_ManagerMutex);
    auto it = m_Players.find(videoId);
    if (it != m_Players.end())
    {
        return it->second.get();
    }
    return nullptr;
}

void VideoManager::UpdateAll()
{
    std::lock_guard<std::mutex> lock(m_ManagerMutex);
    for (auto& pair : m_Players)
    {
        pair.second->UpdateFrame();
    }
}
