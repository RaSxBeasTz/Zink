#include <windows.h>
#include <d3d11.h>
#include <nvEncodeAPI.h>
#include <vector>
#include <unordered_map>
#include <cstring>

#define ZINK_EXPORT extern "C" __declspec(dllexport)

namespace
{
    typedef NVENCSTATUS (NVENCAPI *CreateInstanceFn)(NV_ENCODE_API_FUNCTION_LIST*);
    struct RegisteredTexture
    {
        NV_ENC_REGISTERED_PTR Registered = nullptr;
    };

    struct Encoder
    {
        HMODULE ApiModule = nullptr;
        NV_ENCODE_API_FUNCTION_LIST Api = {};
        void* Session = nullptr;
        NV_ENC_OUTPUT_PTR Bitstream = nullptr;
        ID3D11Device* Device = nullptr;
        int Width = 0;
        int Height = 0;
        std::unordered_map<void*, RegisteredTexture> Textures;
    };

    void Destroy(Encoder* encoder)
    {
        if (!encoder) return;
        if (encoder->Session)
        {
            for (auto& entry : encoder->Textures)
                if (entry.second.Registered)
                    encoder->Api.nvEncUnregisterResource(encoder->Session, entry.second.Registered);
            if (encoder->Bitstream)
                encoder->Api.nvEncDestroyBitstreamBuffer(encoder->Session, encoder->Bitstream);
            encoder->Api.nvEncDestroyEncoder(encoder->Session);
        }
        if (encoder->Device) encoder->Device->Release();
        if (encoder->ApiModule) FreeLibrary(encoder->ApiModule);
        delete encoder;
    }

    bool IsSuccess(NVENCSTATUS status) { return status == NV_ENC_SUCCESS; }
}

