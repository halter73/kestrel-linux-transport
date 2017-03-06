using System;
using System.Net;
using System.Runtime.InteropServices;
using Tmds.Posix;

namespace Tmds.Kestrel.Linux
{
    static class SocketInterop
    {
        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Socket")]
        public static extern unsafe PosixResult Socket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, bool blocking, out Socket socket);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_GetAvailableBytes")]
        public static extern PosixResult GetAvailableBytes(Socket socket);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Bind")]
        public static extern unsafe PosixResult Bind(Socket socket, byte* addr, int addrlen);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Connect")]
        public static extern unsafe PosixResult Connect(Socket socket, byte* addr, int addrlen);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Listen")]
        public static extern PosixResult Listen(Socket socket, int backlog);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Accept")]
        public static unsafe extern PosixResult Accept(Socket socket, byte* socketAddress, int socketAddressLen, bool blocking, out Socket clientSocket);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Shutdown")]
        public static extern PosixResult Shutdown(Socket socket, SocketShutdown shutdown);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Send")]
        public static unsafe extern PosixResult Send(SafeHandle handle, byte* buf, int count);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_Receive")]
        public static unsafe extern PosixResult Receive(SafeHandle handle, byte* buf, int count);
        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_SetSockOpt")]
        public static extern unsafe PosixResult SetSockOpt(SafeHandle socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte* optionValue, int optionLen);

        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_GetSockOpt")]
        public static extern unsafe PosixResult GetSockOpt(SafeHandle socket, SocketOptionLevel optionLevel, SocketOptionName optionName, byte* optionValue, int* optionLen);
        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_GetPeerName")]
        public static extern unsafe PosixResult GetPeerName(Socket socket, byte* addr, int addrlen);
        [DllImportAttribute(Interop.Library, EntryPoint = "TmdsKL_GetSockName")]
        public static extern unsafe PosixResult GetSockName(Socket socket, byte* addr, int addrlen);
    }

    class Socket : CloseSafeHandle
    {
        private Socket()
        {}

        public static Socket Create(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, bool blocking)
        {
            Socket socket;
            var result = SocketInterop.Socket(addressFamily, socketType, protocolType, blocking, out socket);
            result.ThrowOnError();
            return socket;
        }

        public int GetAvailableBytes()
        {
            var result = TryGetAvailableBytes();
            result.ThrowOnError();
            return result.Value;
        }

        public PosixResult TryGetAvailableBytes()
        {
            return SocketInterop.GetAvailableBytes(this);
        }

        public void Bind(IPEndPoint endpoint)
        {
            TryBind(endpoint)
                .ThrowOnError();
        }

        public unsafe PosixResult TryBind(IPEndPoint endpoint)
        {
            IPSocketAddress socketAddress = new IPSocketAddress(endpoint);
            return SocketInterop.Bind(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
        }

        public void Connect(IPEndPoint endpoint)
        {
            TryConnect(endpoint)
                .ThrowOnError();
        }

        public unsafe PosixResult TryConnect(IPEndPoint endpoint)
        {
            IPSocketAddress socketAddress = new IPSocketAddress(endpoint);
            return SocketInterop.Connect(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
        }

        public void Listen(int backlog)
        {
            TryListen(backlog)
                .ThrowOnError();
        }

        public PosixResult TryListen(int backlog)
        {
            return SocketInterop.Listen(this, backlog);
        }

        public unsafe Socket Accept(bool blocking)
        {
            Socket clientSocket;
            var result = TryAccept(out clientSocket, blocking);
            result.ThrowOnError();
            return clientSocket;
        }

        public unsafe PosixResult TryAccept(out Socket clientSocket, bool blocking)
        {
            return SocketInterop.Accept(this, null, 0, blocking, out clientSocket);
        }

        public int Receive(ArraySegment<byte> buffer)
        {
            var result = TryReceive(buffer);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TryReceive(ArraySegment<byte> buffer)
        {
            fixed (byte* buf = buffer.Array)
            {
                return SocketInterop.Receive(this, buf + buffer.Offset, buffer.Count);
            }
        }

        public void Shutdown(SocketShutdown shutdown)
        {
            TryShutdown(shutdown)
                .ThrowOnError();
        }

        public PosixResult TryShutdown(SocketShutdown shutdown)
        {
            return SocketInterop.Shutdown(this, shutdown);
        }

        public int Send(ArraySegment<byte> buffer)
        {
            var result = TrySend(buffer);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TrySend(ArraySegment<byte> buffer)
        {
            fixed (byte* buf = buffer.Array)
            {
                return SocketInterop.Send(this, buf + buffer.Offset, buffer.Count);
            }
        }

        public void SetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int value)
        {
            TrySetSocketOption(optionLevel, optionName, value)
                .ThrowOnError();
        }

        public unsafe PosixResult TrySetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, int value)
        {
            return SocketInterop.SetSockOpt(this, optionLevel, optionName, (byte*)&value, 4);
        }

        public int GetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName)
        {
            int value = 0;
            var result = TryGetSocketOption(optionLevel, optionName, ref value);
            result.ThrowOnError();
            return value;
        }

        public unsafe PosixResult TryGetSocketOption(SocketOptionLevel optionLevel, SocketOptionName optionName, ref int value)
        {
            int v = 0;
            int length = 4;
            var rv = SocketInterop.GetSockOpt(this, optionLevel, optionName, (byte*)&v, &length);
            if (rv.IsSuccess)
            {
                value = v;
            }
            return rv;
        }

        public IPEndPoint GetLocalIPAddress()
        {
            IPEndPoint ep;
            TryGetLocalIPAddress(out ep)
                .ThrowOnError();
            return ep;
        }

        public unsafe PosixResult TryGetLocalIPAddress(out IPEndPoint ep)
        {
            IPSocketAddress socketAddress;
            var rv = SocketInterop.GetSockName(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
            if (rv.IsSuccess)
            {
                ep = socketAddress.ToIPEndPoint();
            }
            else
            {
                ep = null;
            }
            return rv;
        }

        public IPEndPoint GetPeerIPAddress()
        {
            IPEndPoint ep;
            TryGetPeerIPAddress(out ep)
                .ThrowOnError();
            return ep;
        }

        public unsafe PosixResult TryGetPeerIPAddress(out IPEndPoint ep)
        {
            IPSocketAddress socketAddress;
            var rv = SocketInterop.GetPeerName(this, (byte*)&socketAddress, sizeof(IPSocketAddress));
            if (rv.IsSuccess)
            {
                ep = socketAddress.ToIPEndPoint();
            }
            else
            {
                ep = null;
            }
            return rv;
        }
    }
}