using SharpDX.MediaFoundation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services.NativeCalling;

namespace Zink.Services.Recording
{
    /// <summary>
    /// ZINK-owned replay muxer. SharpDX is only the COM projection; capture,
    /// parsing, AAC encoding and MP4 writing are performed by Windows Media
    /// Foundation in-process. No external executable is launched.
    /// </summary>
    internal static class ZinkNativeMultiTrackMuxer
    {
        public static Task MuxAsync(
            IReadOnlyList<H264EncodedFrame> videoFrames,
            int width,
            int height,
            double frameRate,
            int videoBitrate,
            bool hdr10,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath,
            int audioBitrateKbps)
        {
            return Task.Run(() => Mux(
                videoFrames,
                width,
                height,
                frameRate,
                videoBitrate,
                hdr10,
                combinedWavePath,
                desktopWavePath,
                microphoneWavePath,
                outputPath,
                audioBitrateKbps));
        }

        private static void Mux(
            IReadOnlyList<H264EncodedFrame> videoFrames,
            int width,
            int height,
            double frameRate,
            int videoBitrate,
            bool hdr10,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath,
            int audioBitrateKbps)
        {
            if (videoFrames == null || videoFrames.Count == 0)
                throw new InvalidOperationException("No encoded replay frames were supplied to the native muxer.");
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("The encoded replay dimensions are invalid.");

            if (string.Equals(Path.GetExtension(outputPath), ".mkv", StringComparison.OrdinalIgnoreCase))
            {
                ZinkMatroskaWriter.Write(
                    videoFrames,
                    width,
                    height,
                    frameRate,
                    hdr10,
                    combinedWavePath,
                    desktopWavePath,
                    microphoneWavePath,
                    outputPath);
                return;
            }

            string? outputFolder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputFolder))
                throw new InvalidOperationException("The replay output folder is invalid.");
            Directory.CreateDirectory(outputFolder);
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            int bitrate = audioBitrateKbps is 256 or 320 or 384
                ? audioBitrateKbps
                : 320;

