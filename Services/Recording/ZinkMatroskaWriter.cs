using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zink.Services.NativeCalling;

namespace Zink.Services.Recording
{
    /// <summary>Zink-owned Matroska muxer. No external process or codec library is used.</summary>
    internal static class ZinkMatroskaWriter
    {
        private const ulong TimecodeScale = 1_000_000; // one millisecond

        public static void Write(
            IReadOnlyList<H264EncodedFrame> frames,
            int width,
            int height,
            double frameRate,
            bool hevc,
            string? combinedWavePath,
            string? desktopWavePath,
            string? microphoneWavePath,
            string outputPath)
        {
            if (frames == null || frames.Count == 0)
                throw new InvalidOperationException("No encoded replay frames were supplied to the Matroska writer.");

            var video = VideoCodecData.Create(frames, hevc);
            var audioTracks = new List<WaveData>();
            AddWave(combinedWavePath, "Combined audio");
            AddWave(desktopWavePath, "Desktop and game audio");
            AddWave(microphoneWavePath, "Microphone");

            string? folder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(folder))
                throw new InvalidOperationException("The Matroska output folder is invalid.");
            Directory.CreateDirectory(folder);

            string stagingPath = outputPath + ".partial";
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);

            try
            {
                using (var stream = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    var ebml = new EbmlWriter(stream);
                    ebml.Master(0x1A45DFA3, header =>
                    {
                        header.UInt(0x4286, 1);
                        header.UInt(0x42F7, 1);
                        header.UInt(0x42F2, 4);
                        header.UInt(0x42F3, 8);
                        header.String(0x4282, "matroska");
                        header.UInt(0x4287, 4);
                        header.UInt(0x4285, 2);
                    });

                    ebml.Id(0x18538067);
                    ebml.UnknownSize();

                double durationMs = Math.Max(
                    frames[^1].TimestampMilliseconds - frames[0].TimestampMilliseconds + (1000.0 / Math.Max(1, frameRate)),
                    audioTracks.Select(track => track.Duration.TotalMilliseconds).DefaultIfEmpty(0).Max());
                    ebml.Master(0x1549A966, info =>
                    {
                    info.UInt(0x2AD7B1, TimecodeScale);
                    info.Utf8(0x4D80, "Zink Matroska Writer");
                    info.Utf8(0x5741, "Zink");
                    info.Float64(0x4489, durationMs);
                    });

                    ebml.Master(0x1654AE6B, tracks =>
                    {
                    tracks.Master(0xAE, track =>
                    {
                        track.UInt(0xD7, 1);
                        track.UInt(0x73C5, 1);
                        track.UInt(0x83, 1);
                        track.UInt(0x9C, 0);
                        track.String(0x86, hevc ? "V_MPEGH/ISO/HEVC" : "V_MPEG4/ISO/AVC");
                        track.Binary(0x63A2, video.CodecPrivate);
                        track.UInt(0x23E383, (ulong)Math.Max(1, Math.Round(1_000_000_000.0 / Math.Max(1, frameRate))));
                        track.Master(0xE0, v =>
                        {
                            v.UInt(0xB0, (ulong)width);
                            v.UInt(0xBA, (ulong)height);
                        });
                    });

                    for (int audioIndex = 0; audioIndex < audioTracks.Count; audioIndex++)
                    {
                        WaveData audio = audioTracks[audioIndex];
                        int trackNumber = audioIndex + 2;
                        tracks.Master(0xAE, track =>
                        {
                            track.UInt(0xD7, (ulong)trackNumber);
                            track.UInt(0x73C5, (ulong)trackNumber);
                            track.UInt(0x83, 2);
                            track.UInt(0x9C, 0);
                            track.String(0x86, "A_PCM/INT/LIT");
                            track.Utf8(0x536E, audio.Name);
                            track.Master(0xE1, a =>
                            {
                                a.Float64(0xB5, audio.SampleRate);
                                a.UInt(0x9F, (ulong)audio.Channels);
                                a.UInt(0x6264, (ulong)audio.BitsPerSample);
                            });
                        });
                    }
                    });

                    WriteClusters(ebml, frames, video, audioTracks);
                    stream.Flush(true);
                }

                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                File.Move(stagingPath, outputPath);
            }
            catch
            {
                if (File.Exists(stagingPath))
                    File.Delete(stagingPath);
                throw;
            }

