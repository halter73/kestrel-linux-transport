﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Buffers
{
    /// <summary>
    /// Used to allocate and distribute re-usable blocks of memory.
    /// </summary>
    internal sealed class SlabMemoryPool : MemoryPool<byte>
    {
        /// <summary>
        /// The size of a block. 4096 is chosen because most operating systems use 4k pages.
        /// </summary>
        private const int _blockSize = 4096;

        /// <summary>
        /// Allocating 32 contiguous blocks per slab makes the slab size 128k. This is larger than the 85k size which will place the memory
        /// in the large object heap. This means the GC will not try to relocate this array, so the fact it remains pinned does not negatively
        /// affect memory management's compactification.
        /// </summary>
        private const int _blockCount = 32;

        /// <summary>
        /// Max allocation block size for pooled blocks,
        /// larger values can be leased but they will be disposed after use rather than returned to the pool.
        /// </summary>
        public override int MaxBufferSize { get; } = _blockSize;

        /// <summary>
        /// The size of a block. 4096 is chosen because most operating systems use 4k pages.
        /// </summary>
        public static int BlockSize => _blockSize;

        /// <summary>
        /// 4096 * 32 gives you a slabLength of 128k contiguous bytes allocated per slab
        /// </summary>
        private static readonly int _slabLength = _blockSize * _blockCount;

        /// <summary>
        /// Thread-safe collection of blocks which are currently in the pool. A slab will pre-allocate all of the block tracking objects
        /// and add them to this collection. When memory is requested it is taken from here first, and when it is returned it is re-added.
        /// </summary>
        private readonly ConcurrentQueue<MemoryPoolBlock> _blocks = new ConcurrentQueue<MemoryPoolBlock>();

        /// <summary>
        /// Thread-safe collection of slabs which have been allocated by this pool. As long as a slab is in this collection and slab.IsActive,
        /// the blocks will be added to _blocks when returned.
        /// </summary>
        private readonly ConcurrentStack<MemoryPoolSlab> _slabs = new ConcurrentStack<MemoryPoolSlab>();

        /// <summary>
        /// This is part of implementing the IDisposable pattern.
        /// </summary>
        private bool _isDisposed; // To detect redundant calls

        private int _totalAllocatedBlocks;

        private readonly object _disposeSync = new object();

        /// <summary>
        /// This default value passed in to Rent to use the default value for the pool.
        /// </summary>
        private const int AnySize = -1;

        public override IMemoryOwner<byte> Rent(int size = AnySize)
        {
            if (size > _blockSize)
            {
                MemoryPoolThrowHelper.ThrowArgumentOutOfRangeException_BufferRequestTooLarge(_blockSize);
            }

            var block = Lease();
            return block;
        }

        /// <summary>
        /// Called to take a block from the pool.
        /// </summary>
        /// <returns>The block that is reserved for the called. It must be passed to Return when it is no longer being used.</returns>
        private MemoryPoolBlock Lease()
        {
            if (_isDisposed)
            {
                MemoryPoolThrowHelper.ThrowObjectDisposedException(MemoryPoolThrowHelper.ExceptionArgument.MemoryPool);
            }

            if (_blocks.TryDequeue(out MemoryPoolBlock block))
            {
                // block successfully taken from the stack - return it

                block.Lease();
                return block;
            }
            // no blocks available - grow the pool
            block = AllocateSlab();
            block.Lease();
            return block;
        }

        /// <summary>
        /// Internal method called when a block is requested and the pool is empty. It allocates one additional slab, creates all of the
        /// block tracking objects, and adds them all to the pool.
        /// </summary>
        private MemoryPoolBlock AllocateSlab()
        {
            var slab = MemoryPoolSlab.Create(_slabLength);
            _slabs.Push(slab);

            var basePtr = slab.NativePointer;
            // Page align the blocks
            var offset = (int)((((ulong)basePtr + (uint)_blockSize - 1) & ~((uint)_blockSize - 1)) - (ulong)basePtr);
            // Ensure page aligned
            Debug.Assert(((ulong)basePtr + (uint)offset) % _blockSize == 0);

            var blockCount = (_slabLength - offset) / _blockSize;
            Interlocked.Add(ref _totalAllocatedBlocks, blockCount);

            MemoryPoolBlock block = null;

            for (int i = 0; i < blockCount; i++)
            {
                block = new MemoryPoolBlock(this, slab, offset, _blockSize);

                if (i != blockCount - 1) // last block
                {
#if BLOCK_LEASE_TRACKING
                    block.IsLeased = true;
#endif
                    Return(block);
                }

                offset += _blockSize;
            }

            return block;
        }

        /// <summary>
        /// Called to return a block to the pool. Once Return has been called the memory no longer belongs to the caller, and
        /// Very Bad Things will happen if the memory is read of modified subsequently. If a caller fails to call Return and the
        /// block tracking object is garbage collected, the block tracking object's finalizer will automatically re-create and return
        /// a new tracking object into the pool. This will only happen if there is a bug in the server, however it is necessary to avoid
        /// leaving "dead zones" in the slab due to lost block tracking objects.
        /// </summary>
        /// <param name="block">The block to return. It must have been acquired by calling Lease on the same memory pool instance.</param>
        internal void Return(MemoryPoolBlock block)
        {
#if BLOCK_LEASE_TRACKING
            Debug.Assert(block.Pool == this, "Returned block was not leased from this pool");
            Debug.Assert(block.IsLeased, $"Block being returned to pool twice: {block.Leaser}{Environment.NewLine}");
            block.IsLeased = false;
#endif

            if (!_isDisposed)
            {
                _blocks.Enqueue(block);
            }
            else
            {
                GC.SuppressFinalize(block);
            }
        }

        // This method can ONLY be called from the finalizer of MemoryPoolBlock
        internal void RefreshBlock(MemoryPoolSlab slab, int offset, int length)
        {
            lock (_disposeSync)
            {
                if (!_isDisposed && slab != null && slab.IsActive)
                {
                    // Need to make a new object because this one is being finalized
                    // Note, this must be called within the _disposeSync lock because the block
                    // could be disposed at the same time as the finalizer.
                    Return(new MemoryPoolBlock(this, slab, offset, length));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            lock (_disposeSync)
            {
                _isDisposed = true;

                if (disposing)
                {
                    while (_slabs.TryPop(out MemoryPoolSlab slab))
                    {
                        // dispose managed state (managed objects).
                        slab.Dispose();
                    }
                }

                // Discard blocks in pool
                while (_blocks.TryDequeue(out MemoryPoolBlock block))
                {
                    GC.SuppressFinalize(block);
                }
            }
        }
    }

    /// <summary>
    /// Block tracking object used by the byte buffer memory pool. A slab is a large allocation which is divided into smaller blocks. The
    /// individual blocks are then treated as independent array segments.
    /// </summary>
    internal sealed class MemoryPoolBlock : IMemoryOwner<byte>
    {
        private readonly int _offset;
        private readonly int _length;

        /// <summary>
        /// This object cannot be instantiated outside of the static Create method
        /// </summary>
        internal MemoryPoolBlock(SlabMemoryPool pool, MemoryPoolSlab slab, int offset, int length)
        {
            _offset = offset;
            _length = length;

            Pool = pool;
            Slab = slab;

            Memory = MemoryMarshal.CreateFromPinnedArray(slab.Array, _offset, _length);
        }

        /// <summary>
        /// Back-reference to the memory pool which this block was allocated from. It may only be returned to this pool.
        /// </summary>
        public SlabMemoryPool Pool { get; }

        /// <summary>
        /// Back-reference to the slab from which this block was taken, or null if it is one-time-use memory.
        /// </summary>
        public MemoryPoolSlab Slab { get; }

        public Memory<byte> Memory { get; }

        ~MemoryPoolBlock()
        {
            Pool.RefreshBlock(Slab, _offset, _length);
        }

        public void Dispose()
        {
            Pool.Return(this);
        }

        public void Lease()
        {
        }
    }

    /// <summary>
    /// Slab tracking object used by the byte buffer memory pool. A slab is a large allocation which is divided into smaller blocks. The
    /// individual blocks are then treated as independent array segments.
    /// </summary>
    internal class MemoryPoolSlab : IDisposable
    {
        /// <summary>
        /// This handle pins the managed array in memory until the slab is disposed. This prevents it from being
        /// relocated and enables any subsections of the array to be used as native memory pointers to P/Invoked API calls.
        /// </summary>
        private GCHandle _gcHandle;
        private bool _isDisposed;

        public MemoryPoolSlab(byte[] data)
        {
            Array = data;
            _gcHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            NativePointer = _gcHandle.AddrOfPinnedObject();
        }

        /// <summary>
        /// True as long as the blocks from this slab are to be considered returnable to the pool. In order to shrink the
        /// memory pool size an entire slab must be removed. That is done by (1) setting IsActive to false and removing the
        /// slab from the pool's _slabs collection, (2) as each block currently in use is Return()ed to the pool it will
        /// be allowed to be garbage collected rather than re-pooled, and (3) when all block tracking objects are garbage
        /// collected and the slab is no longer references the slab will be garbage collected and the memory unpinned will
        /// be unpinned by the slab's Dispose.
        /// </summary>
        public bool IsActive => !_isDisposed;

        public IntPtr NativePointer { get; private set; }

        public byte[] Array { get; private set; }

        public static MemoryPoolSlab Create(int length)
        {
            // allocate and pin requested memory length
            var array = new byte[length];

            // allocate and return slab tracking object
            return new MemoryPoolSlab(array);
        }

        protected void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            Array = null;
            NativePointer = IntPtr.Zero; ;

            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
        }

        ~MemoryPoolSlab()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

    }

    internal class MemoryPoolThrowHelper
    {
        public static void ThrowArgumentOutOfRangeException_BufferRequestTooLarge(int maxSize)
        {
            throw GetArgumentOutOfRangeException_BufferRequestTooLarge(maxSize);
        }

        public static void ThrowObjectDisposedException(ExceptionArgument argument)
        {
            throw GetObjectDisposedException(argument);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ArgumentOutOfRangeException GetArgumentOutOfRangeException_BufferRequestTooLarge(int maxSize)
        {
            return new ArgumentOutOfRangeException(GetArgumentName(ExceptionArgument.size), $"Cannot allocate more than {maxSize} bytes in a single buffer");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ObjectDisposedException GetObjectDisposedException(ExceptionArgument argument)
        {
            return new ObjectDisposedException(GetArgumentName(argument));
        }

        private static string GetArgumentName(ExceptionArgument argument)
        {
            Debug.Assert(Enum.IsDefined(typeof(ExceptionArgument), argument), "The enum value is not defined, please check the ExceptionArgument Enum.");

            return argument.ToString();
        }

        internal enum ExceptionArgument
        {
            size,
            offset,
            length,
            MemoryPoolBlock,
            MemoryPool
        }
    }
}