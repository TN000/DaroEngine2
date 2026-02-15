// Designer/Engine/EngineInterop.cs
using System;
using System.Runtime.InteropServices;

namespace DaroDesigner.Engine
{
    // Constants matching SharedTypes.h
    public static class DaroConstants
    {
        public const int MAX_LAYERS = 64;
        public const int MAX_TEXT = 1024;
        public const int MAX_FONTNAME = 64;
        public const int MAX_PATH = 260;
        
        // Layer types
        public const int TYPE_RECTANGLE = 0;
        public const int TYPE_ELLIPSE = 1;
        public const int TYPE_TEXT = 2;
        public const int TYPE_IMAGE = 3;
        public const int TYPE_VIDEO = 4;
        public const int TYPE_MASK = 5;
        public const int TYPE_GROUP = 6;
        
        // Source types
        public const int SOURCE_SOLID = 0;
        public const int SOURCE_SPOUT = 1;
        public const int SOURCE_IMAGE = 2;
        public const int SOURCE_VIDEO = 3;
        
        // Text alignment
        public const int ALIGN_LEFT = 0;
        public const int ALIGN_CENTER = 1;
        public const int ALIGN_RIGHT = 2;
    }

    // Structure must match C++ DaroLayer EXACTLY
    // Total size: 2832 bytes (Pack=1, Unicode)
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    public struct DaroLayerNative
    {
        // Basic info (12 bytes)
        public int id;
        public int active;
        public int layerType;       // Must match C++ layerType field!

        // Transform (36 bytes)
        public float posX, posY;
        public float sizeX, sizeY;
        public float rotX, rotY, rotZ;
        public float anchorX, anchorY;

        // Appearance (20 bytes)
        public float opacity;
        public float colorR, colorG, colorB, colorA;

        // Source (12 bytes)
        public int sourceType;
        public int textureId;
        public int spoutReceiverId;

        // Texture transform (24 bytes)
        public float texX, texY, texW, texH;
        public float texRot;
        public int textureLocked;

        // Text properties (2204 bytes)
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string textContent;      // 1024 * 2 = 2048 bytes (Unicode)
        
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string fontFamily;       // 64 * 2 = 128 bytes (Unicode)
        
        public float fontSize;
        public int fontBold;
        public int fontItalic;
        public int textAlignment;
        public float lineHeight;
        public float letterSpacing;
        public int textAntialiasMode;  // 0=Smooth (antialiased), 1=Sharp (aliased)

        // Path (260 bytes) - ASCII byte array matching C++ char[260]
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.U1, SizeConst = 260)]
        public byte[] texturePath;

        // Mask properties (264 bytes)
        public int maskMode;            // 0=Inner, 1=Outer
        public int maskedLayerCount;    // Number of layers affected by this mask

        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.I4, SizeConst = 64)]
        public int[] maskedLayerIds;    // Layer IDs (64 * 4 = 256 bytes)
    }

    public static class DaroEngine
    {
        private const string DLL = "DaroEngine.dll";

        // Error codes
        public const int DARO_OK = 0;
        public const int DARO_ERROR_ALREADY_INIT = 1;
        public const int DARO_ERROR_CREATE_DEVICE = 2;
        public const int DARO_ERROR_CREATE_RT = 3;
        public const int DARO_ERROR_CREATE_SHADERS = 4;
        public const int DARO_ERROR_CREATE_GEOMETRY = 5;
        public const int DARO_ERROR_CREATE_STAGING = 6;
        public const int DARO_ERROR_CREATE_FRAMEBUFFER = 7;

        // Lifecycle
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_Initialize(int width, int height, double targetFps);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_Shutdown();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_IsInitialized();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetLastError();

        // Rendering
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_BeginFrame();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_EndFrame();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_Render();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_Present();

        // Frame buffer
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_LockFrameBuffer(out IntPtr pData, out int width, out int height, out int stride);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_UnlockFrameBuffer();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern long Daro_GetFrameNumber();

        // Layers
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SetLayerCount(int count);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_UpdateLayer(int index, ref DaroLayerNative layer);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_GetLayer(int index, ref DaroLayerNative layer);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_ClearLayers();

        // Playback
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_Play();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_Stop();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SeekToFrame(int frame);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SeekToTime(float time);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_IsPlaying();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetCurrentFrame();

        // Stats
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern double Daro_GetFPS();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern double Daro_GetFrameTime();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetDroppedFrames();

        // Spout Output
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_EnableSpoutOutput([MarshalAs(UnmanagedType.LPUTF8Str)] string senderName);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_DisableSpoutOutput();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_IsSpoutEnabled();

        // Texture management
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_LoadTexture([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_UnloadTexture(int textureId);

        // Spout Input
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetSpoutSenderCount();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_GetSpoutSenderName(int index, [Out] byte[] buffer, int bufferSize);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_ConnectSpoutReceiver([MarshalAs(UnmanagedType.LPUTF8Str)] string senderName);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_DisconnectSpoutReceiver(int receiverId);

        // Debug - structure info
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetStructSize();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetOffsetPosX();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetOffsetSizeX();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetOffsetOpacity();

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetOffsetTextContent();

        // Debug - bounding boxes
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SetShowBounds(bool show);

        // Device status
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_IsDeviceLost();

        // Edge antialiasing
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SetEdgeSmoothing(float width);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern float Daro_GetEdgeSmoothing();

        // Video playback
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_LoadVideo([MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_UnloadVideo(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_PlayVideo(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_PauseVideo(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_StopVideo(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SeekVideo(int videoId, int frame);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SeekVideoTime(int videoId, double seconds);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Daro_IsVideoPlaying(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetVideoFrame(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int Daro_GetVideoTotalFrames(int videoId);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SetVideoLoop(int videoId, bool loop);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern void Daro_SetVideoAlpha(int videoId, bool alpha);

        // Helper
        public static string GetErrorString(int errorCode)
        {
            return errorCode switch
            {
                DARO_OK => "OK",
                DARO_ERROR_ALREADY_INIT => "Engine already initialized",
                DARO_ERROR_CREATE_DEVICE => "Failed to create D3D11 device",
                DARO_ERROR_CREATE_RT => "Failed to create render target",
                DARO_ERROR_CREATE_SHADERS => "Failed to compile shaders",
                DARO_ERROR_CREATE_GEOMETRY => "Failed to create geometry buffers",
                DARO_ERROR_CREATE_STAGING => "Failed to create staging texture",
                DARO_ERROR_CREATE_FRAMEBUFFER => "Failed to create frame buffer",
                _ => $"Unknown error: {errorCode}"
            };
        }
    }
}
