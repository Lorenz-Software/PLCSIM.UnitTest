using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts;
using PLCSIM.UnitTest.AccessProfiles.Siemens.Contracts.Worker;
using PLCSIM.UnitTest.Common.UnitTests;
using System;
using System.Collections.Generic;

namespace PLCSIM.UnitTest.AccessProfiles.Siemens.PlcsimAdvanced.V7.Worker
{
    internal sealed class EventBufferPollingSnapshotClassifier
    {
        private readonly string contractVersion;
        private readonly string frameworkVersion;

        public EventBufferPollingSnapshotClassifier(string contractVersion, string frameworkVersion)
        {
            this.contractVersion = contractVersion ?? string.Empty;
            this.frameworkVersion = frameworkVersion ?? string.Empty;
        }

        public EventBufferPollingSnapshotClassification Classify(
            PlcsimEventBufferFifoControlSnapshot preFifoControl,
            PlcsimEventBufferStatusSnapshot preStatus,
            IReadOnlyDictionary<ushort, uint> sequenceIdsByBufferIndex,
            PlcsimEventBufferFifoControlSnapshot postFifoControl,
            PlcsimEventBufferStatusSnapshot postStatus,
            uint lastAcceptedSequenceId,
            DateTimeOffset observedAt)
        {
            return Classify(
                preFifoControl,
                preStatus,
                DeriveReadablePositions(preFifoControl),
                sequenceIdsByBufferIndex,
                postFifoControl,
                postStatus,
                lastAcceptedSequenceId,
                observedAt);
        }

        public EventBufferPollingSnapshotClassification Classify(
            PlcsimEventBufferFifoControlSnapshot preFifoControl,
            PlcsimEventBufferStatusSnapshot preStatus,
            IReadOnlyList<ushort> readablePositions,
            IReadOnlyDictionary<ushort, uint> sequenceIdsByBufferIndex,
            PlcsimEventBufferFifoControlSnapshot postFifoControl,
            PlcsimEventBufferStatusSnapshot postStatus,
            uint lastAcceptedSequenceId,
            DateTimeOffset observedAt)
        {
            readablePositions = readablePositions ?? Array.Empty<ushort>();
            sequenceIdsByBufferIndex = sequenceIdsByBufferIndex ?? new Dictionary<ushort, uint>();

            var readState = ClassifyControlTransition(preFifoControl, postFifoControl, readablePositions);
            if (readState is EventBufferReadState.RetryRequired or EventBufferReadState.Inconsistent)
            {
                return CreateResult(preFifoControl, preStatus, readablePositions, Array.Empty<PlcsimEventBufferEntrySnapshot>(), readState, observedAt);
            }

            if (readablePositions.Count == 0)
            {
                return CreateResult(preFifoControl, preStatus, readablePositions, Array.Empty<PlcsimEventBufferEntrySnapshot>(), EventBufferReadState.Empty, observedAt);
            }

            uint previousSequenceId = lastAcceptedSequenceId;
            var capturedSequences = new List<EventBufferCapturedSequence>();
            foreach (var bufferIndex in readablePositions)
            {
                if (!sequenceIdsByBufferIndex.TryGetValue(bufferIndex, out var sequenceId) || sequenceId == 0)
                {
                    return CreateResult(preFifoControl, preStatus, readablePositions, Array.Empty<PlcsimEventBufferEntrySnapshot>(), EventBufferReadState.RetryRequired, observedAt);
                }

                if (sequenceId <= previousSequenceId)
                {
                    return CreateResult(preFifoControl, preStatus, readablePositions, Array.Empty<PlcsimEventBufferEntrySnapshot>(), EventBufferReadState.Inconsistent, observedAt);
                }

                capturedSequences.Add(new EventBufferCapturedSequence(bufferIndex, sequenceId));
                previousSequenceId = sequenceId;
            }

            return CreateResult(preFifoControl, preStatus, readablePositions, Array.Empty<PlcsimEventBufferEntrySnapshot>(), EventBufferReadState.Valid, observedAt, capturedSequences);
        }

        public IReadOnlyList<ushort> DeriveReadablePositions(PlcsimEventBufferFifoControlSnapshot fifoControl)
        {
            return DeriveReadablePositions(fifoControl, int.MaxValue);
        }