            int fpsNumerator = Math.Max(1, (int)Math.Round(frameRate * 1000.0));
            const int fpsDenominator = 1000;
            using var videoType = new MediaType();
            videoType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoType.Set(MediaTypeAttributeKeys.Subtype, hdr10 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
            videoType.Set(MediaTypeAttributeKeys.AvgBitrate, Math.Max(1, videoBitrate));
            videoType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(width, height));
            videoType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(fpsNumerator, fpsDenominator));
            videoType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            videoType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            videoType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 0);
            byte[] sequenceHeader = ExtractAnnexBSequenceHeader(videoFrames, hdr10);
            if (sequenceHeader.Length > 0)
                videoType.Set(MediaTypeAttributeKeys.MpegSequenceHeader, sequenceHeader);
            if (hdr10)
                SetHdr10ColourMetadata(videoType);
            else
                SetSdrBt709ColourMetadata(videoType);

            using var writerAttributes = new MediaAttributes(3);
            writerAttributes.Set(SinkWriterAttributeKeys.DisableThrottling.Guid, 1);
            writerAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms.Guid, 1);
            writerAttributes.Set(SinkWriterAttributeKeys.LowLatency.Guid, 1);
            using SinkWriter writer = MediaFactory.CreateSinkWriterFromURL(outputPath, null, writerAttributes);

            writer.AddStream(videoType, out int videoStreamIndex);
            writer.SetInputMediaType(videoStreamIndex, videoType, null);

            var audioInputs = new List<AudioInput>();
            try
            {
                AddAudioInput(combinedWavePath, "Combined audio");
                AddAudioInput(desktopWavePath, "Desktop and game audio");
                AddAudioInput(microphoneWavePath, "Microphone");

                writer.BeginWriting();
                WriteVideoSamples(videoFrames, writer, videoStreamIndex, frameRate);
                foreach (AudioInput input in audioInputs)
                    WriteAudioSamples(input, writer);
                writer.Finalize();
            }
            finally
            {
                foreach (AudioInput input in audioInputs)
                    input.Dispose();
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException("The native MP4 mux completed without creating a valid output file.");

            void AddAudioInput(string? path, string title)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                var reader = new SourceReader(path, null);
                var pcmType = CreatePcmType();
                try
                {
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, pcmType);
                    using MediaType aacType = CreateAacType(bitrate);
                    writer.AddStream(aacType, out int streamIndex);
                    writer.SetInputMediaType(streamIndex, pcmType, null);
                    audioInputs.Add(new AudioInput(reader, pcmType, streamIndex, title));
                }
                catch
                {
                    pcmType.Dispose();
                    reader.Dispose();
                    throw;
                }
            }
        }

        private static MediaType CreatePcmType()
        {
            var type = new MediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
            type.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4);
            type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 48_000 * 4);
            return type;
        }

        private static MediaType CreateAacType(int bitrateKbps)
        {
            var type = new MediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            type.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            type.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
            type.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000);
            type.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
            type.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, bitrateKbps * 1000 / 8);
            type.Set(MediaTypeAttributeKeys.AacPayloadType, 0);
            type.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29);
            return type;
        }

        private static void WriteVideoSamples(
            IReadOnlyList<H264EncodedFrame> frames,
            SinkWriter writer,
            int outputStreamIndex,
            double frameRate)
        {
            const long maximumBytesPerSecond = 32L * 1024 * 1024;
            double ticksPerFrame = 10_000_000.0 / Math.Max(1.0, frameRate);
            long bytesSubmitted = 0;
            var writeClock = Stopwatch.StartNew();
            for (int index = 0; index < frames.Count; index++)
            {
                H264EncodedFrame frame = frames[index];
                using MediaBuffer buffer = MediaFactory.CreateMemoryBuffer(frame.Data.Length);
                IntPtr destination = buffer.Lock(out _, out _);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(frame.Data, 0, destination, frame.Data.Length);
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.CurrentLength = frame.Data.Length;

                using Sample sample = MediaFactory.CreateSample();
                sample.AddBuffer(buffer);
                long sampleTime = (long)Math.Round(index * ticksPerFrame);
                long nextSampleTime = (long)Math.Round((index + 1) * ticksPerFrame);
                sample.SampleTime = sampleTime;
                sample.SampleDuration = Math.Max(1, nextSampleTime - sampleTime);
                if (frame.IsKeyFrame)
                    sample.Set(SampleAttributeKeys.CleanPoint, true);
                writer.WriteSample(outputStreamIndex, sample);

                bytesSubmitted += frame.Data.Length;
                if (frames is H264EncodedFrame[] releasableFrames)
                    releasableFrames[index] = null!;
                double minimumElapsedSeconds = bytesSubmitted / (double)maximumBytesPerSecond;
                double delaySeconds = minimumElapsedSeconds - writeClock.Elapsed.TotalSeconds;
                if (delaySeconds > 0)
                    Thread.Sleep(TimeSpan.FromSeconds(Math.Min(delaySeconds, 0.05)));
            }
        }

        private static long PackRatio(int high, int low) =>
            ((long)(uint)high << 32) | (uint)low;

        private static byte[] ExtractAnnexBSequenceHeader(
            IReadOnlyList<H264EncodedFrame> frames,
            bool hevc)
        {
            var parameterSets = new List<byte[]>();
            var foundTypes = new HashSet<int>();
            foreach (H264EncodedFrame frame in frames)
            {
                byte[] data = frame.Data;
                int offset = 0;
                while (TryFindStartCode(data, offset, out int start, out int prefixLength))
                {
                    int payloadStart = start + prefixLength;
                    if (payloadStart >= data.Length)
                        break;

                    int nextSearch = payloadStart;
                    int end = TryFindStartCode(data, nextSearch, out int nextStart, out _)
                        ? nextStart
                        : data.Length;
                    int nalType = hevc
                        ? (data[payloadStart] >> 1) & 0x3F
                        : data[payloadStart] & 0x1F;
                    bool isParameterSet = hevc
                        ? nalType is 32 or 33 or 34
                        : nalType is 7 or 8;
                    if (isParameterSet && foundTypes.Add(nalType))
                    {
                        var nal = new byte[end - start];
                        Buffer.BlockCopy(data, start, nal, 0, nal.Length);
                        parameterSets.Add(nal);
                    }

                    offset = Math.Max(end, payloadStart + 1);
                }

                if (hevc
                    ? foundTypes.Contains(32) && foundTypes.Contains(33) && foundTypes.Contains(34)
                    : foundTypes.Contains(7) && foundTypes.Contains(8))
                    break;
            }

            int length = 0;
            foreach (byte[] parameterSet in parameterSets)
                length += parameterSet.Length;
            var header = new byte[length];
            int destinationOffset = 0;
            foreach (byte[] parameterSet in parameterSets)
            {
                Buffer.BlockCopy(parameterSet, 0, header, destinationOffset, parameterSet.Length);
                destinationOffset += parameterSet.Length;
            }
            return header;
        }

        private static bool TryFindStartCode(
            byte[] data,
            int offset,
            out int start,
            out int prefixLength)
        {
            for (int index = Math.Max(0, offset); index + 2 < data.Length; index++)
            {
                if (data[index] != 0 || data[index + 1] != 0)
                    continue;
                if (data[index + 2] == 1)
                {
                    start = index;
                    prefixLength = 3;
                    return true;
                }
                if (index + 3 < data.Length && data[index + 2] == 0 && data[index + 3] == 1)
                {
                    start = index;
                    prefixLength = 4;
                    return true;
                }
            }

            start = -1;
            prefixLength = 0;
            return false;
        }

        private static void SetSdrBt709ColourMetadata(MediaType mediaType)
        {
            // MFVideoPrimaries_BT709=2, MFVideoTransferFunction_709=5,
            // MFVideoTransferMatrix_BT709=1, MFNominalRange_16_235=2.
            mediaType.Set(MediaTypeAttributeKeys.VideoPrimaries, 2);
            mediaType.Set(MediaTypeAttributeKeys.TransferFunction, 5);
            mediaType.Set(MediaTypeAttributeKeys.YuvMatrix, 1);
            mediaType.Set(MediaTypeAttributeKeys.VideoNominalRange, 2);
        }

        private static void SetHdr10ColourMetadata(MediaType mediaType)
        {
            // BT.2020 primaries, SMPTE ST 2084 (PQ), BT.2020 non-constant
            // luminance matrix and studio-range 10-bit YUV.
            mediaType.Set(MediaTypeAttributeKeys.VideoPrimaries, 9);
            mediaType.Set(MediaTypeAttributeKeys.TransferFunction, 15);
            mediaType.Set(MediaTypeAttributeKeys.YuvMatrix, 4);
            mediaType.Set(MediaTypeAttributeKeys.VideoNominalRange, 2);
            // Windows Media Foundation HDR10 luminance attributes. These mirror
            // the mastering-display and content-light SEI written by oneVPL.
            mediaType.Set(new Guid("50253128-C110-4DE4-98AE-46A324FAE6DA"), 1000);
            mediaType.Set(new Guid("58D4BF57-6F52-4733-A195-A9E29ECF9E27"), 400);
            mediaType.Set(new Guid("D6C6B997-272F-4CA1-8D00-8042111A0FF6"), 1000);
            mediaType.Set(new Guid("839A4460-4E7E-4B4F-AE79-CC08905C7B27"), 50);
        }

        private static void WriteAudioSamples(AudioInput input, SinkWriter writer)
        {
            long firstTimestamp = -1;
            while (true)
            {
                using Sample? sample = input.Reader.ReadSample(
                    SourceReaderIndex.FirstAudioStream,
                    SourceReaderControlFlags.None,
                    out _,
                    out SourceReaderFlags flags,
                    out long timestamp);
                if ((flags & SourceReaderFlags.Endofstream) != 0)
                    break;
                if (sample == null)
                    continue;

                firstTimestamp = firstTimestamp < 0 ? timestamp : firstTimestamp;
                sample.SampleTime = Math.Max(0, timestamp - firstTimestamp);
                writer.WriteSample(input.OutputStreamIndex, sample);
            }
        }

        private sealed class AudioInput : IDisposable
        {
            public AudioInput(SourceReader reader, MediaType pcmType, int outputStreamIndex, string title)
            {
                Reader = reader;
                PcmType = pcmType;
                OutputStreamIndex = outputStreamIndex;
                Title = title;
            }

            public SourceReader Reader { get; }
            public MediaType PcmType { get; }
            public int OutputStreamIndex { get; }
            public string Title { get; }

            public void Dispose()
            {
                PcmType.Dispose();
                Reader.Dispose();
            }
        }
    }
}
