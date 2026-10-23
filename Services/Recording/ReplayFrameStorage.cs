using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Zink.Services.NativeCalling;

namespace Zink.Services.Recording
{
    // Store the original encoded bytes, without decoding or recompressing them.
    // Reference-counted segments keep exports valid while capture expires old frames.
    internal sealed class ReplayFrameStorage : IDisposable
    {
        private Segment? _current;
        public Entry Append(H264EncodedFrame frame)
        {
            if (_current == null || _current.Length >= 16 * 1024 * 1024)
            {
                _current?.Release();
                _current = new Segment();
            }
            return _current.Append(frame);
        }
        public void Dispose() { _current?.Release(); _current = null; }

        internal sealed class Segment
        {
            private readonly FileStream _stream;
            private int _references = 1;
            public long Length { get; private set; }
            public Segment()
            {
                Directory.CreateDirectory(ReplayTemporaryFileCleanup.FolderPath);
                _stream = new FileStream(Path.Combine(ReplayTemporaryFileCleanup.FolderPath,
                    "encoded-" + Guid.NewGuid().ToString("N") + ".tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                    64 * 1024, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            }
            public Entry Append(H264EncodedFrame frame)
            {
                lock (_stream)
                {
                    long offset = Length;
                    _stream.Position = offset;
                    _stream.Write(frame.Data);
                    Length += frame.Data.Length;
                    Retain();
                    return new Entry(this, offset, frame.Data.Length, frame.IsKeyFrame, frame.TimestampMilliseconds);
                }
            }
            public byte[] Read(long offset, int length)
            {
                var bytes = new byte[length];
                lock (_stream)
                {
                    _stream.Position = offset;
                    _stream.ReadExactly(bytes);
                }
                return bytes;
            }
            public void Retain() => Interlocked.Increment(ref _references);
            public void Release() { if (Interlocked.Decrement(ref _references) == 0) _stream.Dispose(); }
        }

        internal sealed record Entry(Segment Segment, long Offset, int Length, bool IsKeyFrame, long TimestampMilliseconds)
        {
            public H264EncodedFrame Read() => new(Segment.Read(Offset, Length), IsKeyFrame, TimestampMilliseconds);
            public void Release() => Segment.Release();
        }

        internal sealed class Snapshot : IReadOnlyList<H264EncodedFrame>, IDisposable
        {
            private readonly Entry[] _entries;
            private int _disposed;
            public Snapshot(Entry[] entries)
            {
                _entries = entries;
                foreach (var entry in entries) entry.Segment.Retain();
            }
            public int Count => _entries.Length;
            public long TotalBytes
            {
                get { long bytes = 0; foreach (var entry in _entries) bytes += entry.Length; return bytes; }
            }
            public H264EncodedFrame this[int index] => _entries[index].Read();
            public IEnumerator<H264EncodedFrame> GetEnumerator()
            {
                foreach (var entry in _entries) yield return entry.Read();
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    foreach (var entry in _entries) entry.Release();
            }
        }
    }
}
