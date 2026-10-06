using System;
using System.Collections.Generic;
using System.Linq;

namespace Zink.Services.Recording
{
    public sealed class ShiftedVideoFrameRef
    {
        public VideoFramePacket Source { get; }
        public TimeSpan ShiftedTimestamp { get; }

        public ShiftedVideoFrameRef(VideoFramePacket source, TimeSpan shiftedTimestamp)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            ShiftedTimestamp = shiftedTimestamp;
        }
    }

    public static class AudioMixHelpers
    {
        public const int TargetSampleRate = 48000;
        public const int TargetChannels = 2;
        public const int TargetBitsPerSample = 16;

        public static List<AudioPacket> ShiftAudio(IReadOnlyList<AudioPacket>? packets, TimeSpan origin)
        {
            if (packets is null || packets.Count == 0)
                return new List<AudioPacket>();

            return packets
                .Select(p => new AudioPacket
                {
                    Timestamp = p.Timestamp - origin,
                    PcmData = p.PcmData,
                    SampleRate = p.SampleRate,
                    Channels = p.Channels,
                    BitsPerSample = p.BitsPerSample
                })
                .Where(p => p.Timestamp >= TimeSpan.Zero)
                .OrderBy(p => p.Timestamp)
                .ToList();
        }

        public static List<ShiftedVideoFrameRef> ShiftVideo(IReadOnlyList<VideoFramePacket> frames, TimeSpan origin)
        {
            if (frames is null || frames.Count == 0)
                return new List<ShiftedVideoFrameRef>();

            return frames
                .Select(f => new ShiftedVideoFrameRef(f, f.Timestamp - origin))
                .Where(f => f.ShiftedTimestamp >= TimeSpan.Zero)
                .OrderBy(f => f.ShiftedTimestamp)
                .ToList();
        }

        public static TimeSpan ComputeCommonOrigin(
            IReadOnlyList<VideoFramePacket> videoFrames,
            IReadOnlyList<AudioPacket>? systemPackets,
            IReadOnlyList<AudioPacket>? micPackets)
        {
            if (videoFrames.Count > 0)
                return videoFrames.Min(v => v.Timestamp);

            var values = new List<TimeSpan>();

            if (systemPackets is not null && systemPackets.Count > 0)
                values.Add(systemPackets.Min(p => p.Timestamp));

            if (micPackets is not null && micPackets.Count > 0)
                values.Add(micPackets.Min(p => p.Timestamp));

            return values.Count == 0 ? TimeSpan.Zero : values.Min();
        }

        public static List<AudioPacket> NormalizePackets(IReadOnlyList<AudioPacket>? packets)
        {
            if (packets is null || packets.Count == 0)
                return new List<AudioPacket>();

            var normalized = new List<AudioPacket>(packets.Count);

            foreach (var packet in packets.OrderBy(p => p.Timestamp))
            {
                normalized.Add(NormalizePacket(packet));
            }

            return normalized;
        }

        public static List<AudioPacket> MixPcm16(
            IReadOnlyList<AudioPacket>? systemPackets,
            IReadOnlyList<AudioPacket>? micPackets)
        {
            int outputChannels = systemPackets?
                .Where(packet => packet.Channels >= 1 && packet.Channels <= 8)
                .Select(packet => packet.Channels)
                .DefaultIfEmpty(TargetChannels)
                .Max() ?? TargetChannels;
            var normalizedSystem = NormalizePackets(systemPackets, outputChannels);
            var normalizedMic = NormalizePackets(micPackets, outputChannels);

            if (normalizedSystem.Count == 0 && normalizedMic.Count == 0)
                return new List<AudioPacket>();

            byte[]? systemTrack = normalizedSystem.Count > 0
                ? BuildContinuousTrack(normalizedSystem, outputChannels)
                : null;

            byte[]? micTrack = normalizedMic.Count > 0
                ? BuildContinuousTrack(normalizedMic, outputChannels)
                : null;

            if (systemTrack is null || systemTrack.Length == 0)
            {
                return new List<AudioPacket>
                {
                    new AudioPacket
                    {
                        Timestamp = TimeSpan.Zero,
                        PcmData = micTrack ?? Array.Empty<byte>(),
                        SampleRate = TargetSampleRate,
                        Channels = outputChannels,
                        BitsPerSample = TargetBitsPerSample
                    }
                };
            }

            if (micTrack is null || micTrack.Length == 0)
            {
                return new List<AudioPacket>
                {
                    new AudioPacket
                    {
                        Timestamp = TimeSpan.Zero,
                        PcmData = systemTrack,
                        SampleRate = TargetSampleRate,
                        Channels = outputChannels,
                        BitsPerSample = TargetBitsPerSample
                    }
                };
            }

            int byteCount = Math.Max(systemTrack.Length, micTrack.Length);
            if ((byteCount & 1) != 0)
                byteCount--;

            byte[] mixed = new byte[byteCount];

            for (int i = 0; i < byteCount; i += 2)
            {
                short sa = i + 1 < systemTrack.Length
                    ? BitConverter.ToInt16(systemTrack, i)
                    : (short)0;

                short sb = i + 1 < micTrack.Length
                    ? BitConverter.ToInt16(micTrack, i)
                    : (short)0;

                int sum = sa + sb;

                if (sum > short.MaxValue) sum = short.MaxValue;
                if (sum < short.MinValue) sum = short.MinValue;

                short s = (short)sum;
                mixed[i] = (byte)(s & 0xFF);
                mixed[i + 1] = (byte)((s >> 8) & 0xFF);
            }

            return new List<AudioPacket>
            {
                new AudioPacket
                {
                    Timestamp = TimeSpan.Zero,
                    PcmData = mixed,
                    SampleRate = TargetSampleRate,
                    Channels = outputChannels,
                    BitsPerSample = TargetBitsPerSample
                }
            };
        }

        public static AudioPacket NormalizePacket(AudioPacket packet)
            => NormalizePacket(packet, TargetChannels);

        private static List<AudioPacket> NormalizePackets(
            IReadOnlyList<AudioPacket>? packets,
            int targetChannels)
        {
            if (packets is null || packets.Count == 0)
                return new List<AudioPacket>();

            return packets
                .OrderBy(packet => packet.Timestamp)
                .Select(packet => NormalizePacket(packet, targetChannels))
                .ToList();
        }

        private static AudioPacket NormalizePacket(AudioPacket packet, int targetChannels)
        {
            if (packet is null)
                throw new ArgumentNullException(nameof(packet));

            short[] sourceSamples = ConvertToInt16Samples(packet);
            short[] channelNormalized;

            if (targetChannels < 1 || targetChannels > 8)
                throw new InvalidOperationException($"Unsupported target channel count: {targetChannels}");

            if (packet.Channels == targetChannels)
            {
                channelNormalized = sourceSamples;
            }
            else if (packet.Channels == 1)
            {
                channelNormalized = MonoToChannels(sourceSamples, targetChannels);
            }
            else if (packet.Channels == 2 && targetChannels > 2)
            {
                channelNormalized = StereoToMultichannel(sourceSamples, targetChannels);
            }
            else if (targetChannels == 2)
            {
                if (packet.Channels == 1)
                {
                    channelNormalized = MonoToStereo(sourceSamples);
                }
                else if (packet.Channels == 2)
                {
                    channelNormalized = sourceSamples;
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported channel count: {packet.Channels}");
                }
            }
            else if (targetChannels == 1)
            {
                if (packet.Channels == 1)
                {
                    channelNormalized = sourceSamples;
                }
                else if (packet.Channels == 2)
                {
                    channelNormalized = StereoToMono(sourceSamples);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported channel count: {packet.Channels}");
                }
            }
            else
            {
                throw new InvalidOperationException($"Unsupported channel conversion: {packet.Channels} to {targetChannels}");
            }

            short[] resampled = packet.SampleRate == TargetSampleRate
                ? channelNormalized
                : ResampleLinear(
                    channelNormalized,
                    packet.SampleRate,
                    TargetSampleRate,
                    targetChannels);

            return new AudioPacket
            {
                Timestamp = packet.Timestamp,
                PcmData = Int16ToBytes(resampled),
                SampleRate = TargetSampleRate,
                Channels = targetChannels,
                BitsPerSample = TargetBitsPerSample
            };
        }

        private static byte[] BuildContinuousTrack(IReadOnlyList<AudioPacket> packets, int channels)
        {
            if (packets.Count == 0)
                return Array.Empty<byte>();

            int blockAlign = channels * (TargetBitsPerSample / 8);
            int byteRate = TargetSampleRate * blockAlign;

            long totalBytes = 0;

            foreach (var packet in packets)
            {
                long offset = TimeToAlignedByteOffset(packet.Timestamp, byteRate, blockAlign);
                long end = offset + packet.PcmData.Length;
                if (end > totalBytes)
                    totalBytes = end;
            }

            if (totalBytes <= 0)
                return Array.Empty<byte>();

            if (totalBytes > int.MaxValue)
                throw new InvalidOperationException("Audio track is too large to build in memory.");

            byte[] track = new byte[(int)totalBytes];

            foreach (var packet in packets)
            {
                long offset = TimeToAlignedByteOffset(packet.Timestamp, byteRate, blockAlign);
                int dst = checked((int)offset);
                int copyCount = Math.Min(packet.PcmData.Length, track.Length - dst);

                if (copyCount <= 0)
                    continue;

                // If packets overlap slightly because of capture jitter, preserve continuity by overwriting.
                Buffer.BlockCopy(packet.PcmData, 0, track, dst, copyCount);
            }

            return TrimTrailingSilence(track, blockAlign);
        }

        private static long TimeToAlignedByteOffset(TimeSpan timestamp, int byteRate, int blockAlign)
        {
            if (timestamp < TimeSpan.Zero)
                timestamp = TimeSpan.Zero;

            long bytes = (long)Math.Round(timestamp.TotalSeconds * byteRate, MidpointRounding.AwayFromZero);

            long remainder = bytes % blockAlign;
            if (remainder != 0)
                bytes -= remainder;

            return Math.Max(0, bytes);
        }

        private static byte[] TrimTrailingSilence(byte[] data, int blockAlign)
        {
            if (data.Length == 0)
                return data;

            int lastNonZero = -1;
            for (int i = data.Length - 1; i >= 0; i--)
            {
                if (data[i] != 0)
                {
                    lastNonZero = i;
                    break;
                }
            }

            if (lastNonZero < 0)
                return Array.Empty<byte>();

            int newLength = lastNonZero + 1;
            int remainder = newLength % blockAlign;
            if (remainder != 0)
                newLength += blockAlign - remainder;

            if (newLength > data.Length)
                newLength = data.Length;

            if (newLength == data.Length)
                return data;

            byte[] trimmed = new byte[newLength];
            Buffer.BlockCopy(data, 0, trimmed, 0, newLength);
            return trimmed;
        }

        private static short[] ConvertToInt16Samples(AudioPacket packet)
        {
            if (packet.PcmData is null || packet.PcmData.Length == 0)
                return Array.Empty<short>();

            if (packet.BitsPerSample == 16)
                return BytesToInt16(packet.PcmData);

            if (packet.BitsPerSample == 32)
            {
                if (packet.PcmData.Length % 4 != 0)
                    throw new InvalidOperationException("32-bit audio packet size is invalid.");

                return BytesFloat32ToInt16(packet.PcmData);
            }

            throw new InvalidOperationException(
                $"Unsupported audio bit depth: {packet.BitsPerSample}. Only 16-bit PCM and 32-bit float are supported.");
        }

        private static short[] BytesToInt16(byte[] data)
        {
            if (data is null || data.Length == 0)
                return Array.Empty<short>();

            int sampleCount = data.Length / 2;
            short[] samples = new short[sampleCount];
            Buffer.BlockCopy(data, 0, samples, 0, sampleCount * 2);
            return samples;
        }

        private static short[] BytesFloat32ToInt16(byte[] data)
        {
            const float conversionHeadroom = 0.7079458f; // -3 dB prevents hard clipping and AAC overshoot.
            int sampleCount = data.Length / 4;
            short[] samples = new short[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float value = BitConverter.ToSingle(data, i * 4);

                if (float.IsNaN(value) || float.IsInfinity(value))
                    value = 0f;

                if (value > 1f) value = 1f;
                if (value < -1f) value = -1f;

                samples[i] = (short)Math.Round(value * conversionHeadroom * short.MaxValue);
            }

            return samples;
        }

        private static byte[] Int16ToBytes(short[] samples)
        {
            if (samples is null || samples.Length == 0)
                return Array.Empty<byte>();

            byte[] data = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, data, 0, data.Length);
            return data;
        }

        private static short[] MonoToStereo(short[] monoSamples)
        {
            if (monoSamples.Length == 0)
                return Array.Empty<short>();

            short[] stereo = new short[monoSamples.Length * 2];

            for (int i = 0; i < monoSamples.Length; i++)
            {
                short s = monoSamples[i];
                int dst = i * 2;
                stereo[dst] = s;
                stereo[dst + 1] = s;
            }

            return stereo;
        }

        private static short[] MonoToChannels(short[] monoSamples, int channels)
        {
            if (monoSamples.Length == 0)
                return Array.Empty<short>();

            short[] output = new short[monoSamples.Length * channels];
            for (int frame = 0; frame < monoSamples.Length; frame++)
            {
                // A microphone is a front-centre source. Duplicate it into the
                // front pair while leaving spatial surround channels untouched.
                output[frame * channels] = monoSamples[frame];
                if (channels > 1)
                    output[(frame * channels) + 1] = monoSamples[frame];
            }
            return output;
        }

        private static short[] StereoToMultichannel(short[] stereoSamples, int channels)
        {
            int frameCount = stereoSamples.Length / 2;
            short[] output = new short[frameCount * channels];
            for (int frame = 0; frame < frameCount; frame++)
            {
                output[frame * channels] = stereoSamples[frame * 2];
                output[(frame * channels) + 1] = stereoSamples[(frame * 2) + 1];
            }
            return output;
        }

        private static short[] StereoToMono(short[] stereoSamples)
        {
            if (stereoSamples.Length < 2)
                return Array.Empty<short>();

            int frameCount = stereoSamples.Length / 2;
            short[] mono = new short[frameCount];

            for (int i = 0; i < frameCount; i++)
            {
                int src = i * 2;
                int avg = (stereoSamples[src] + stereoSamples[src + 1]) / 2;
                mono[i] = (short)avg;
            }

            return mono;
        }

        private static short[] ResampleLinear(short[] input, int sourceRate, int targetRate, int channels)
        {
            if (input.Length == 0)
                return Array.Empty<short>();

            if (sourceRate <= 0 || targetRate <= 0)
                throw new InvalidOperationException("Invalid sample rate for resampling.");

            if (channels <= 0)
                throw new InvalidOperationException("Invalid channel count for resampling.");

            int inputFrameCount = input.Length / channels;
            if (inputFrameCount <= 1)
                return input;

            int outputFrameCount = (int)Math.Round(inputFrameCount * (targetRate / (double)sourceRate));
            if (outputFrameCount <= 0)
                return Array.Empty<short>();

            short[] output = new short[outputFrameCount * channels];

            for (int outFrame = 0; outFrame < outputFrameCount; outFrame++)
            {
                double srcPosition = outFrame * (sourceRate / (double)targetRate);
                int srcIndex0 = (int)Math.Floor(srcPosition);
                int srcIndex1 = Math.Min(srcIndex0 + 1, inputFrameCount - 1);
                double frac = srcPosition - srcIndex0;

                for (int ch = 0; ch < channels; ch++)
                {
                    short s0 = input[srcIndex0 * channels + ch];
                    short s1 = input[srcIndex1 * channels + ch];

                    double interpolated = s0 + ((s1 - s0) * frac);

                    if (interpolated > short.MaxValue) interpolated = short.MaxValue;
                    if (interpolated < short.MinValue) interpolated = short.MinValue;

                    output[outFrame * channels + ch] = (short)Math.Round(interpolated);
                }
            }

            return output;
        }
    }
}
