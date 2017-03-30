using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Transport;
using Tmds.Posix;

namespace Tmds.Kestrel.Linux
{
    sealed partial class TransportThread
    {
        private const int MaxPooledBlockLength = MemoryPool.MaxPooledBlockLength;
        // 32 IOVectors, take up 512B of stack, can send up to 128KB
        private const int MaxIOVectorSendLength = 32;
        // 32 IOVectors, take up 512B of stack, can receive up to 128KB
        private const int MaxIOVectorReceiveLength = 32;
        internal const int MaxSendLength = MaxIOVectorSendLength * MaxPooledBlockLength;
        private const int ListenBacklog     = 128;
        private const int EventBufferLength = 512;
        // Highest bit set in EPollData for writable poll
        // the remaining bits of the EPollData are the key
        // of the _sockets dictionary.
        private const int DupKeyMask        = 1 << 31;
        private const byte PipeStateChange  = 0;
        private const byte PipeCoalesce     = 1;
        unsafe struct ReceiveBuffer
        {
            public fixed long IOVectors[2 * MaxIOVectorReceiveLength];
        }

        enum State
        {
            Initial,
            Starting,
            Started,
            ClosingAccept,
            AcceptClosed,
            Stopping,
            Stopped
        }

        private readonly IConnectionHandler _connectionHandler;

        private State _state;
        private readonly object _gate = new object();
        // key is the file descriptor
        private ConcurrentDictionary<int, TSocket> _sockets;
        private ConcurrentQueue<TSocket> _coalescingWrites;
        private int _coalesceWritesOnNextPoll;
        private List<TSocket> _acceptSockets;
        private EPoll _epoll;
        private PipeEndPair _pipeEnds;
        private Thread _thread;
        private TaskCompletionSource<object> _stateChangeCompletion;
        private bool _deferAccept;
        private bool _coalesceWrites;
        private int _cpuId;
        private bool _receiveOnIncomingCpu;
        private unsafe IOVector* _receiveIoVectors;
        private OwnedBuffer<byte>[] _receivePool;
        private PipeFactory _pipeFactory;
        private MemoryPool _bufferPool;
        private ListenOptions _listenOptions;

        public TransportThread(IConnectionHandler connectionHandler, TransportOptions options, int cpuId, ListenOptions listenOptions)
        {
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            _connectionHandler = connectionHandler;
            _deferAccept = options.DeferAccept;
            _coalesceWrites = options.CoalesceWrites;
            _cpuId = cpuId;
            _receiveOnIncomingCpu = options.ReceiveOnIncomingCpu;
            _listenOptions = listenOptions;
        }

