// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.TestHost
{
    // This steam accepts writes from the server/app, buffers them internally, and returns the data via Reads
    // when requested by the client.
    internal class ResponseStream : Stream
    {
        private bool _complete;
        private bool _aborted;
        private Exception _abortException;
        private SemaphoreSlim _writeLock;

        private Func<Task> _onFirstWriteAsync;
        private bool _firstWrite;
        private Action _abortRequest;

        private Pipe _pipe = new Pipe();

        internal ResponseStream(Func<Task> onFirstWriteAsync, Action abortRequest)
        {
            _onFirstWriteAsync = onFirstWriteAsync ?? throw new ArgumentNullException(nameof(onFirstWriteAsync));
            _abortRequest = abortRequest ?? throw new ArgumentNullException(nameof(abortRequest));
            _firstWrite = true;
            _writeLock = new SemaphoreSlim(1, 1);
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        #region NotSupported

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        #endregion NotSupported

        public override void Flush()
        {
            FlushAsync().GetAwaiter().GetResult();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckNotComplete();

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await FirstWriteAsync();
                await _pipe.Writer.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public async override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            VerifyBuffer(buffer, offset, count, allowEmpty: false);
            CheckAborted();
            var registration = cancellationToken.Register(Abort);
            try
            {
                // TODO: Usability issue. Flush or zero byte write causes ReadAsync to complete without data so I have to call ReadAsync in a loop.
                while (true)
                {
                    var result = await _pipe.Reader.ReadAsync(cancellationToken);

                    var readableBuffer = result.Buffer;
                    if (!readableBuffer.IsEmpty)
                    {
                        var actual = Math.Min(readableBuffer.Length, count);
                        readableBuffer = readableBuffer.Slice(0, actual);
                        readableBuffer.CopyTo(buffer);
                        _pipe.Reader.AdvanceTo(readableBuffer.End, readableBuffer.End);
                        return (int)actual;
                    }

                    if (result.IsCompleted)
                    {
                        return 0;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Debug.Assert(!result.IsCanceled); // It should only be canceled by cancellationToken.

                    // Try again. TODO: I shouldn't need to do this, there wasn't any data.
                    _pipe.Reader.AdvanceTo(readableBuffer.End, readableBuffer.End);
                }
            }
            finally
            {
                registration.Dispose();
            }
        }

        // Called under write-lock.
        private Task FirstWriteAsync()
        {
            if (_firstWrite)
            {
                _firstWrite = false;
                return _onFirstWriteAsync();
            }
            return Task.FromResult(true);
        }

        // Write with count 0 will still trigger OnFirstWrite
        public override void Write(byte[] buffer, int offset, int count)
        {
            VerifyBuffer(buffer, offset, count, allowEmpty: true);
            CheckNotComplete();

            _writeLock.Wait();
            try
            {
                FirstWriteAsync().GetAwaiter().GetResult();
                _pipe.Writer.Write(new ReadOnlySpan<byte>(buffer, offset, count));
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
        {
            Write(buffer, offset, count);
            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>(state);
            tcs.TrySetResult(null);
            IAsyncResult result = tcs.Task;
            callback?.Invoke(result);
            return result;
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            VerifyBuffer(buffer, offset, count, allowEmpty: true);
            CheckNotComplete();

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await FirstWriteAsync();
                await _pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static void VerifyBuffer(byte[] buffer, int offset, int count, bool allowEmpty)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("offset", offset, string.Empty);
            }
            if (count < 0 || count > buffer.Length - offset
                || (!allowEmpty && count == 0))
            {
                throw new ArgumentOutOfRangeException("count", count, string.Empty);
            }
        }

        internal void Abort()
        {
            Abort(new OperationCanceledException());
        }

        internal void Abort(Exception innerException)
        {
            Contract.Requires(innerException != null);
            _aborted = true;
            _abortException = innerException;
            _complete = true;
            _pipe.Writer.Complete(new IOException(string.Empty, innerException));
        }

        internal void Complete()
        {
            // If HttpClient.Dispose gets called while HttpClient.SetTask...() is called
            // there is a chance that this method will be called twice and hang on the lock
            // to prevent this we can check if there is already a thread inside the lock
            if (_complete)
            {
                return;
            }

            // Throw for further writes, but not reads.  Allow reads to drain the buffered data and then return 0 for further reads.
            _complete = true;
            _pipe.Writer.Complete();
        }

        private void CheckAborted()
        {
            if (_aborted)
            {
                throw new IOException(string.Empty, _abortException);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _abortRequest();
            }
            base.Dispose(disposing);
        }

        private void CheckNotComplete()
        {
            if (_complete)
            {
                throw new IOException("The request was aborted or the pipeline has finished");
            }
        }
    }
}
