using System;
using System.Collections.Generic;
using System.Linq;

namespace Zink.Services.Recording
{
    public sealed class RollingAudioBuffer
    {
        private readonly object _gate = new();
        private readonly LinkedList<AudioPacket> _packets = new();
        private readonly TimeSpan _duration;
        private long _bufferedBytes;

        public RollingAudioBuffer(TimeSpan duration)
        {
            _duration = duration;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _packets.Count;
                }
            }
        }

        public long BufferedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _bufferedBytes;
                }
            }
        }

        public void Add(AudioPacket packet)
        {
            lock (_gate)
            {
                _packets.AddLast(packet);
                _bufferedBytes += packet.PcmData?.Length ?? 0;
                TrimNoLock();
            }
        }

        public List<AudioPacket> Snapshot()
        {
            lock (_gate)
            {
                return _packets.ToList();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                while (_packets.Count > 0)
                {
                    var packet = _packets.First!.Value;
                    _packets.RemoveFirst();
                    _bufferedBytes -= packet.PcmData?.Length ?? 0;
                    packet.PcmData = Array.Empty<byte>();
                }
                _bufferedBytes = 0;
            }
        }

        private void TrimNoLock()
        {
            if (_packets.Count == 0)
                return;

            var newest = _packets.Last!.Value.Timestamp;

            while (_packets.Count > 0)
            {
                var oldest = _packets.First!.Value.Timestamp;
                if ((newest - oldest) <= _duration)
                    break;

                var old = _packets.First!.Value;
                _packets.RemoveFirst();
                _bufferedBytes -= old.PcmData?.Length ?? 0;
                old.PcmData = Array.Empty<byte>();
            }
        }
    }
}