ZINK_EXPORT int ZinkNvenc_Create(
    void* d3d11Device, int width, int height, int frameRate, int bitrate, void** handle)
{
    if (!d3d11Device || !handle || width <= 0 || height <= 0 || frameRate <= 0) return -1;
    *handle = nullptr;
    Encoder* encoder = new Encoder();
    encoder->Width = width;
    encoder->Height = height;
    encoder->Device = static_cast<ID3D11Device*>(d3d11Device);
    encoder->Device->AddRef();
    encoder->ApiModule = LoadLibraryW(L"nvEncodeAPI64.dll");
    if (!encoder->ApiModule) { Destroy(encoder); return -2; }

    auto createInstance = reinterpret_cast<CreateInstanceFn>(
        GetProcAddress(encoder->ApiModule, "NvEncodeAPICreateInstance"));
    if (!createInstance) { Destroy(encoder); return -3; }
    encoder->Api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    if (!IsSuccess(createInstance(&encoder->Api))) { Destroy(encoder); return -4; }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS open = {};
    open.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    open.device = encoder->Device;
    open.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
    open.apiVersion = NVENCAPI_VERSION;
    if (!IsSuccess(encoder->Api.nvEncOpenEncodeSessionEx(&open, &encoder->Session)))
    { Destroy(encoder); return -5; }

    GUID selectedPreset = NV_ENC_PRESET_P1_GUID;
    uint32_t presetCount = 0;
    if (!IsSuccess(encoder->Api.nvEncGetEncodePresetCount(
            encoder->Session, NV_ENC_CODEC_HEVC_GUID, &presetCount)) || presetCount == 0)
    { Destroy(encoder); return -6; }
    std::vector<GUID> supportedPresets(presetCount);
    uint32_t returnedPresetCount = 0;
    if (!IsSuccess(encoder->Api.nvEncGetEncodePresetGUIDs(
            encoder->Session, NV_ENC_CODEC_HEVC_GUID, supportedPresets.data(),
            presetCount, &returnedPresetCount)))
    { Destroy(encoder); return -7; }

    const GUID preferredPresets[] = {
        NV_ENC_PRESET_P1_GUID,
        NV_ENC_PRESET_P2_GUID
    };
    bool presetSelected = false;
    for (const GUID& preferred : preferredPresets)
    {
        bool supported = false;
        for (uint32_t index = 0; index < returnedPresetCount; ++index)
        {
            if (IsEqualGUID(preferred, supportedPresets[index]))
            {
                supported = true;
                break;
            }
        }
        if (!supported) continue;
        selectedPreset = preferred;
        presetSelected = true;
        break;
    }
    if (!presetSelected) { Destroy(encoder); return -8; }

    NV_ENC_PRESET_CONFIG preset = {};
    preset.version = NV_ENC_PRESET_CONFIG_VER;
    preset.presetCfg.version = NV_ENC_CONFIG_VER;
    if (!IsSuccess(encoder->Api.nvEncGetEncodePresetConfigEx(
            encoder->Session, NV_ENC_CODEC_HEVC_GUID, selectedPreset,
            NV_ENC_TUNING_INFO_LOW_LATENCY, &preset)))
    { Destroy(encoder); return -9; }

    NV_ENC_CONFIG config = preset.presetCfg;
    config.profileGUID = NV_ENC_HEVC_PROFILE_MAIN10_GUID;
    config.gopLength = static_cast<uint32_t>(frameRate * 2);
    config.frameIntervalP = 1;
    config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
    config.rcParams.averageBitRate = static_cast<uint32_t>(bitrate);
    config.rcParams.maxBitRate = static_cast<uint32_t>(bitrate);
    config.encodeCodecConfig.hevcConfig.chromaFormatIDC = 1;
    config.encodeCodecConfig.hevcConfig.repeatSPSPPS = 1;
    config.encodeCodecConfig.hevcConfig.inputBitDepth = NV_ENC_BIT_DEPTH_10;
    config.encodeCodecConfig.hevcConfig.outputBitDepth = NV_ENC_BIT_DEPTH_10;
    auto& vui = config.encodeCodecConfig.hevcConfig.hevcVUIParameters;
    vui.videoSignalTypePresentFlag = 1;
    vui.videoFormat = NV_ENC_VUI_VIDEO_FORMAT_UNSPECIFIED;
    vui.videoFullRangeFlag = 0;
    vui.colourDescriptionPresentFlag = 1;
    vui.colourPrimaries = NV_ENC_VUI_COLOR_PRIMARIES_BT2020;
    vui.transferCharacteristics = NV_ENC_VUI_TRANSFER_CHARACTERISTIC_SMPTE2084;
    vui.colourMatrix = NV_ENC_VUI_MATRIX_COEFFS_BT2020_NCL;

    NV_ENC_INITIALIZE_PARAMS init = {};
    init.version = NV_ENC_INITIALIZE_PARAMS_VER;
    init.encodeGUID = NV_ENC_CODEC_HEVC_GUID;
    init.presetGUID = selectedPreset;
    init.encodeWidth = width;
    init.encodeHeight = height;
    init.darWidth = width;
    init.darHeight = height;
    init.frameRateNum = frameRate;
    init.frameRateDen = 1;
    init.enablePTD = 1;
    // Background replay may coexist with the latency-sensitive H.264 live stream.
    // Do not let the driver's automatic HEVC split-frame mode occupy every NVENC
    // engine for the 4K replay session. This changes scheduling only: resolution,
    // frame rate, HDR format and bitrate remain untouched.
    init.splitEncodeMode = NV_ENC_SPLIT_DISABLE_MODE;
    init.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;
    init.encodeConfig = &config;
    if (!IsSuccess(encoder->Api.nvEncInitializeEncoder(encoder->Session, &init)))
    { Destroy(encoder); return -10; }

    NV_ENC_CREATE_BITSTREAM_BUFFER createBuffer = {};
    createBuffer.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
    if (!IsSuccess(encoder->Api.nvEncCreateBitstreamBuffer(encoder->Session, &createBuffer)))
    { Destroy(encoder); return -11; }
    encoder->Bitstream = createBuffer.bitstreamBuffer;
    *handle = encoder;
    return 0;
}

