using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zink.Services.Recording
{
    internal static class NativeMuxWriter
    {
        private const string DllName = "Zink.NativeRecorder.dll";
        private static bool _resolverInstalled;
        private static uint _currentVideoWidth;
        private static uint _currentVideoHeight;
        private static uint _currentAudioSampleRate;
        private static uint _currentAudioChannels;
        private static uint _currentAudioBitsPerSample;
        private static IntPtr _currentWriter;

        static NativeMuxWriter()
        {
            InstallResolver();
        }

        private static void InstallResolver()
        {
            if (_resolverInstalled)
                return;

            NativeLibrary.SetDllImportResolver(typeof(NativeMuxWriter).Assembly, ResolveNativeLibrary);
            _resolverInstalled = true;
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            string baseDir = AppContext.BaseDirectory;
            string fullPath = Path.Combine(baseDir, DllName);

            if (!File.Exists(fullPath))
            {
                throw new DllNotFoundException(
                    $"Native mux DLL was not found at expected path: {fullPath}");
            }

            try
            {
                return NativeLibrary.Load(fullPath);
            }
            catch (Exception ex)
            {
                throw new DllNotFoundException(
                    $"Failed to load native mux DLL from '{fullPath}'. " +
                    $"This usually means the DLL exists but one of its native dependencies is missing. " +
                    $"Inner error: {ex.Message}", ex);
            }
        }

        public static int ZrmCreateWriter(
            string outputPath,
            uint width,
            uint height,
            uint fpsNum,
            uint fpsDen,
            uint videoBitrate,
            uint audioSampleRate,
            uint audioChannels,
            uint audioBitsPerSample)
        {
            _currentVideoWidth = width;
            _currentVideoHeight = height;
            _currentAudioSampleRate = audioSampleRate;
            _currentAudioChannels = audioChannels;
            _currentAudioBitsPerSample = audioBitsPerSample;

            ZrmShutdownWriter();

            uint fps = fpsDen == 0
                ? fpsNum
                : Math.Max(1, fpsNum / fpsDen);

            _currentWriter = ZnkWriterCreate(
                outputPath,
                checked((int)width),
                checked((int)height),
                checked((int)fps),
                checked((int)audioSampleRate),
                checked((int)audioChannels),
                checked((int)audioBitsPerSample));

            return _currentWriter != IntPtr.Zero
                ? 0
                : unchecked((int)0x80004005);
        }

        [DllImport(DllName, EntryPoint = "znk_writer_create", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ZnkWriterCreate(
            string outputPath,
            int width,
            int height,
            int fps,
            int audioSampleRate,
            int audioChannels,
            int audioBitsPerSample);

        public static int ZrmWriteVideoFrame(
            long sampleTime100ns,
            long sampleDuration100ns,
            byte[] bgraData,
            uint cbBgraData)
        {
            return ZnkWriterWriteVideoFrame(
                _currentWriter,
                bgraData,
                checked((int)cbBgraData),
                checked((int)_currentVideoWidth),
                checked((int)_currentVideoHeight),
                sampleTime100ns);
        }

        [DllImport(DllName, EntryPoint = "znk_writer_write_video_frame", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZnkWriterWriteVideoFrame(
            IntPtr writerHandle,
            byte[] bgraData,
            int bgraByteCount,
            int width,
            int height,
            long timestamp100ns);

        public static int ZrmWriteAudioPacket(
            long sampleTime100ns,
            long sampleDuration100ns,
            byte[] pcmData,
            uint cbPcmData)
        {
            if (_currentWriter == IntPtr.Zero)
                return unchecked((int)0x80004005);

            return ZnkWriterWriteAudioPcm(
                _currentWriter,
                pcmData,
                checked((int)cbPcmData),
                checked((int)_currentAudioSampleRate),
                checked((int)_currentAudioChannels),
                checked((int)_currentAudioBitsPerSample),
                sampleTime100ns);
        }

        [DllImport(DllName, EntryPoint = "znk_writer_write_audio_pcm", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZnkWriterWriteAudioPcm(
            IntPtr writerHandle,
            byte[] pcmData,
            int pcmByteCount,
            int sampleRate,
            int channels,
            int bitsPerSample,
            long timestamp100ns);

        public static int ZrmFinalizeWriter()
        {
            return _currentWriter != IntPtr.Zero
                ? ZnkWriterFinalize(_currentWriter)
                : unchecked((int)0x80004005);
        }

        [DllImport(DllName, EntryPoint = "znk_writer_finalize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZnkWriterFinalize(IntPtr writerHandle);

        public static void ZrmShutdownWriter()
        {
            if (_currentWriter == IntPtr.Zero)
                return;

            ZnkWriterDestroy(_currentWriter);
            _currentWriter = IntPtr.Zero;
        }

        [DllImport(DllName, EntryPoint = "znk_writer_destroy", CallingConvention = CallingConvention.Cdecl)]
        private static extern void ZnkWriterDestroy(IntPtr writerHandle);

        [DllImport(DllName, EntryPoint = "znk_mux_replay_tracks", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ZnkMuxReplayTracks(
            string videoPath,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath,
            int audioBitrateKbps);

        public static void MuxReplayTracks(
            string videoPath,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath,
            int audioBitrateKbps)
        {
            int hr = ZnkMuxReplayTracks(
                videoPath,
                combinedWavePath,
                desktopWavePath,
                microphoneWavePath,
                outputPath,
                audioBitrateKbps);
            ThrowIfFailed(hr, nameof(ZnkMuxReplayTracks));
        }

        public static void ThrowIfFailed(int hr, string api)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

    }
}