        public IReadOnlyList<ushort> DeriveReadablePositions(
            PlcsimEventBufferFifoControlSnapshot fifoControl,
            int maximumCount)
        {
            if (maximumCount <= 0
                || fifoControl.BufferLength == 0
                || fifoControl.ConfigError
                || fifoControl.IndexError)
            {
                return Array.Empty<ushort>();
            }

            if (fifoControl.BufferEmpty)
            {
                return Array.Empty<ushort>();
            }

            if (!IsValidIndex(fifoControl.IndexOut, fifoControl.BufferLength)
                || !IsValidIndex(fifoControl.IndexIn, fifoControl.BufferLength))
            {
                return Array.Empty<ushort>();
            }

            var positions = new List<ushort>();
            var current = fifoControl.IndexOut;
            var count = fifoControl.BufferFull
                ? fifoControl.BufferLength
                : CountUntilInputBoundary(fifoControl.IndexOut, fifoControl.IndexIn, fifoControl.BufferLength);
            count = Math.Min(count, maximumCount);

            if (count == 0)
            {
                return Array.Empty<ushort>();
            }

            for (var index = 0; index < count; index++)
            {
                positions.Add(current);
                current = Advance(current, fifoControl.BufferLength);
            }

            return positions;
        }

        private EventBufferReadState ClassifyControlTransition(
            PlcsimEventBufferFifoControlSnapshot preFifoControl,
            PlcsimEventBufferFifoControlSnapshot postFifoControl,
            IReadOnlyList<ushort> preRange)
        {
            if (preFifoControl.ConfigError
                || preFifoControl.IndexError
                || postFifoControl.ConfigError
                || postFifoControl.IndexError)
            {
                return EventBufferReadState.RetryRequired;
            }

            if (preFifoControl.BufferLength == 0
                || !IsValidIndex(preFifoControl.IndexOut, preFifoControl.BufferLength)
                || !IsValidIndex(preFifoControl.IndexIn, preFifoControl.BufferLength)
                || postFifoControl.BufferLength == 0
                || !IsValidIndex(postFifoControl.IndexOut, postFifoControl.BufferLength)
                || !IsValidIndex(postFifoControl.IndexIn, postFifoControl.BufferLength))
            {
                return EventBufferReadState.Inconsistent;
            }

            if (preFifoControl.BufferEmpty)
            {
                return EventBufferReadState.Empty;
            }

            if (preRange.Count == 0)
            {
                return EventBufferReadState.Inconsistent;
            }

            if (preFifoControl.BufferLength != postFifoControl.BufferLength
                || preFifoControl.IndexOut != postFifoControl.IndexOut)
            {
                return EventBufferReadState.RetryRequired;
            }

            if ((!preFifoControl.BufferEmpty && postFifoControl.BufferEmpty)
                || (preFifoControl.BufferFull && !postFifoControl.BufferFull))
            {
                return EventBufferReadState.RetryRequired;
            }

            return EventBufferReadState.Valid;
        }

        private EventBufferPollingSnapshotClassification CreateResult(
            PlcsimEventBufferFifoControlSnapshot fifoControl,
            PlcsimEventBufferStatusSnapshot status,
            IReadOnlyList<ushort> readablePositions,
            IReadOnlyList<PlcsimEventBufferEntrySnapshot> entries,
            EventBufferReadState readState,
            DateTimeOffset observedAt,
            IReadOnlyList<EventBufferCapturedSequence>? capturedSequences = null)
        {
            var snapshot = new PlcsimEventBufferSnapshot(
                new PlcsimEventBufferContractSnapshot(
                    contractVersion,
                    frameworkVersion,
                    ContractValid: !string.IsNullOrWhiteSpace(contractVersion)),
                fifoControl,
                status,
                entries,
                readState,
                observedAt);

            return new EventBufferPollingSnapshotClassification(
                snapshot,
                readablePositions,
                capturedSequences ?? Array.Empty<EventBufferCapturedSequence>());
        }

        private static int CountUntilInputBoundary(ushort indexOut, ushort indexIn, ushort bufferLength)
        {
            if (indexOut == indexIn)
            {
                return 0;
            }

            var count = 0;
            var current = indexOut;
            while (current != indexIn)
            {
                count++;
                current = Advance(current, bufferLength);
                if (count > bufferLength)
                {
                    return 0;
                }
            }

            return count;
        }

        private static ushort Advance(ushort current, ushort bufferLength)
        {
            return current >= bufferLength ? (ushort)1 : (ushort)(current + 1);
        }

        private static bool IsValidIndex(ushort index, ushort bufferLength)
        {
            return index >= 1 && index <= bufferLength;
        }
    }

}
