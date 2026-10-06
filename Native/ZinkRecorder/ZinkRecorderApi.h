#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define ZINK_RECORDER_CALL __cdecl
#if defined(ZINK_RECORDER_EXPORTS)
#define ZINK_RECORDER_API __declspec(dllexport)
#else
#define ZINK_RECORDER_API __declspec(dllimport)
#endif
#else
#define ZINK_RECORDER_CALL
#define ZINK_RECORDER_API
#endif

extern "C"
{
    enum ZinkRecorderStatus : int32_t
    {
        ZINK_RECORDER_OK = 0,
        ZINK_RECORDER_INVALID_ARGUMENT = -1,
        ZINK_RECORDER_NOT_SUPPORTED = -2,
        ZINK_RECORDER_NOT_RUNNING = -3,
        ZINK_RECORDER_ALREADY_RUNNING = -4,
        ZINK_RECORDER_DEVICE_LOST = -5,
        ZINK_RECORDER_ENCODER_FAILED = -6,
        ZINK_RECORDER_MUX_FAILED = -7,
        ZINK_RECORDER_CANCELLED = -8,
        ZINK_RECORDER_INTERNAL_ERROR = -9
    };

    enum ZinkRecorderVideoCodec : int32_t
    {
        ZINK_RECORDER_CODEC_H264 = 1,
        ZINK_RECORDER_CODEC_HEVC_MAIN10 = 2
    };

    enum ZinkRecorderGpuVendor : int32_t
    {
        ZINK_RECORDER_GPU_AUTO = 0,
        ZINK_RECORDER_GPU_NVIDIA = 0x10DE,
        ZINK_RECORDER_GPU_INTEL = 0x8086,
        ZINK_RECORDER_GPU_AMD = 0x1002
    };

    struct ZinkRecorderConfig
    {
        uint32_t StructSize;
        uint32_t BufferSeconds;
        uint32_t RequestedFrameRate;
        uint32_t RequestedWidth;
        uint32_t RequestedHeight;
        uint32_t VideoBitrate;
        uint32_t AudioBitrate;
        uint32_t AudioSampleRate;
        uint32_t AudioChannels;
        uint32_t EnableHdr10;
        uint32_t EnableSystemAudio;
        uint32_t EnableMicrophone;
        uint32_t EnableCombinedAudioTrack;
        uint32_t EnableSeparateSystemTrack;
        uint32_t EnableSeparateMicrophoneTrack;
        int32_t PreferredGpuVendor;
    };

    struct ZinkRecorderRuntimeInfo
    {
        uint32_t StructSize;
        uint32_t Width;
        uint32_t Height;
        uint32_t FrameRateNumerator;
        uint32_t FrameRateDenominator;
        uint32_t IsHdr10;
        uint32_t VideoCodec;
        int32_t EncoderGpuVendor;
        uint64_t BufferedDuration100ns;
        uint64_t CapturedFrames;
        uint64_t EncodedFrames;
        uint64_t DroppedFrames;
    };

    typedef void* ZinkRecorderHandle;

    ZINK_RECORDER_API uint32_t ZINK_RECORDER_CALL zink_recorder_abi_version();
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_is_supported();
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_create(
        const ZinkRecorderConfig* config,
        ZinkRecorderHandle* recorder);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_start(
        ZinkRecorderHandle recorder,
        void* graphicsCaptureItemUnknown);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_write_system_audio(
        ZinkRecorderHandle recorder,
        const uint8_t* pcm,
        uint32_t byteCount,
        uint64_t timestamp100ns);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_write_microphone_audio(
        ZinkRecorderHandle recorder,
        const uint8_t* pcm,
        uint32_t byteCount,
        uint64_t timestamp100ns);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_get_runtime_info(
        ZinkRecorderHandle recorder,
        ZinkRecorderRuntimeInfo* info);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_save_replay(
        ZinkRecorderHandle recorder,
        const wchar_t* outputPath,
        uint64_t requestedDuration100ns);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_mux_replay_tracks(
        const wchar_t* videoPath,
        const wchar_t* combinedWavePath,
        const wchar_t* desktopWavePath,
        const wchar_t* microphoneWavePath,
        const wchar_t* outputPath,
        uint32_t audioBitrateKbps);
    ZINK_RECORDER_API int32_t ZINK_RECORDER_CALL zink_recorder_stop(
        ZinkRecorderHandle recorder);
    ZINK_RECORDER_API void ZINK_RECORDER_CALL zink_recorder_destroy(
        ZinkRecorderHandle recorder);
    ZINK_RECORDER_API uint32_t ZINK_RECORDER_CALL zink_recorder_get_last_error(
        ZinkRecorderHandle recorder,
        wchar_t* destination,
        uint32_t destinationCharacters);
}
