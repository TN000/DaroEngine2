// Engine/FFmpegDecoder.h
// FFmpeg-based video decoder as fallback when Media Foundation can't handle a codec.
// Automatically enabled when FFmpeg dev headers are installed in ThirdParty/ffmpeg/include.
// Without FFmpeg headers, provides stubs that return "not available".
#pragma once

#include <cstdint>

// Auto-detect FFmpeg availability at compile time
#if __has_include(<libavformat/avformat.h>)
#define HAS_FFMPEG 1
#else
#define HAS_FFMPEG 0
#endif

#if HAS_FFMPEG
extern "C" {
struct AVFormatContext;
struct AVCodecContext;
struct SwsContext;
struct AVFrame;
struct AVPacket;
struct AVRational;
}
#endif

class FFmpegDecoder
{
public:
    FFmpegDecoder();
    ~FFmpegDecoder();

    /// Returns true if FFmpeg support was compiled in.
    static bool IsAvailable();

    /// Open a video file. Returns true on success.
    bool Open(const char* filePath);
    void Close();

    /// Decode the next video frame into internal BGRA buffer.
    /// Returns true if a frame was decoded successfully.
    bool DecodeNextFrame();

    /// Get pointer to last decoded frame (BGRA format, bottom-up or top-down depending on codec).
    const uint8_t* GetFrameData() const;
    int GetFrameStride() const;

    /// Seek to a specific frame or time.
    bool SeekToFrame(int frame);
    bool SeekToTime(double seconds);

    int GetWidth() const { return m_Width; }
    int GetHeight() const { return m_Height; }
    double GetDuration() const { return m_Duration; }
    double GetFrameRate() const { return m_FrameRate; }
    int GetTotalFrames() const { return m_TotalFrames; }
    bool HasAlpha() const { return m_HasAlpha; }
    bool IsEndOfStream() const { return m_EndOfStream; }

private:
#if HAS_FFMPEG
    AVFormatContext* m_FmtCtx = nullptr;
    AVCodecContext* m_CodecCtx = nullptr;
    SwsContext* m_SwsCtx = nullptr;
    AVFrame* m_Frame = nullptr;
    AVFrame* m_FrameBGRA = nullptr;
    AVPacket* m_Packet = nullptr;
    int m_VideoStreamIdx = -1;
#endif
    uint8_t* m_OutputBuffer = nullptr;
    int m_Width = 0;
    int m_Height = 0;
    double m_Duration = 0.0;
    double m_FrameRate = 0.0;
    int m_TotalFrames = 0;
    bool m_HasAlpha = false;
    bool m_EndOfStream = false;
    bool m_Opened = false;
};
