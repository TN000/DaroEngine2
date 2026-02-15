// Engine/SharedTypes.h
#pragma once

#define DARO_MAX_LAYERS 64
#define DARO_MAX_PATH 260
#define DARO_MAX_TEXT 1024
#define DARO_MAX_FONTNAME 64

// Layer types
#define DARO_TYPE_RECTANGLE 0
#define DARO_TYPE_CIRCLE 1
#define DARO_TYPE_TEXT 2
#define DARO_TYPE_IMAGE 3
#define DARO_TYPE_VIDEO 4
#define DARO_TYPE_MASK 5
#define DARO_TYPE_GROUP 6

// Layer source types
#define DARO_SOURCE_SOLID 0
#define DARO_SOURCE_SPOUT 1
#define DARO_SOURCE_IMAGE 2
#define DARO_SOURCE_VIDEO 3

// Text alignment
#define DARO_ALIGN_LEFT 0
#define DARO_ALIGN_CENTER 1
#define DARO_ALIGN_RIGHT 2

// Structure must match C# DaroLayerNative EXACTLY
// Total size on Windows: 2832 bytes (with Pack=1)
#pragma pack(push, 1)
struct DaroLayer
{
    // Basic info (12 bytes)
    int id;
    int active;
    int layerType;          // DARO_TYPE_*
    
    // Transform (36 bytes)
    float posX, posY;
    float sizeX, sizeY;
    float rotX, rotY, rotZ;
    float anchorX, anchorY;
    
    // Appearance (20 bytes)
    float opacity;
    float colorR, colorG, colorB, colorA;
    
    // Source (12 bytes)
    int sourceType;         // DARO_SOURCE_*
    int textureId;
    int spoutReceiverId;
    
    // Texture transform (24 bytes)
    float texX, texY, texW, texH;
    float texRot;
    int textureLocked;
    
    // Text properties (2204 bytes on Windows)
    wchar_t textContent[DARO_MAX_TEXT];     // 1024 * 2 = 2048 bytes
    wchar_t fontFamily[DARO_MAX_FONTNAME];  // 64 * 2 = 128 bytes
    float fontSize;
    int fontBold;
    int fontItalic;
    int textAlignment;      // DARO_ALIGN_*
    float lineHeight;
    float letterSpacing;
    int textAntialiasMode;  // 0=Smooth (antialiased), 1=Sharp (aliased)

    // Path (260 bytes)
    char texturePath[DARO_MAX_PATH];

    // Mask properties (264 bytes)
    int maskMode;                           // 0=Inner, 1=Outer
    int maskedLayerCount;                   // Number of layers affected by this mask
    int maskedLayerIds[DARO_MAX_LAYERS];    // Layer IDs (64 * 4 = 256 bytes)
};
#pragma pack(pop)

// Verify size at compile time (Windows only)
#ifdef _WIN32
static_assert(sizeof(DaroLayer) == 2832, "DaroLayer size mismatch! Check struct alignment with C# DaroLayerNative.");
#endif
