using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Zink.Services
{
    // Media Foundation may clone a source or open independent inputs while probing codecs.
    // The ordinary .NET AsRandomAccessStream adapter does not support CloneStream.
    internal sealed partial class OneDriveRandomAccessStream : IRandomAccessStream
    {
        private readonly OneDriveOriginalStream _stream;
        private readonly IInputStream _input;
        internal OneDriveRandomAccessStream(OneDriveOriginalStream stream)
        {
            _stream = stream;
            _input = stream.AsInputStream();
        }
        public bool CanRead => _stream.CanRead;
        public bool CanWrite => false;
        public ulong Position => checked((ulong)_stream.Position);
        public ulong Size { get => checked((ulong)_stream.Length); set => throw new NotSupportedException(); }
        public void Seek(ulong position) => _stream.Seek(checked((long)position), SeekOrigin.Begin);
        public IRandomAccessStream CloneStream() => new OneDriveRandomAccessStream(_stream.CloneAt(_stream.Position));
        public IInputStream GetInputStreamAt(ulong position) => _stream.CloneAt(checked((long)position)).AsInputStream();
        public IOutputStream GetOutputStreamAt(ulong position) => throw new NotSupportedException();
        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options) => _input.ReadAsync(buffer, count, options);
        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) => throw new NotSupportedException();
        public IAsyncOperation<bool> FlushAsync() => AsyncInfo.Run(_ => Task.FromResult(true));
        public void Dispose() => _input.Dispose();
    }
}
