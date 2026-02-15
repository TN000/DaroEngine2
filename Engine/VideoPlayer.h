// Engine/VideoPlayer.h
// Video playback using Media Foundation Source Reader, with FFmpeg fallback
#pragma once

#include <d3d11.h>
#include <wrl/client.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <string>
#include <mutex>
#include <map>
#include <memory>
#include "FFmpegDecoder.h"

using Microsoft::WRL::ComPtr;

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")

// Video diagnostic logger - writes to DaroVideo.log next to DLL
void VideoLog(const char* msg);

class VideoPlayer
{
public:
    VideoPlayer();
    ~VideoPlayer();

    // Initialize with D3D device for hardware acceleration
    bool Initialize(ID3D11Device* device, ID3D11DeviceContext* context);
    void Shutdown();

    // Load video file
    bool LoadVideo(const char* filePath);
    void UnloadVideo();
    bool IsLoaded() const { return m_Loaded; }
    bool HasFrameData() const { return m_FrameCopied; }

    // Playback control
    void Play();
    void Pause();
    void Stop();
    void SeekToFrame(int frame);
    void SeekToTime(double seconds);
    bool IsPlaying() const { return m_Playing; }

    // Frame update - call each render frame
    // Returns true if a new frame was decoded
    bool UpdateFrame();

    // Get texture for rendering
    ID3D11ShaderResourceView* GetSRV() const { return m_SRV.Get(); }
    ID3D11Texture2D* GetTexture() const { return m_Texture.Get(); }

    // Video info
    int GetWidth() const { return m_Width; }
    int GetHeight() const { return m_Height; }
    double GetDuration() const { return m_Duration; }
    double GetFrameRate() const { return m_FrameRate; }
    int GetTotalFrames() const { return m_TotalFrames; }
    int GetCurrentFrame() const { return m_CurrentFrame; }
    double GetCurrentTime() const { return m_CurrentTime; }

    // Looping
    void SetLoop(bool loop) { m_Loop = loop; }
    bool GetLoop() const { return m_Loop; }

    // Alpha channel control
    void SetVideoAlpha(bool alpha) { std::lock_guard<std::mutex> lock(m_Mutex); m_VideoAlpha = alpha; }
    bool GetVideoAlpha() const { return m_VideoAlpha; }

private:
    bool CreateTexture();
    bool DecodeNextFrame();
    void CopyFrameToTexture(IMFSample* sample);
    void CopyBufferToTexture(const uint8_t* srcData, int srcStride);

    // MF-specific loading (returns true if MF could open the file)
    bool LoadVideoMF(const wchar_t* wpath);
    // FFmpeg fallback loading
    bool LoadVideoFFmpeg(const char* filePath);

    // Internal versions called while m_Mutex is already held
    void UnloadVideoInternal();
    void SeekToFrameInternal(int frame);

private:
    ID3D11Device* m_Device = nullptr;
    ID3D11DeviceContext* m_Context = nullptr;

    ComPtr<IMFSourceReader> m_Reader;
    ComPtr<ID3D11Texture2D> m_Texture;
    ComPtr<ID3D11ShaderResourceView> m_SRV;

    std::string m_FilePath;
    int m_Width = 0;
    int m_Height = 0;
    double m_Duration = 0.0;
    double m_FrameRate = 0.0;
    int m_TotalFrames = 0;
    int m_CurrentFrame = 0;
    double m_CurrentTime = 0.0;

    bool m_Loaded = false;
    bool m_Playing = false;
    bool m_Loop = false;
    bool m_EndOfStream = false;
    bool m_FrameCopied = false;   // True once at least one frame has been copied to texture
    bool m_NeedsAlphaFix = false; // True when using RGB32 (no alpha) - must set alpha=0xFF
    bool m_VideoAlpha = false;    // True when user wants alpha channel preserved from video
    bool m_UsingFFmpeg = false;   // True when FFmpeg decoder is active instead of MF
    std::unique_ptr<FFmpegDecoder> m_FFmpegDecoder;

    // Timing for frame advancement
    LARGE_INTEGER m_LastFrameTime;
    LARGE_INTEGER m_Frequency;
    double m_FrameDuration = 0.0;
    double m_AccumulatedTime = 0.0;

    std::mutex m_Mutex;
};

// Global video manager
class VideoManager
{
public:
    static VideoManager& Instance();

    bool Initialize(ID3D11Device* device, ID3D11DeviceContext* context);
    void Shutdown();

    // Load video and get ID
    int LoadVideo(const char* filePath);
    void UnloadVideo(int videoId);

    // Get player by ID. Caller must ensure the player is not unloaded
    // while using the returned pointer (guaranteed by C# _engineLock serialization).
    VideoPlayer* GetPlayer(int videoId);

    // Update all playing videos (call each frame)
    void UpdateAll();

private:
    VideoManager() = default;
    ~VideoManager() = default;

    ID3D11Device* m_Device = nullptr;
    ID3D11DeviceContext* m_Context = nullptr;
    std::map<int, std::unique_ptr<VideoPlayer>> m_Players;
    std::mutex m_ManagerMutex;
    int m_NextVideoId = 1;
    bool m_Initialized = false;
};
