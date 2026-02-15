// Engine/FFmpegDecoder.cpp
// FFmpeg-based video decoder implementation.
// When HAS_FFMPEG=1 (headers found), provides full FFmpeg decoding.
// When HAS_FFMPEG=0 (no headers), provides stubs that always fail gracefully.
#include "FFmpegDecoder.h"
#include "VideoPlayer.h"  // For VideoLog

#if HAS_FFMPEG

extern "C" {
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
#include <libavutil/imgutils.h>
#include <libavutil/mathematics.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
}

// Link FFmpeg import libraries
#pragma comment(lib, "avformat.lib")
#pragma comment(lib, "avcodec.lib")
#pragma comment(lib, "avutil.lib")
#pragma comment(lib, "swscale.lib")

FFmpegDecoder::FFmpegDecoder() {}

FFmpegDecoder::~FFmpegDecoder()
{
    Close();
}

bool FFmpegDecoder::IsAvailable()
{
    return true;
}

bool FFmpegDecoder::Open(const char* filePath)
{
    Close();

    VideoLog("[DaroVideo] FFmpeg: Opening file...\n");
    char dbg[512];

    // Open input file
    int ret = avformat_open_input(&m_FmtCtx, filePath, nullptr, nullptr);
    if (ret < 0)
    {
        char errbuf[128];
        av_strerror(ret, errbuf, sizeof(errbuf));
        sprintf_s(dbg, "[DaroVideo] FFmpeg: avformat_open_input failed: %s\n", errbuf);
        VideoLog(dbg);
        return false;
    }

    // Find stream info
    ret = avformat_find_stream_info(m_FmtCtx, nullptr);
    if (ret < 0)
    {
        VideoLog("[DaroVideo] FFmpeg: avformat_find_stream_info failed\n");
        Close();
        return false;
    }

    // Find best video stream
    const AVCodec* codec = nullptr;
    m_VideoStreamIdx = av_find_best_stream(m_FmtCtx, AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
    if (m_VideoStreamIdx < 0)
    {
        VideoLog("[DaroVideo] FFmpeg: No video stream found\n");
        Close();
        return false;
    }

    AVStream* stream = m_FmtCtx->streams[m_VideoStreamIdx];

    // Get video info
    m_Width = stream->codecpar->width;
    m_Height = stream->codecpar->height;

    if (m_Width <= 0 || m_Height <= 0 || m_Width > 8192 || m_Height > 8192)
    {
        sprintf_s(dbg, "[DaroVideo] FFmpeg: Invalid dimensions %dx%d\n", m_Width, m_Height);
        VideoLog(dbg);
        Close();
        return false;
    }

    // Frame rate
    AVRational fr = stream->r_frame_rate;
    if (fr.num > 0 && fr.den > 0)
        m_FrameRate = (double)fr.num / (double)fr.den;
    else if (stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0)
        m_FrameRate = (double)stream->avg_frame_rate.num / (double)stream->avg_frame_rate.den;
    else
        m_FrameRate = 25.0;

    // Duration
    if (m_FmtCtx->duration > 0)
        m_Duration = (double)m_FmtCtx->duration / AV_TIME_BASE;
    else if (stream->duration > 0 && stream->time_base.den > 0)
        m_Duration = (double)stream->duration * stream->time_base.num / stream->time_base.den;

    // Total frames
    if (stream->nb_frames > 0)
        m_TotalFrames = (int)stream->nb_frames;
    else if (m_Duration > 0 && m_FrameRate > 0)
        m_TotalFrames = (int)(m_Duration * m_FrameRate);

    sprintf_s(dbg, "[DaroVideo] FFmpeg: %dx%d @ %.2f fps, %.2f sec, %d frames, codec=%s\n",
              m_Width, m_Height, m_FrameRate, m_Duration, m_TotalFrames,
              codec ? codec->name : "unknown");
    VideoLog(dbg);

    // Open codec context
    m_CodecCtx = avcodec_alloc_context3(codec);
    if (!m_CodecCtx)
    {
        VideoLog("[DaroVideo] FFmpeg: avcodec_alloc_context3 failed\n");
        Close();
        return false;
    }

    ret = avcodec_parameters_to_context(m_CodecCtx, stream->codecpar);
    if (ret < 0)
    {
        VideoLog("[DaroVideo] FFmpeg: avcodec_parameters_to_context failed\n");
        Close();
        return false;
    }

    ret = avcodec_open2(m_CodecCtx, codec, nullptr);
    if (ret < 0)
    {
        char errbuf[128];
        av_strerror(ret, errbuf, sizeof(errbuf));
        sprintf_s(dbg, "[DaroVideo] FFmpeg: avcodec_open2 failed: %s\n", errbuf);
        VideoLog(dbg);
        Close();
        return false;
    }

    // Check alpha AFTER avcodec_open2 - codec sets the real pixel format
    {
        const AVPixFmtDescriptor* fmtDesc = av_pix_fmt_desc_get(m_CodecCtx->pix_fmt);
        m_HasAlpha = fmtDesc && (fmtDesc->flags & AV_PIX_FMT_FLAG_ALPHA);
        sprintf_s(dbg, "[DaroVideo] FFmpeg: pix_fmt=%d (%s), hasAlpha=%d\n",
                  (int)m_CodecCtx->pix_fmt,
                  fmtDesc ? fmtDesc->name : "unknown",
                  m_HasAlpha);
        VideoLog(dbg);
    }

    // Allocate frames and packet
    m_Frame = av_frame_alloc();
    m_FrameBGRA = av_frame_alloc();
    m_Packet = av_packet_alloc();
    if (!m_Frame || !m_FrameBGRA || !m_Packet)
    {
        VideoLog("[DaroVideo] FFmpeg: Failed to allocate frame/packet\n");
        Close();
        return false;
    }

    // Create pixel format converter (any source format -> BGRA for D3D11)
    m_SwsCtx = sws_getContext(m_Width, m_Height, m_CodecCtx->pix_fmt,
                               m_Width, m_Height, AV_PIX_FMT_BGRA,
                               SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!m_SwsCtx)
    {
        VideoLog("[DaroVideo] FFmpeg: sws_getContext failed\n");
        Close();
        return false;
    }

    // Allocate BGRA output buffer
    int bufSize = av_image_get_buffer_size(AV_PIX_FMT_BGRA, m_Width, m_Height, 1);
    if (bufSize <= 0)
    {
        VideoLog("[DaroVideo] FFmpeg: av_image_get_buffer_size failed\n");
        Close();
        return false;
    }

    m_OutputBuffer = (uint8_t*)av_malloc(bufSize);
    if (!m_OutputBuffer)
    {
        VideoLog("[DaroVideo] FFmpeg: Failed to allocate output buffer\n");
        Close();
        return false;
    }

    av_image_fill_arrays(m_FrameBGRA->data, m_FrameBGRA->linesize,
                          m_OutputBuffer, AV_PIX_FMT_BGRA, m_Width, m_Height, 1);

    m_Opened = true;
    m_EndOfStream = false;
    VideoLog("[DaroVideo] FFmpeg: Opened successfully\n");
    return true;
}

void FFmpegDecoder::Close()
{
    if (m_SwsCtx) { sws_freeContext(m_SwsCtx); m_SwsCtx = nullptr; }
    if (m_Frame) { av_frame_free(&m_Frame); }
    if (m_FrameBGRA) { av_frame_free(&m_FrameBGRA); }
    if (m_Packet) { av_packet_free(&m_Packet); }
    if (m_OutputBuffer) { av_free(m_OutputBuffer); m_OutputBuffer = nullptr; }
    if (m_CodecCtx) { avcodec_free_context(&m_CodecCtx); }
    if (m_FmtCtx) { avformat_close_input(&m_FmtCtx); }

    m_VideoStreamIdx = -1;
    m_Width = 0;
    m_Height = 0;
    m_Duration = 0.0;
    m_FrameRate = 0.0;
    m_TotalFrames = 0;
    m_HasAlpha = false;
    m_EndOfStream = false;
    m_Opened = false;
}

bool FFmpegDecoder::DecodeNextFrame()
{
    if (!m_Opened || m_EndOfStream) return false;

    while (true)
    {
        int ret = av_read_frame(m_FmtCtx, m_Packet);
        if (ret < 0)
        {
            if (ret == AVERROR_EOF)
            {
                m_EndOfStream = true;
            }
            return false;
        }

        // Skip non-video packets
        if (m_Packet->stream_index != m_VideoStreamIdx)
        {
            av_packet_unref(m_Packet);
            continue;
        }

        ret = avcodec_send_packet(m_CodecCtx, m_Packet);
        av_packet_unref(m_Packet);

        if (ret < 0)
        {
            // Send failed, try next packet
            continue;
        }

        ret = avcodec_receive_frame(m_CodecCtx, m_Frame);
        if (ret == AVERROR(EAGAIN))
        {
            // Need more packets
            continue;
        }
        if (ret < 0)
        {
            return false;
        }

        // Convert decoded frame to BGRA for D3D11 (DXGI_FORMAT_B8G8R8A8_UNORM)
        // sws_scale handles all formats including planar alpha (YUVA*) at any bit depth
        sws_scale(m_SwsCtx,
                   m_Frame->data, m_Frame->linesize,
                   0, m_Height,
                   m_FrameBGRA->data, m_FrameBGRA->linesize);

        return true;
    }
}

const uint8_t* FFmpegDecoder::GetFrameData() const
{
    return m_OutputBuffer;
}

int FFmpegDecoder::GetFrameStride() const
{
    if (m_FrameBGRA)
        return m_FrameBGRA->linesize[0];
    return m_Width * 4;
}

bool FFmpegDecoder::SeekToFrame(int frame)
{
    if (!m_Opened) return false;
    double time = (m_FrameRate > 0) ? (double)frame / m_FrameRate : 0.0;
    return SeekToTime(time);
}

bool FFmpegDecoder::SeekToTime(double seconds)
{
    if (!m_Opened) return false;

    int64_t ts = (int64_t)(seconds * AV_TIME_BASE);
    int ret = av_seek_frame(m_FmtCtx, -1, ts, AVSEEK_FLAG_BACKWARD);
    if (ret < 0) return false;

    avcodec_flush_buffers(m_CodecCtx);
    m_EndOfStream = false;
    return true;
}

#else // !HAS_FFMPEG - Stub implementations when FFmpeg is not available

FFmpegDecoder::FFmpegDecoder() {}
FFmpegDecoder::~FFmpegDecoder() {}
bool FFmpegDecoder::IsAvailable() { return false; }
bool FFmpegDecoder::Open(const char*) { return false; }
void FFmpegDecoder::Close() {}
bool FFmpegDecoder::DecodeNextFrame() { return false; }
const uint8_t* FFmpegDecoder::GetFrameData() const { return nullptr; }
int FFmpegDecoder::GetFrameStride() const { return 0; }
bool FFmpegDecoder::SeekToFrame(int) { return false; }
bool FFmpegDecoder::SeekToTime(double) { return false; }

#endif // HAS_FFMPEG