        public Task StartAsync()
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                if (_state == State.Started)
                {
                    return Task.CompletedTask;
                }
                else if (_state == State.Starting)
                {
                    return _stateChangeCompletion.Task;
                }
                else if (_state != State.Initial)
                {
                    ThrowInvalidState();
                }
                try
                {
                    _sockets = new ConcurrentDictionary<int, TSocket>();
                    _acceptSockets = new List<TSocket>();
                    if (_coalesceWrites)
                    {
                        _coalescingWrites = new ConcurrentQueue<TSocket>();
                    }

                    _epoll = EPoll.Create();

                    _pipeEnds = PipeEnd.CreatePair(blocking: false);
                    var tsocket = new TSocket(this)
                    {
                        Flags = SocketFlags.TypePipe,
                        Key = _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32()
                    };
                    _sockets.TryAdd(tsocket.Key, tsocket);
                    _epoll.Control(EPollOperation.Add, _pipeEnds.ReadEnd, EPollEvents.Readable, new EPollData { Int1 = tsocket.Key, Int2 = tsocket.Key });

                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Starting;

                    _thread = new Thread(PollThread);
                    _thread.Start();
                }
                catch
                {
                    _state = State.Stopped;
                    _epoll?.Dispose();
                    _pipeEnds.Dispose();
                    throw;
                }
            }
            return tcs.Task;
        }

        public void AcceptOn(System.Net.IPEndPoint endPoint)
        {
            lock (_gate)
            {
                if (_state != State.Started)
                {
                    ThrowInvalidState();
                }

                Socket acceptSocket = null;
                int key = 0;
                int port = endPoint.Port;
                SocketFlags flags = SocketFlags.TypeAccept;
                try
                {
                    bool ipv4 = endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
                    acceptSocket = Socket.Create(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, blocking: false);
                    key = acceptSocket.DangerousGetHandle().ToInt32();
                    if (!ipv4)
                    {
                        // Don't do mapped ipv4
                        acceptSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IPv6Only, 1);
                    }
                    if (_receiveOnIncomingCpu)
                    {
                        if (_cpuId != -1)
                        {
                            if (!acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.IncomingCpu, _cpuId))
                            {
                                // TODO: log
                            }
                        }
                        else
                        {
                            // TODO: log
                        }
                    }
                    // Linux: allow bind during linger time
                    acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                    // Linux: allow concurrent binds and let the kernel do load-balancing
                    acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReusePort, 1);
                    if (_deferAccept)
                    {
                        // Linux: wait up to 1 sec for data to arrive before accepting socket
                        acceptSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.DeferAccept, 1);
                        flags |= SocketFlags.DeferAccept;
                    }

                    acceptSocket.Bind(endPoint);
                    if (port == 0)
                    {
                        // When testing we want the OS to select a free port
                        port = acceptSocket.GetLocalIPAddress().Port;
                    }

                    acceptSocket.Listen(ListenBacklog);
                }
                catch
                {
                    acceptSocket?.Dispose();
                    throw;
                }

                TSocket tsocket = null;
                try
                {
                    tsocket = new TSocket(this)
                    {
                        Flags = flags,
                        Key = key,
                        Socket = acceptSocket
                    };
                    _acceptSockets.Add(tsocket);
                    _sockets.TryAdd(tsocket.Key, tsocket);

                    _epoll.Control(EPollOperation.Add, acceptSocket, EPollEvents.Readable, new EPollData { Int1 = tsocket.Key, Int2 = tsocket.Key });
                }
                catch
                {
                    acceptSocket.Dispose();
                    _acceptSockets.Remove(tsocket);
                    _sockets.TryRemove(key, out tsocket);
                    throw;
                }
                endPoint.Port = port;
            }
        }

        public async Task CloseAcceptAsync()
        {
            TaskCompletionSource<object> tcs = null;
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return;
                }
                else if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
            }
            if (tcs != null)
            {
                await tcs.Task;
                return;
            }
            try
            {
                await StartAsync();
            }
            catch
            {}
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == State.Started)
                {
                    triggerStateChange = true;
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.ClosingAccept;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _pipeEnds.WriteEnd.WriteByte(PipeStateChange);
            }
            await tcs.Task;
        }

        public async Task StopAsync()
        {
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return;
                }
                else if (_state == State.Stopped)
                {
                    return;
                }
            }

            await CloseAcceptAsync();

            TaskCompletionSource<object> tcs = null;
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.Stopping)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == State.AcceptClosed)
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Stopping;
                    triggerStateChange = true;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _pipeEnds.WriteEnd.WriteByte(PipeStateChange);
            }
            await tcs.Task;
        }

        private unsafe void PollThread(object obj)
        {
            if (_cpuId != -1)
            {
                if (!Scheduler.TrySetCurrentThreadAffinity(_cpuId))
                {
                    // TODO: log
                    _cpuId = -1;
                }
            }

            CompleteStateChange(State.Started);

            _bufferPool = new MemoryPool();
            _pipeFactory = new PipeFactory(_bufferPool);

            ReceiveBuffer receiveBuffer = default(ReceiveBuffer);
            _receiveIoVectors = (IOVector*)receiveBuffer.IOVectors;
            _receivePool = new OwnedBuffer<byte>[MaxIOVectorSendLength];

            bool notPacked = !EPoll.PackedEvents;
            var buffer = stackalloc int[EventBufferLength * (notPacked ? 4 : 3)];
            bool running = true;
            while (running)
            {
                bool doCloseAccept = false;
                int numEvents = _epoll.Wait(buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite);
                if (_coalesceWrites)
                {
                    DoCoalescedWrites();
                }
                int* ptr = buffer;
                for (int i = 0; i < numEvents; i++)
                {
                    //   Packed             Non-Packed
                    //   ------             ------
                    // 0:Events       ==    Events
                    // 1:Int1 = Key         [Padding]
                    // 2:Int2 = Key   ==    Int1 = Key
                    // 3:~~~~~~~~~~         Int2 = Key
                    //                      ~~~~~~~~~~
                    ptr += 2;            // 0 & 1
                    int key = *ptr++;    // 2
                    if (notPacked)
                        ptr++;           // 3
                    TSocket tsocket;
                    if (_sockets.TryGetValue(key & ~DupKeyMask, out tsocket))
                    {
                        var type = tsocket.Flags & SocketFlags.TypeMask;
                        if (type == SocketFlags.TypeClient)
                        {
                            bool read = (key & DupKeyMask) == 0;
                            if (read)
                            {
                                tsocket.CompleteReadable();
                            }
                            else
                            {
                                tsocket.CompleteWritable();
                            }
                        }
                        else if (type == SocketFlags.TypeAccept && !doCloseAccept)
                        {
                            HandleAccept(tsocket);
                        }
                        else // TypePipe
                        {
                            var action = _pipeEnds.ReadEnd.TryReadByte();
                            if (action.Value == PipeStateChange)
                            {
                                HandleState(ref running, ref doCloseAccept);
                            }
                        }
                    }
                }
                if (doCloseAccept)
                {
                    CloseAccept();
                }
            }
            Stop();
        }

        private void DoCoalescedWrites()
        {
            Volatile.Write(ref _coalesceWritesOnNextPoll, 0);
            int count = _coalescingWrites.Count;
            for (int i = 0; i < count; i++)
            {
                TSocket tsocket;
                _coalescingWrites.TryDequeue(out tsocket);
                tsocket.CompleteWritable();
            }
        }

        private void HandleAccept(TSocket tacceptSocket)
        {
            // TODO: should we handle more than 1 accept? If we do, we shouldn't be to eager
            //       as that might give the kernel the impression we have nothing to do
            //       which could interfere with the SO_REUSEPORT load-balancing.
            Socket clientSocket;
            var result = tacceptSocket.Socket.TryAccept(out clientSocket, blocking: false);
            if (result.IsSuccess)
            {
                int key;
                TSocket tsocket;
                try
                {
                    key = clientSocket.DangerousGetHandle().ToInt32();

                    tsocket = new TSocket(this)
                    {
                        Flags = SocketFlags.TypeClient,
                        Key = key,
                        Socket = clientSocket,
                        PeerAddress = clientSocket.GetPeerIPAddress(),
                        LocalAddress = clientSocket.GetLocalIPAddress()
                    };

                    clientSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
                }
                catch
                {
                    clientSocket.Dispose();
                    return;
                }

                var connectionContext = _connectionHandler.OnConnection(tsocket);
                tsocket.PipeReader = connectionContext.Output;
                tsocket.PipeWriter = connectionContext.Input;

                _sockets.TryAdd(key, tsocket);

                WriteToSocket(tsocket, connectionContext.Output);
                bool dataMayBeAvailable = (tacceptSocket.Flags & SocketFlags.DeferAccept) != 0;
                ReadFromSocket(tsocket, connectionContext.Input, dataMayBeAvailable);
            }
        }

        private async void WriteToSocket(TSocket tsocket, IPipeReader reader)
        {
            try
            {
                while (true)
                {
                    var readResult = await reader.ReadAsync();
                    ReadableBuffer buffer = readResult.Buffer;
                    if (_coalesceWrites)
                    {
                        reader.Advance(default(ReadCursor));
                        if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCancelled)
                        {
                            // EOF or TransportThread stopped
                            break;
                        }
                        if (!await CoalescingWrites(tsocket))
                        {
                            // TransportThread stopped
                            break;
                        }
                        readResult = await reader.ReadAsync();
                        buffer = readResult.Buffer;
                    }
                    ReadCursor end = buffer.Start;
                    try
                    {
                        if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCancelled)
                        {
                            // EOF or TransportThread stopped
                            break;
                        }
                        if (!buffer.IsEmpty)
                        {
                            var result = TrySend(tsocket.Socket, ref buffer);
                            if (result.IsSuccess && result.Value != 0)
                            {
                                end = result.Value == buffer.Length ? buffer.End : buffer.Move(buffer.Start, result.Value);
                            }
                            else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                            {
                                if (!await Writable(tsocket))
                                {
                                    // TransportThread stopped
                                    break;
                                }
                            }
                            else
                            {
                                result.ThrowOnError();
                            }
                        }
                    }
                    finally
                    {
                        // We need to call Advance to end the read
                        reader.Advance(end);
                    }
                }
                reader.Complete();
            }
            catch (Exception ex)
            {
                reader.Complete(ex);
            }
            finally
            {
                CleanupSocket(tsocket, SocketShutdown.Send);
            }
        }

        private static unsafe PosixResult TrySend(Socket socket, ref ReadableBuffer buffer)
        {
            int ioVectorLength = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0)
                {
                    continue;
                }
                ioVectorLength++;
                if (ioVectorLength == MaxIOVectorSendLength)
                {
                    // No more room in the IOVector
                    break;
                }
            }
            if (ioVectorLength == 0)
            {
                return new PosixResult(0);
            }

            var ioVectors = stackalloc IOVector[ioVectorLength];
            int i = 0;
            foreach (var memory in buffer)
            {
                if (memory.Length == 0)
                {
                    continue;
                }
                void* pointer;
                memory.TryGetPointer(out pointer);
                ioVectors[i].Base = pointer;
                ioVectors[i].Count = (void*)memory.Length;
                i++;
                if (i == ioVectorLength)
                {
                    // No more room in the IOVector
                    break;
                }
            }
            return socket.TrySend(ioVectors, ioVectorLength);
        }

        private WritableAwaitable CoalescingWrites(TSocket tsocket)
        {
            tsocket.ResetWritableAwaitable();
            _coalescingWrites.Enqueue(tsocket);
            var pending = Interlocked.CompareExchange(ref _coalesceWritesOnNextPoll, 1, 0);
            if (pending == 0)
            {
                _pipeEnds.WriteEnd.WriteByte(PipeCoalesce);
            }
            return tsocket.WritableAwaitable;
        }

        private WritableAwaitable Writable(TSocket tsocket)
        {
            tsocket.ResetWritableAwaitable();
            bool registered = tsocket.DupSocket != null;
            // To avoid having to synchronize the event mask with the Readable
            // we dup the socket.
            // In the EPollData we set the highest bit to indicate this is the
            // poll for writable.
            if (!registered)
            {
                tsocket.DupSocket = tsocket.Socket.Duplicate();
            }
            _epoll.Control(registered ? EPollOperation.Modify : EPollOperation.Add,
                            tsocket.DupSocket,
                            EPollEvents.Writable | EPollEvents.OneShot,
                            new EPollData{ Int1 = tsocket.Key | DupKeyMask, Int2 = tsocket.Key | DupKeyMask } );
            return tsocket.WritableAwaitable;
        }

        private static void RegisterForReadable(TSocket tsocket, EPoll epoll)
        {
            try
            {
                bool registered = (tsocket.Flags & SocketFlags.EPollRegistered) != 0;
                if (!registered)
                {
                    tsocket.AddFlags(SocketFlags.EPollRegistered);
                }
                epoll.Control(registered ? EPollOperation.Modify : EPollOperation.Add,
                    tsocket.Socket,
                    EPollEvents.Readable | EPollEvents.OneShot,
                    new EPollData{ Int1 = tsocket.Key, Int2 = tsocket.Key });
            }
            catch (System.Exception)
            {
                tsocket.CompleteReadable(stopping: true);
            }
        }

        private async void ReadFromSocket(TSocket tsocket, IPipeWriter writer, bool dataMayBeAvailable)
        {
            // Start on PollThread
            try
            {
                bool eof = false;
                if (!dataMayBeAvailable)
                {
                    eof = !await Readable(tsocket);
                }
                while (!eof)
                {
                    var buffer = writer.Alloc();
                    try
                    {
                        var result = TryReceive(tsocket.Socket, ref buffer);
                        if (result.IsSuccess)
                        {
                            if (result.Value != 0)
                            {
                                var flushResult = await buffer.FlushAsync();
                                eof = flushResult.IsCompleted || flushResult.IsCancelled;
                            }
                            else
                            {
                                buffer.Commit();
                                eof = true;
                            }
                        }
                        else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                        {
                            buffer.Commit();
                        }
                        else
                        {
                            result.ThrowOnError();
                        }
                    }
                    catch
                    {
                        buffer.Commit();
                        throw;
                    }
                    if (!eof)
                    {
                        // FlushAsync may put us on a differen thread,
                        // so we always call await Readable to ensure we TryReceive on the PollThread.
                        eof = !await Readable(tsocket);
                    }
                }
                writer.Complete();
            }
            catch (Exception ex)
            {
                writer.Complete(ex);
            }
            finally
            {
                CleanupSocket(tsocket, SocketShutdown.Receive);
            }
        }

        private unsafe PosixResult TryReceive(Socket socket, ref WritableBuffer wb)
        {
            // All receives execute on PollThread, so it's safe to share the receive pool.

            // Refill pool
            for (int i = 0; (i < MaxIOVectorReceiveLength && _receivePool[i] == null); i++)
            {
                OwnedBuffer<byte> buffer = _bufferPool.Rent(1);
                _receivePool[i] = buffer;
                void* pointer;
                buffer.Buffer.TryGetPointer(out pointer); // this always returns true (MemoryPool is pinned)
                _receiveIoVectors[i].Base = pointer;
                _receiveIoVectors[i].Count = (void*)buffer.Length;
            }

            // Receive
            var result = socket.TryReceive(_receiveIoVectors, MaxIOVectorReceiveLength);

            // Add filled buffers to pipe
            int length = result.Value;
            int usedIdx = 0;
            while (length > 0)
            {
                int bufferLength = (int)_receiveIoVectors[usedIdx].Count;
                var buffer = _receivePool[usedIdx];
                bufferLength = Math.Min(bufferLength, length);
                wb.Append(ReadableBuffer.Create(buffer, 0, bufferLength));
                _receivePool[usedIdx] = null;
                length -= bufferLength;
                usedIdx++;
            }
            return result;
        }

        // Readable is 'rigged' to always return asynchronous
        private ReadableAwaitable Readable(TSocket tsocket) => new ReadableAwaitable(tsocket, _epoll);

        private void CleanupSocket(TSocket tsocket, SocketShutdown shutdown)
        {
            // One caller will end up calling Shutdown, the other will call Dispose.
            // To ensure the Shutdown is executed against an open file descriptor
            // we manually increment/decrement the refcount on the safehandle.
            // We need to use a CER (Constrainted Execution Region) to ensure
            // the refcount is decremented.

            // This isn't available in .NET Core 1.x
            // RuntimeHelpers.PrepareConstrainedRegions();
            try
            { }
            finally
            {
                bool releaseRef = false;
                tsocket.Socket.DangerousAddRef(ref releaseRef);
                var previous = tsocket.AddFlags(shutdown == SocketShutdown.Send ? SocketFlags.ShutdownSend : SocketFlags.ShutdownReceive);
                var other = shutdown == SocketShutdown.Send ? SocketFlags.ShutdownReceive : SocketFlags.ShutdownSend;
                var close = (previous & other) != 0;
                if (close)
                {
                    TSocket removedSocket;
                    _sockets.TryRemove(tsocket.Key, out removedSocket);
                    tsocket.Socket.Dispose();
                    tsocket.DupSocket?.Dispose();
                }
                else
                {
                    tsocket.Socket.TryShutdown(shutdown);
                }
                // when CleanupSocket finished for both ends
                // the close will be invoked by the next statement
                // causing removal from the epoll
                if (releaseRef)
                {
                    tsocket.Socket.DangerousRelease();
                }
            }
        }

        private void Stop()
        {
            _epoll.BlockingDispose();

            var pipeReadKey = _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
            TSocket pipeReadSocket;
            _sockets.TryRemove(pipeReadKey, out pipeReadSocket);

            foreach (var kv in _sockets)
            {
                var tsocket = kv.Value;
                tsocket.PipeReader.CancelPendingRead();
                tsocket.PipeWriter.CancelPendingFlush();
                tsocket.CompleteReadable(stopping: true);
                tsocket.CompleteWritable(stopping: true);
            }

            SpinWait sw = new SpinWait();
            while (!_sockets.IsEmpty)
            {
                sw.SpinOnce();
            }

            _pipeEnds.Dispose();

            foreach (var buffer in _receivePool)
            {
                buffer?.Dispose();
            }
            _pipeFactory.Dispose(); // also disposes _bufferPool

            CompleteStateChange(State.Stopped);
        }

        private void CloseAccept()
        {
            foreach (var acceptSocket in _acceptSockets)
            {
                TSocket removedSocket;
                _sockets.TryRemove(acceptSocket.Key, out removedSocket);
                // close causes remove from epoll (CLOEXEC)
                acceptSocket.Socket.Dispose(); // will close (no concurrent users)
            }
            _acceptSockets.Clear();
            CompleteStateChange(State.AcceptClosed);
        }

        private unsafe void HandleState(ref bool running, ref bool doCloseAccept)
        {
            lock (_gate)
            {
                if (_state == State.ClosingAccept)
                {
                    doCloseAccept = true;
                }
                else if (_state == State.Stopping)
                {
                    running = false;
                }
            }
        }

        private void ThrowInvalidState()
        {
            throw new InvalidOperationException($"nameof(TransportThread) is {_state}");
        }

        private void CompleteStateChange(State state)
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                tcs = _stateChangeCompletion;
                _stateChangeCompletion = null;
                _state = state;
            }
            tcs.SetResult(null);
        }
    }
}