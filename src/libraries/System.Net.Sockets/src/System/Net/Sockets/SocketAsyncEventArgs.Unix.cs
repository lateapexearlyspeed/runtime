// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace System.Net.Sockets
{
    public partial class SocketAsyncEventArgs : EventArgs, IDisposable
    {
        private IntPtr _acceptedFileDescriptor;
        private int _socketAddressSize;
        private SocketFlags _receivedFlags;
        private Action<int, byte[]?, int, SocketFlags, SocketError>? _transferCompletionCallback;

        partial void InitializeInternals();

        partial void FreeInternals();

        partial void SetupMultipleBuffers();

        partial void CompleteCore();

        private void AcceptCompletionCallback(IntPtr acceptedFileDescriptor, byte[] socketAddress, int socketAddressSize, SocketError socketError)
        {
            CompleteAcceptOperation(acceptedFileDescriptor, socketAddress, socketAddressSize);

            CompletionCallback(0, SocketFlags.None, socketError);
        }

        private void CompleteAcceptOperation(IntPtr acceptedFileDescriptor, byte[] socketAddress, int socketAddressSize)
        {
            _acceptedFileDescriptor = acceptedFileDescriptor;
            Debug.Assert(socketAddress == null || socketAddress == _acceptBuffer, $"Unexpected socketAddress: {socketAddress}");
            _acceptAddressBufferCount = socketAddressSize;
        }

        internal unsafe SocketError DoOperationAccept(Socket _ /*socket*/, SafeSocketHandle handle, SafeSocketHandle? acceptHandle, CancellationToken cancellationToken)
        {
            if (!_buffer.Equals(default))
            {
                throw new PlatformNotSupportedException(SR.net_sockets_accept_receive_notsupported);
            }

            _acceptedFileDescriptor = (IntPtr)(-1);

            Debug.Assert(acceptHandle == null, $"Unexpected acceptHandle: {acceptHandle}");

            IntPtr acceptedFd;
            int socketAddressLen = _acceptAddressBufferCount / 2;
            SocketError socketError = handle.AsyncContext.AcceptAsync(_acceptBuffer!, ref socketAddressLen, out acceptedFd, AcceptCompletionCallback, cancellationToken);

            if (socketError != SocketError.IOPending)
            {
                CompleteAcceptOperation(acceptedFd, _acceptBuffer!, socketAddressLen);
                FinishOperationSync(socketError, 0, SocketFlags.None);
            }

            return socketError;
        }

        private void ConnectCompletionCallback(SocketError socketError)
        {
            CompletionCallback(0, SocketFlags.None, socketError);
        }

        internal unsafe SocketError DoOperationConnectEx(Socket _ /*socket*/, SafeSocketHandle handle)
            => DoOperationConnect(handle);

        internal unsafe SocketError DoOperationConnect(SafeSocketHandle handle)
        {
            SocketError socketError = handle.AsyncContext.ConnectAsync(_socketAddress!.Buffer, _socketAddress.Size, ConnectCompletionCallback);
            if (socketError != SocketError.IOPending)
            {
                FinishOperationSync(socketError, 0, SocketFlags.None);
            }
            return socketError;
        }

        internal SocketError DoOperationDisconnect(Socket socket, SafeSocketHandle handle, CancellationToken _ /*cancellationToken*/)
        {
            SocketError socketError = SocketPal.Disconnect(socket, handle, _disconnectReuseSocket);
            FinishOperationSync(socketError, 0, SocketFlags.None);
            return socketError;
        }

        private Action<int, byte[]?, int, SocketFlags, SocketError> TransferCompletionCallback =>
            _transferCompletionCallback ??= TransferCompletionCallbackCore;

        private void TransferCompletionCallbackCore(int bytesTransferred, byte[]? socketAddress, int socketAddressSize, SocketFlags receivedFlags, SocketError socketError)
        {
            CompleteTransferOperation(socketAddress, socketAddressSize, receivedFlags);

            CompletionCallback(bytesTransferred, receivedFlags, socketError);
        }

        private void CompleteTransferOperation(byte[]? socketAddress, int socketAddressSize, SocketFlags receivedFlags)
        {
            Debug.Assert(socketAddress == null || socketAddress == _socketAddress!.Buffer, $"Unexpected socketAddress: {socketAddress}");
            _socketAddressSize = socketAddressSize;
            _receivedFlags = receivedFlags;
        }

        internal unsafe SocketError DoOperationReceive(SafeSocketHandle handle, CancellationToken cancellationToken)
        {
            _receivedFlags = System.Net.Sockets.SocketFlags.None;
            _socketAddressSize = 0;

            SocketFlags flags;
            int bytesReceived;
            SocketError errorCode;
            if (_bufferList == null)
            {
                // TCP has no out-going receive flags. We can use different syscalls which give better performance.
                bool noReceivedFlags = _currentSocket!.ProtocolType == ProtocolType.Tcp;
                if (noReceivedFlags)
                {
                    errorCode = handle.AsyncContext.ReceiveAsync(_buffer.Slice(_offset, _count), _socketFlags, out bytesReceived, TransferCompletionCallback, cancellationToken);
                    flags = SocketFlags.None;
                }
                else
                {
                    errorCode = handle.AsyncContext.ReceiveAsync(_buffer.Slice(_offset, _count), _socketFlags, out bytesReceived, out flags, TransferCompletionCallback, cancellationToken);
                }
            }
            else
            {
                errorCode = handle.AsyncContext.ReceiveAsync(_bufferListInternal!, _socketFlags, out bytesReceived, out flags, TransferCompletionCallback);
            }

            if (errorCode != SocketError.IOPending)
            {
                CompleteTransferOperation(null, 0, flags);
                FinishOperationSync(errorCode, bytesReceived, flags);
            }

            return errorCode;
        }

        internal unsafe SocketError DoOperationReceiveFrom(SafeSocketHandle handle, CancellationToken cancellationToken)
        {
            _receivedFlags = System.Net.Sockets.SocketFlags.None;
            _socketAddressSize = 0;

            SocketFlags flags;
            SocketError errorCode;
            int bytesReceived;
            int socketAddressLen = _socketAddress!.Size;
            if (_bufferList == null)
            {
                errorCode = handle.AsyncContext.ReceiveFromAsync(_buffer.Slice(_offset, _count), _socketFlags, _socketAddress.Buffer, ref socketAddressLen, out bytesReceived, out flags, TransferCompletionCallback, cancellationToken);
            }
            else
            {
                errorCode = handle.AsyncContext.ReceiveFromAsync(_bufferListInternal!, _socketFlags, _socketAddress.Buffer, ref socketAddressLen, out bytesReceived, out flags, TransferCompletionCallback);
            }

            if (errorCode != SocketError.IOPending)
            {
                CompleteTransferOperation(_socketAddress.Buffer, socketAddressLen, flags);
                FinishOperationSync(errorCode, bytesReceived, flags);
            }

            return errorCode;
        }

        private void ReceiveMessageFromCompletionCallback(int bytesTransferred, byte[] socketAddress, int socketAddressSize, SocketFlags receivedFlags, IPPacketInformation ipPacketInformation, SocketError errorCode)
        {
            CompleteReceiveMessageFromOperation(socketAddress, socketAddressSize, receivedFlags, ipPacketInformation);

            CompletionCallback(bytesTransferred, receivedFlags, errorCode);
        }

        private void CompleteReceiveMessageFromOperation(byte[] socketAddress, int socketAddressSize, SocketFlags receivedFlags, IPPacketInformation ipPacketInformation)
        {
            Debug.Assert(_socketAddress != null, "Expected non-null _socketAddress");
            Debug.Assert(socketAddress == null || _socketAddress.Buffer == socketAddress, $"Unexpected socketAddress: {socketAddress}");

            _socketAddressSize = socketAddressSize;
            _receivedFlags = receivedFlags;
            _receiveMessageFromPacketInfo = ipPacketInformation;
        }

        internal unsafe SocketError DoOperationReceiveMessageFrom(Socket socket, SafeSocketHandle handle, CancellationToken cancellationToken)
        {
            _receiveMessageFromPacketInfo = default(IPPacketInformation);
            _receivedFlags = System.Net.Sockets.SocketFlags.None;
            _socketAddressSize = 0;

            bool isIPv4, isIPv6;
            Socket.GetIPProtocolInformation(socket.AddressFamily, _socketAddress!, out isIPv4, out isIPv6);

            int socketAddressSize = _socketAddress!.Size;
            int bytesReceived;
            SocketFlags receivedFlags;
            IPPacketInformation ipPacketInformation;
            SocketError socketError = handle.AsyncContext.ReceiveMessageFromAsync(_buffer.Slice(_offset, _count), _bufferListInternal, _socketFlags, _socketAddress.Buffer, ref socketAddressSize, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, ReceiveMessageFromCompletionCallback, cancellationToken);
            if (socketError != SocketError.IOPending)
            {
                CompleteReceiveMessageFromOperation(_socketAddress.Buffer, socketAddressSize, receivedFlags, ipPacketInformation);
                FinishOperationSync(socketError, bytesReceived, receivedFlags);
            }
            return socketError;
        }

        internal unsafe SocketError DoOperationSend(SafeSocketHandle handle, CancellationToken cancellationToken)
        {
            _receivedFlags = System.Net.Sockets.SocketFlags.None;
            _socketAddressSize = 0;

            int bytesSent;
            SocketError errorCode;
            if (_bufferList == null)
            {
                errorCode = handle.AsyncContext.SendAsync(_buffer, _offset, _count, _socketFlags, out bytesSent, TransferCompletionCallback, cancellationToken);
            }
            else
            {
                errorCode = handle.AsyncContext.SendAsync(_bufferListInternal!, _socketFlags, out bytesSent, TransferCompletionCallback);
            }

            if (errorCode != SocketError.IOPending)
            {
                CompleteTransferOperation(null, 0, SocketFlags.None);
                FinishOperationSync(errorCode, bytesSent, SocketFlags.None);
            }

            return errorCode;
        }

        internal SocketError DoOperationSendPackets(Socket socket, SafeSocketHandle _1 /*handle*/, CancellationToken cancellationToken)
        {
            Debug.Assert(_sendPacketsElements != null);
            SendPacketsElement[] elements = (SendPacketsElement[])_sendPacketsElements.Clone();
            SafeFileHandle[] fileHandles = new SafeFileHandle[elements.Length];

            // Open all files synchronously ahead of time so that any exceptions are propagated
            // to the caller, to match Windows behavior.
            try
            {
                for (int i = 0; i < elements.Length; i++)
                {
                    string? path = elements[i]?.FilePath;
                    if (path != null)
                    {
                        fileHandles[i] = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
                    }
                }
            }
            catch (Exception exc)
            {
                // Clean up any files that were already opened.
                foreach (SafeFileHandle s in fileHandles)
                {
                    s?.Dispose();
                }

                // Windows differentiates the directory not being found from the file not being found.
                // Approximate this by checking to see if the directory exists; this is only best-effort,
                // as there are various things that could affect this, e.g. directory creation racing with
                // this check, but it's good enough for most situations.
                if (exc is FileNotFoundException fnfe)
                {
                    string? dirname = Path.GetDirectoryName(fnfe.FileName);
                    if (!string.IsNullOrEmpty(dirname) && !Directory.Exists(dirname))
                    {
                        throw new DirectoryNotFoundException(fnfe.Message);
                    }
                }

                // Otherwise propagate the original error.
                throw;
            }

            _ = SocketPal.SendPacketsAsync(socket, SendPacketsFlags, elements, fileHandles, cancellationToken, (bytesTransferred, error) =>
            {
                if (error == SocketError.Success)
                {
                    FinishOperationAsyncSuccess((int)bytesTransferred, SocketFlags.None);
                }
                else
                {
                    FinishOperationAsyncFailure(error, (int)bytesTransferred, SocketFlags.None);
                }
            });

            return SocketError.IOPending;
        }

        internal SocketError DoOperationSendTo(SafeSocketHandle handle, CancellationToken cancellationToken)
        {
            _receivedFlags = System.Net.Sockets.SocketFlags.None;
            _socketAddressSize = 0;

            int bytesSent;
            int socketAddressLen = _socketAddress!.Size;
            SocketError errorCode;
            if (_bufferList == null)
            {
                errorCode = handle.AsyncContext.SendToAsync(_buffer, _offset, _count, _socketFlags, _socketAddress.Buffer, ref socketAddressLen, out bytesSent, TransferCompletionCallback, cancellationToken);
            }
            else
            {
                errorCode = handle.AsyncContext.SendToAsync(_bufferListInternal!, _socketFlags, _socketAddress.Buffer, ref socketAddressLen, out bytesSent, TransferCompletionCallback);
            }

            if (errorCode != SocketError.IOPending)
            {
                CompleteTransferOperation(_socketAddress.Buffer, socketAddressLen, SocketFlags.None);
                FinishOperationSync(errorCode, bytesSent, SocketFlags.None);
            }

            return errorCode;
        }

        internal void LogBuffer(int size)
        {
            // This should only be called if tracing is enabled. However, there is the potential for a race
            // condition where tracing is disabled between a calling check and here, in which case the assert
            // may fire erroneously.
            Debug.Assert(NetEventSource.Log.IsEnabled());

            if (_bufferList == null)
            {
                NetEventSource.DumpBuffer(this, _buffer, _offset, size);
            }
            else if (_acceptBuffer != null)
            {
                NetEventSource.DumpBuffer(this, _acceptBuffer, 0, size);
            }
        }

        private SocketError FinishOperationAccept(Internals.SocketAddress remoteSocketAddress)
        {
            System.Buffer.BlockCopy(_acceptBuffer!, 0, remoteSocketAddress.Buffer, 0, _acceptAddressBufferCount);
            Socket acceptedSocket = _currentSocket!.CreateAcceptSocket(
                SocketPal.CreateSocket(_acceptedFileDescriptor),
                _currentSocket._rightEndPoint!.Create(remoteSocketAddress));
            if (_acceptSocket is null)
            {
                // Store the accepted socket
                _acceptSocket = acceptedSocket;
            }
            else
            {
                // Copy state from the accepted socket into the caller-supplied socket and then dispose of the original.
                _acceptSocket.DisposeHandle();
                _acceptSocket.CopyStateFromSource(acceptedSocket);
                acceptedSocket.ClearHandle();
                acceptedSocket.Dispose();
            }
            return SocketError.Success;
        }

        private static SocketError FinishOperationConnect()
        {
            // No-op for *nix.
            return SocketError.Success;
        }

        private unsafe int GetSocketAddressSize()
        {
            return _socketAddressSize;
        }

        partial void FinishOperationReceiveMessageFrom();

        partial void FinishOperationSendPackets();

        private void CompletionCallback(int bytesTransferred, SocketFlags flags, SocketError socketError)
        {
            if (socketError == SocketError.Success)
            {
                FinishOperationAsyncSuccess(bytesTransferred, flags);
            }
            else
            {
                if (_currentSocket!.Disposed)
                {
                    socketError = SocketError.OperationAborted;
                }

                FinishOperationAsyncFailure(socketError, bytesTransferred, flags);
            }
        }
    }
}
