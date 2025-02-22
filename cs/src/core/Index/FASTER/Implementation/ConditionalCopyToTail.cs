﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FASTER.core
{
    public unsafe partial class FasterKV<Key, Value> : FasterBase, IFasterKV<Key, Value>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OperationStatus ConditionalCopyToTail<Input, Output, Context, FasterSession>(FasterSession fasterSession,
                ref PendingContext<Input, Output, Context> pendingContext,
                ref Key key, ref Input input, ref Value value, ref Output output, ref Context userContext, long lsn,
                ref OperationStackContext<Key, Value> stackCtx, WriteReason writeReason)
            where FasterSession : IFasterSession<Key, Value, Input, Output, Context>
        {
            // We are called by one of ReadFromImmutable, CompactionConditionalCopyToTail, or ContinueConditionalCopyToTail, and stackCtx is set up for the first try.
            // minAddress is the stackCtx.recSrc.LatestLogicalAddress; by the time we get here, any IO below that has been done due to PrepareConditionalCopyToTailIO,
            // which then went to ContinueConditionalCopyToTail, which evaluated whether the record was found at that level.
            while (true)
            {
                // ConditionalCopyToTail is different in regard to locking from the usual procedures, in that if we find a source record we don't lock--we exit with success.
                // So we only do LockTable-based locking and only when we are about to insert at the tail.
                if (TryTransientSLock<Input, Output, Context, FasterSession>(fasterSession, ref key, ref stackCtx, out OperationStatus status))
                {
                    try
                    {
                        RecordInfo dummyRecordInfo = default;   // TryCopyToTail only needs this for readcache record invalidation.
                        status = TryCopyToTail(ref pendingContext, ref key, ref input, ref value, ref output, ref stackCtx, ref dummyRecordInfo, fasterSession, writeReason);
                    }
                    finally
                    {
                        stackCtx.HandleNewRecordOnException(this);
                        TransientSUnlock<Input, Output, Context, FasterSession>(fasterSession, ref key, ref stackCtx);
                    }
                }
                if (!HandleImmediateRetryStatus(status, fasterSession, ref pendingContext))
                    return status;

                // Failed TryCopyToTail, probably a failed CAS due to another record insertion. Re-traverse from the tail to the highest point we just searched
                // (which may have gone below HeadAddress). +1 to LatestLogicalAddress because we have examined that already.
                var minAddress = stackCtx.recSrc.LatestLogicalAddress + 1;
                stackCtx = new(stackCtx.hei.hash);
                if (TryFindRecordInMainLogForConditionalCopyToTail(ref key, ref stackCtx, minAddress, out bool needIO))
                    return OperationStatus.SUCCESS;

                // Issue IO if necessary, else loop back up and retry the insert.
                if (needIO)
                    return PrepareIOForConditionalCopyToTail(fasterSession, ref pendingContext, ref key, ref input, ref value, ref output, ref userContext, lsn,
                                                      ref stackCtx, minAddress, WriteReason.Compaction);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Status CompactionConditionalCopyToTail<Input, Output, Context, FasterSession>(FasterSession fasterSession, ref Key key, ref Input input, ref Value value, 
                ref Output output, long minAddress)
            where FasterSession : IFasterSession<Key, Value, Input, Output, Context>
        {
            Debug.Assert(epoch.ThisInstanceProtected(), "This is called only from Compaction so the epoch should be protected");
            PendingContext<Input, Output, Context> pendingContext = new();

            OperationStackContext<Key, Value> stackCtx = new(comparer.GetHashCode64(ref key));
            if (TryFindRecordInMainLogForConditionalCopyToTail(ref key, ref stackCtx, minAddress, out bool needIO))
                return Status.CreateFound();

            Context userContext = default;
            OperationStatus status;
            if (needIO)
                status = PrepareIOForConditionalCopyToTail(fasterSession, ref pendingContext, ref key, ref input, ref value, ref output, ref userContext, 0L,
                                                    ref stackCtx, minAddress, WriteReason.Compaction);
            else
                status = ConditionalCopyToTail(fasterSession, ref pendingContext, ref key, ref input, ref value, ref output, ref userContext, 0L, ref stackCtx, WriteReason.Compaction);
            return HandleOperationStatus(fasterSession.Ctx, ref pendingContext, status, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private OperationStatus PrepareIOForConditionalCopyToTail<Input, Output, Context, FasterSession>(FasterSession fasterSession, ref PendingContext<Input, Output, Context> pendingContext,
                                        ref Key key, ref Input input, ref Value value, ref Output output, ref Context userContext, long lsn,
                                        ref OperationStackContext<Key, Value> stackCtx, long minAddress, WriteReason writeReason)
            where FasterSession : IFasterSession<Key, Value, Input, Output, Context>
        {
            pendingContext.type = OperationType.CONDITIONAL_INSERT;
            pendingContext.minAddress = minAddress;
            pendingContext.writeReason = writeReason;
            pendingContext.InitialEntryAddress = Constants.kInvalidAddress;
            pendingContext.InitialLatestLogicalAddress = stackCtx.recSrc.LatestLogicalAddress;

            if (!pendingContext.NoKey && pendingContext.key == default)    // If this is true, we don't have a valid key
                pendingContext.key = hlog.GetKeyContainer(ref key);
            if (pendingContext.input == default)
                pendingContext.input = fasterSession.GetHeapContainer(ref input);
            if (pendingContext.value == default)
                pendingContext.value = hlog.GetValueContainer(ref value);

            pendingContext.output = output;
            if (pendingContext.output is IHeapConvertible heapConvertible)
                heapConvertible.ConvertToHeap();

            pendingContext.userContext = userContext;
            pendingContext.logicalAddress = stackCtx.recSrc.LogicalAddress;
            pendingContext.version = fasterSession.Ctx.version;
            pendingContext.serialNum = lsn;

            return OperationStatus.RECORD_ON_DISK;
        }
    }
}