            void AddWave(string? path, string name)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    audioTracks.Add(WaveData.Read(path, name));
            }
        }

        private static void WriteClusters(
            EbmlWriter writer,
            IReadOnlyList<H264EncodedFrame> frames,
            VideoCodecData video,
            IReadOnlyList<WaveData> audioTracks)
        {
            long videoOrigin = frames[0].TimestampMilliseconds;
            var samples = new List<ClusterSample>(frames.Count + audioTracks.Sum(track => track.ChunkCount));
            for (int i = 0; i < frames.Count; i++)
            {
                long time = Math.Max(0, frames[i].TimestampMilliseconds - videoOrigin);
                samples.Add(new ClusterSample(time, 1, frames[i].IsKeyFrame, video.ConvertFrame(frames[i].Data)));
            }

            for (int audioIndex = 0; audioIndex < audioTracks.Count; audioIndex++)
            {
                foreach (var chunk in audioTracks[audioIndex].Chunks())
                    samples.Add(new ClusterSample(chunk.TimestampMilliseconds, audioIndex + 2, false, chunk.Data));
            }

            samples.Sort((a, b) => a.TimestampMilliseconds.CompareTo(b.TimestampMilliseconds));
            foreach (var group in samples.GroupBy(sample => sample.TimestampMilliseconds / 30_000))
            {
                long clusterTime = group.Key * 30_000;
                writer.Master(0x1F43B675, cluster =>
                {
                    cluster.UInt(0xE7, (ulong)clusterTime);
                    foreach (ClusterSample sample in group)
                    {
                        int relative = checked((int)(sample.TimestampMilliseconds - clusterTime));
                        cluster.SimpleBlock(sample.TrackNumber, (short)relative, sample.KeyFrame, sample.Data);
                    }
                });
            }
        }

        private readonly record struct ClusterSample(long TimestampMilliseconds, int TrackNumber, bool KeyFrame, byte[] Data);

        private sealed class VideoCodecData
        {
            private readonly bool _hevc;
            public byte[] CodecPrivate { get; }

            private VideoCodecData(bool hevc, byte[] codecPrivate)
            {
                _hevc = hevc;
                CodecPrivate = codecPrivate;
            }

            public static VideoCodecData Create(IReadOnlyList<H264EncodedFrame> frames, bool hevc)
            {
                var nals = frames.SelectMany(frame => SplitAnnexB(frame.Data)).ToList();
                byte[] codecPrivate = hevc ? BuildHevcPrivate(nals) : BuildAvcPrivate(nals);
                return new VideoCodecData(hevc, codecPrivate);
            }

            public byte[] ConvertFrame(byte[] frame)
            {
                var nals = SplitAnnexB(frame).ToList();
                if (nals.Count == 0)
                    return frame;

                int length = nals.Sum(nal => 4 + nal.Length);
                byte[] result = new byte[length];
                int offset = 0;
                foreach (byte[] nal in nals)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset, 4), (uint)nal.Length);
                    offset += 4;
                    nal.CopyTo(result, offset);
                    offset += nal.Length;
                }
                return result;
            }

            private static byte[] BuildAvcPrivate(IReadOnlyList<byte[]> nals)
            {
                byte[] sps = nals.FirstOrDefault(n => n.Length > 3 && (n[0] & 0x1F) == 7)
                    ?? throw new InvalidOperationException("The H.264 replay stream has no SPS.");
                byte[] pps = nals.FirstOrDefault(n => n.Length > 0 && (n[0] & 0x1F) == 8)
                    ?? throw new InvalidOperationException("The H.264 replay stream has no PPS.");
                using var data = new MemoryStream();
                data.WriteByte(1);
                data.WriteByte(sps[1]); data.WriteByte(sps[2]); data.WriteByte(sps[3]);
                data.WriteByte(0xFF); data.WriteByte(0xE1);
                WriteUInt16(data, sps.Length); data.Write(sps);
                data.WriteByte(1); WriteUInt16(data, pps.Length); data.Write(pps);
                return data.ToArray();
            }

            private static byte[] BuildHevcPrivate(IReadOnlyList<byte[]> nals)
            {
                byte[] vps = FindHevc(nals, 32);
                byte[] sps = FindHevc(nals, 33);
                byte[] pps = FindHevc(nals, 34);
                using var data = new MemoryStream();
                byte[] header = { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 120, 0xF0, 0, 0xFC, 0xFD, 0xF8, 0xF8, 0, 0, 0x0F, 3 };
                data.Write(header);
                WriteHevcArray(data, 32, vps); WriteHevcArray(data, 33, sps); WriteHevcArray(data, 34, pps);
                return data.ToArray();
            }

            private static byte[] FindHevc(IReadOnlyList<byte[]> nals, int type) =>
                nals.FirstOrDefault(n => n.Length > 1 && ((n[0] >> 1) & 0x3F) == type)
                ?? throw new InvalidOperationException($"The HEVC replay stream has no parameter set {type}.");

            private static void WriteHevcArray(Stream stream, byte type, byte[] nal)
            {
                stream.WriteByte((byte)(0x80 | type)); WriteUInt16(stream, 1); WriteUInt16(stream, nal.Length); stream.Write(nal);
            }

            private static IEnumerable<byte[]> SplitAnnexB(byte[] data)
            {
                int search = 0;
                while (TryStart(data, search, out int start, out int prefix))
                {
                    int payload = start + prefix;
                    int next = TryStart(data, payload, out int nextStart, out _) ? nextStart : data.Length;
                    if (next > payload)
                        yield return data.AsSpan(payload, next - payload).ToArray();
                    search = next;
                }
            }

            private static bool TryStart(byte[] data, int from, out int index, out int length)
            {
                for (int i = Math.Max(0, from); i + 3 < data.Length; i++)
                {
                    if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                    { index = i; length = 3; return true; }
                    if (i + 4 < data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                    { index = i; length = 4; return true; }
                }
                index = length = 0; return false;
            }

            private static void WriteUInt16(Stream stream, int value)
            {
                stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
            }
        }

        private sealed class WaveData
        {
            public byte[] Pcm { get; private init; } = Array.Empty<byte>();
            public int SampleRate { get; private init; }
            public int Channels { get; private init; }
            public int BitsPerSample { get; private init; }
            public string Name { get; private init; } = "Audio";
            public int BlockAlign => Channels * BitsPerSample / 8;
            public TimeSpan Duration => TimeSpan.FromSeconds(Pcm.Length / (double)(SampleRate * BlockAlign));
            public int ChunkCount => (int)Math.Ceiling(Pcm.Length / (double)Math.Max(BlockAlign, SampleRate * BlockAlign / 50));

            public static WaveData Read(string path, string name)
            {
                using var input = new BinaryReader(File.OpenRead(path));
                if (new string(input.ReadChars(4)) != "RIFF") throw new InvalidDataException("Invalid RIFF wave file.");
                input.ReadUInt32();
                if (new string(input.ReadChars(4)) != "WAVE") throw new InvalidDataException("Invalid wave file.");
                int channels = 0, rate = 0, bits = 0; byte[]? pcm = null;
                while (input.BaseStream.Position + 8 <= input.BaseStream.Length)
                {
                    string id = new string(input.ReadChars(4)); int size = input.ReadInt32(); long next = input.BaseStream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        ushort format = input.ReadUInt16(); channels = input.ReadUInt16(); rate = input.ReadInt32();
                        input.ReadInt32(); input.ReadUInt16(); bits = input.ReadUInt16();
                        if (format != 1 && format != 0xFFFE) throw new InvalidDataException("Only PCM wave input is supported.");
                    }
                    else if (id == "data") pcm = input.ReadBytes(size);
                    input.BaseStream.Position = Math.Min(next, input.BaseStream.Length);
                }
                if (pcm == null || channels < 1 || channels > 8 || rate <= 0 || bits != 16)
                    throw new InvalidDataException("The replay wave format is not supported.");
                return new WaveData { Pcm = pcm, Channels = channels, SampleRate = rate, BitsPerSample = bits, Name = name };
            }

            public IEnumerable<(long TimestampMilliseconds, byte[] Data)> Chunks()
            {
                int chunk = Math.Max(BlockAlign, (SampleRate * BlockAlign / 50 / BlockAlign) * BlockAlign);
                for (int offset = 0; offset < Pcm.Length; offset += chunk)
                {
                    int count = Math.Min(chunk, Pcm.Length - offset);
                    byte[] data = new byte[count]; Buffer.BlockCopy(Pcm, offset, data, 0, count);
                    yield return ((long)(offset * 1000.0 / (SampleRate * BlockAlign)), data);
                }
            }
        }

        private sealed class EbmlWriter
        {
            private readonly Stream _stream;
            public EbmlWriter(Stream stream) => _stream = stream;
            public void Master(uint id, Action<EbmlWriter> content)
            {
                using var buffer = new MemoryStream(); content(new EbmlWriter(buffer)); Id(id); Size((ulong)buffer.Length); buffer.Position = 0; buffer.CopyTo(_stream);
            }
            public void UInt(uint id, ulong value) { int bytes = value == 0 ? 1 : (64 - System.Numerics.BitOperations.LeadingZeroCount(value) + 7) / 8; Id(id); Size((ulong)bytes); for (int i = bytes - 1; i >= 0; i--) _stream.WriteByte((byte)(value >> (i * 8))); }
            public void String(uint id, string value) => Binary(id, System.Text.Encoding.ASCII.GetBytes(value));
            public void Utf8(uint id, string value) => Binary(id, System.Text.Encoding.UTF8.GetBytes(value));
            public void Binary(uint id, byte[] value) { Id(id); Size((ulong)value.Length); _stream.Write(value); }
            public void Float64(uint id, double value) { Id(id); Size(8); Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, BitConverter.DoubleToInt64Bits(value)); _stream.Write(b); }
            public void SimpleBlock(int track, short timecode, bool key, byte[] data) { Id(0xA3); Size((ulong)(4 + data.Length)); _stream.WriteByte((byte)(0x80 | track)); _stream.WriteByte((byte)(timecode >> 8)); _stream.WriteByte((byte)timecode); _stream.WriteByte(key ? (byte)0x80 : (byte)0); _stream.Write(data); }
            public void UnknownSize() { _stream.WriteByte(0x01); for (int i = 0; i < 7; i++) _stream.WriteByte(0xFF); }
            public void Id(uint id) { int bytes = id <= 0xFF ? 1 : id <= 0xFFFF ? 2 : id <= 0xFFFFFF ? 3 : 4; for (int i = bytes - 1; i >= 0; i--) _stream.WriteByte((byte)(id >> (i * 8))); }
            private void Size(ulong value) { int width = 1; while (width < 8 && value >= ((1UL << (7 * width)) - 1)) width++; ulong encoded = value | (1UL << (7 * width)); for (int i = width - 1; i >= 0; i--) _stream.WriteByte((byte)(encoded >> (i * 8))); }
        }
    }
}
