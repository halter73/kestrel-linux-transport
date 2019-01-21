using System;
using System.Runtime.InteropServices;
using System.Threading;
using static Tmds.LibC.Definitions;
using Tmds.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    static class EPollInterop
    {
        public static PosixResult EPollCreate(out EPoll epoll)
        {
            epoll = new EPoll();

            int rv = epoll_create1(EPOLL_CLOEXEC);

            if (rv == -1)
            {
                epoll = null;
            }
            else
            {
                epoll.SetHandle(rv);
            }

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult EPollWait(int epoll, epoll_event* events, int maxEvents, int timeout)
        {
            int rv;
            do
            {
                rv = epoll_wait(epoll, events, maxEvents, timeout);
            } while (rv < 0 && errno == EINTR);

            return PosixResult.FromReturnValue(rv);
        }

        public static unsafe PosixResult EPollWait(EPoll epoll, epoll_event* events, int maxEvents, int timeout)
        => EPollWait(epoll.DangerousGetHandle().ToInt32(), events, maxEvents, timeout);

        public static unsafe PosixResult EPollControl(int epoll, int operation, int fd, int events, int data)
        {
            epoll_event ev = default(epoll_event);
            ev.events = events;
            ev.data.fd = data;

            int rv = epoll_ctl(epoll, operation, fd, &ev);

            return PosixResult.FromReturnValue(rv);
        }

        public static PosixResult EPollControl(EPoll epoll, int operation, SafeHandle fd, int events, int data)
        => EPollControl(epoll.DangerousGetHandle().ToInt32(), operation, fd.DangerousGetHandle().ToInt32(), events, data);
    }

    // Warning: Some operations use DangerousGetHandle for increased performance
    class EPoll : CloseSafeHandle
    {
        public const int TimeoutInfinite = -1;
        private bool _released = false;

        internal EPoll()
        {}

        public static EPoll Create()
        {
            EPoll epoll;
            var result = EPollInterop.EPollCreate(out epoll);
            result.ThrowOnError();
            return epoll;
        }

        public unsafe int Wait(void* events, int maxEvents, int timeout)
        {
            var result = TryWait(events, maxEvents, timeout);
            result.ThrowOnError();
            return result.Value;
        }

        public unsafe PosixResult TryWait(void* events, int maxEvents, int timeout)
        {
            return EPollInterop.EPollWait(this, (epoll_event*)events, maxEvents, timeout);
        }

        public void Control(int operation, SafeHandle fd, int events, int data)
        {
            TryControl(operation, fd, events, data)
                .ThrowOnError();
        }

        public PosixResult TryControl(int operation, SafeHandle fd, int events, int data)
        {
            return EPollInterop.EPollControl(this, operation, fd, events, data);
        }

        protected override bool ReleaseHandle()
        {
            _released = true;
            return base.ReleaseHandle();
        }

        // This method will only return when the EPoll has been closed.
        // Calls to Control will then throw ObjectDisposedException.
        public void BlockingDispose()
        {
            if (IsInvalid)
            {
                return;
            }

            Dispose();

            // block until the refcount drops to zero
            SpinWait sw = new SpinWait();
            while (!_released)
            {
                sw.SpinOnce();
            }
        }
    }
}