ZINK_EXPORT int ZinkNvenc_Encode(
    void* handle, void* d3d11Texture, long long timestamp100ns, int forceIdr,
    unsigned char* output, int outputCapacity, int* outputLength, int* keyFrame)
{
    if (!handle || !d3d11Texture || !output || !outputLength || !keyFrame) return -1;
    Encoder* encoder = static_cast<Encoder*>(handle);
    auto found = encoder->Textures.find(d3d11Texture);
    if (found == encoder->Textures.end())
    {
        NV_ENC_REGISTER_RESOURCE reg = {};
        reg.version = NV_ENC_REGISTER_RESOURCE_VER;
        reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
        reg.resourceToRegister = d3d11Texture;
        reg.width = encoder->Width;
        reg.height = encoder->Height;
        reg.pitch = 0;
        reg.bufferFormat = NV_ENC_BUFFER_FORMAT_YUV420_10BIT;
        reg.bufferUsage = NV_ENC_INPUT_IMAGE;
        if (!IsSuccess(encoder->Api.nvEncRegisterResource(encoder->Session, &reg))) return -2;
        found = encoder->Textures.emplace(d3d11Texture, RegisteredTexture{reg.registeredResource}).first;
    }

    NV_ENC_MAP_INPUT_RESOURCE map = {};
    map.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    map.registeredResource = found->second.Registered;
    if (!IsSuccess(encoder->Api.nvEncMapInputResource(encoder->Session, &map))) return -3;

    NV_ENC_PIC_PARAMS picture = {};
    picture.version = NV_ENC_PIC_PARAMS_VER;
    picture.inputBuffer = map.mappedResource;
    picture.bufferFmt = map.mappedBufferFmt;
    picture.inputWidth = encoder->Width;
    picture.inputHeight = encoder->Height;
    picture.outputBitstream = encoder->Bitstream;
    picture.inputTimeStamp = static_cast<uint64_t>(timestamp100ns);
    picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
    if (forceIdr) picture.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
    NVENCSTATUS encodeStatus = encoder->Api.nvEncEncodePicture(encoder->Session, &picture);
    if (!IsSuccess(encodeStatus))
    {
        encoder->Api.nvEncUnmapInputResource(encoder->Session, map.mappedResource);
        return encodeStatus == NV_ENC_ERR_NEED_MORE_INPUT ? 1 : -4;
    }

    NV_ENC_LOCK_BITSTREAM locked = {};
    locked.version = NV_ENC_LOCK_BITSTREAM_VER;
    locked.outputBitstream = encoder->Bitstream;
    locked.doNotWait = 0;
    if (!IsSuccess(encoder->Api.nvEncLockBitstream(encoder->Session, &locked)))
    {
        encoder->Api.nvEncUnmapInputResource(encoder->Session, map.mappedResource);
        return -5;
    }
    if (static_cast<int>(locked.bitstreamSizeInBytes) > outputCapacity)
    {
        encoder->Api.nvEncUnlockBitstream(encoder->Session, encoder->Bitstream);
        encoder->Api.nvEncUnmapInputResource(encoder->Session, map.mappedResource);
        return -6;
    }
    std::memcpy(output, locked.bitstreamBufferPtr, locked.bitstreamSizeInBytes);
    *outputLength = static_cast<int>(locked.bitstreamSizeInBytes);
    *keyFrame = locked.pictureType == NV_ENC_PIC_TYPE_IDR || locked.pictureType == NV_ENC_PIC_TYPE_I;
    encoder->Api.nvEncUnlockBitstream(encoder->Session, encoder->Bitstream);
    encoder->Api.nvEncUnmapInputResource(encoder->Session, map.mappedResource);
    return 0;
}

ZINK_EXPORT void ZinkNvenc_Destroy(void* handle) { Destroy(static_cast<Encoder*>(handle)); }
