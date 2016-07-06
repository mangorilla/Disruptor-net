﻿using System;
using System.Threading;

namespace Disruptor
{
    /// <summary>
    /// Coordinator for claiming sequences for access to a data structure while tracking dependent <see cref="Sequence"/>s
    /// </summary>
    public abstract class Sequencer : ISequenced, ICursored
    {
        protected readonly Sequence _cursor = new Sequence(Sequence.InitialCursorValue);
        protected Volatile.Reference<Sequence[]> _gatingSequences = new Volatile.Reference<Sequence[]>(new Sequence[0]);

        protected readonly IWaitStrategy _waitStrategy;
        protected readonly int _bufferSize;

        /// <summary>
        /// Construct a Sequencer with the selected strategies.
        /// </summary>
        /// <param name="bufferSize"></param>
        /// <param name="waitStrategy">waitStrategy for those waiting on sequences.</param>
        public Sequencer(int bufferSize, IWaitStrategy waitStrategy)
        {
            if (bufferSize < 1)
            {
                throw new ArgumentException("bufferSize must not be less than 1");
            }
            if (!bufferSize.IsPowerOf2())
            {
                throw new ArgumentException("bufferSize must be a power of 2");
            }

            _bufferSize = bufferSize;
            _waitStrategy = waitStrategy;
        }

        /// <summary>
        /// Set the sequences that will gate publishers to prevent the buffer wrapping.
        /// 
        /// This method must be called prior to claiming sequences otherwise
        /// a <see cref="NullReferenceException"/> will be thrown.
        /// </summary>
        /// <param name="sequences">sequences to be to be gated on.</param>
        public void SetGatingSequences(params Sequence[] sequences)
        {
            _gatingSequences.WriteUnfenced(sequences);
        }

        /// <summary>
        /// Create a <see cref="ISequenceBarrier"/> that gates on the the cursor and a list of <see cref="Sequence"/>s
        /// </summary>
        /// <param name="sequencesToTrack"></param>
        /// <returns></returns>
        public ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack)
        {
            return new ProcessingSequenceBarrier(this, _waitStrategy, _cursor, sequencesToTrack);
        }
        
        /// <summary>
        /// The capacity of the data structure to hold entries.
        /// </summary>
        public int BufferSize => _bufferSize;

        /// <summary>
        /// Get the value of the cursor indicating the published sequence.
        /// </summary>
        public long Cursor => _cursor.Value;

        /// <summary>
        /// Has the buffer got capacity to allocate another sequence.  This is a concurrent
        /// method so the response should only be taken as an indication of available capacity.
        /// </summary>
        /// <param name="requiredCapacity">requiredCapacity in the buffer</param>
        /// <returns>true if the buffer has the capacity to allocate the next sequence otherwise false.</returns>
        public abstract bool HasAvailableCapacity(int requiredCapacity);

        /// <summary>
        /// Claim the next event in sequence for publishing.
        /// </summary>
        /// <returns></returns>
        public abstract long Next();

        /// <summary>
        /// Claim the next n events in sequence for publishing.  This is for batch event producing.  Using batch producing requires a little care and some math.
        /// <code>
        ///     int n = 10;
        ///     long hi = sequencer.next(n);
        ///     long lo = hi - (n - 1);
        ///     for (long sequence = lo; sequence <= hi; sequence++) {
        ///        // Do work.
        ///     }
        ///     sequencer.publish(lo, hi);
        /// </code>
        /// </summary>
        /// <param name="n">the number of sequences to claim</param>
        /// <returns>the highest claimed sequence value</returns>
        public abstract long Next(int n);

        /// <summary>
        /// Attempt to claim the next event in sequence for publishing.  Will return the number of the slot if there is at least<code>requiredCapacity</code> slots available.
        /// </summary>
        /// <returns>the claimed sequence value</returns>
        public abstract long TryNext();

        /// <summary>
        /// Attempt to claim the next event in sequence for publishing.  Will return the
        /// number of the slot if there is at least n slots
        /// available. 
        /// </summary>
        /// <param name="n">the number of sequences to claim</param>
        /// <returns>the claimed sequence value</returns>
        public abstract long TryNext(int n);

        /// <summary>
        /// Claim a specific sequence when only one publisher is involved.
        /// </summary>
        /// <param name="sequence">sequence to be claimed.</param>
        /// <returns>sequence just claimed.</returns>
        public abstract long Claim(long sequence);

        /// <summary>
        /// Publish an event and make it visible to <see cref="IEventProcessor"/>s
        /// </summary>
        /// <param name="sequence">sequence to be published</param>
        public abstract void Publish(long sequence);

        /// <summary>
        /// Publish an event and make it visible to <see cref="IEventProcessor"/>s
        /// </summary>
        public abstract void Publish(long lo, long hi);

        /// <summary>
        /// Get the remaining capacity for this sequencer. return The number of slots remaining.
        /// </summary>
        public abstract long GetRemainingCapacity();

        /// <summary>
        /// Confirms if a sequence is published and the event is available for use; non-blocking.
        /// </summary>
        /// <param name="sequence">sequence of the buffer to check</param>
        /// <returns>true if the sequence is available for use, false if not</returns>
        public abstract bool IsAvailable(long sequence);

        /// <summary>
        /// Get the highest sequence number that can be safely read from the ring buffer.  Depending
        /// on the implementation of the Sequencer this call may need to scan a number of values
        /// in the Sequencer.  The scan will range from nextSequence to availableSequence.  If
        /// there are no available values <code>>= nextSequence</code> the return value will be
        /// <code>nextSequence - 1</code>.  To work correctly a consumer should pass a value that
        /// it 1 higher than the last sequence that was successfully processed.
        /// </summary>
        /// <param name="nextSequence">The sequence to start scanning from.</param>
        /// <param name="availableSequence">The sequence to scan to.</param>
        /// <returns>The highest value that can be safely read, will be at least <code>nextSequence - 1</code>.</returns>
        public abstract long GetHighestPublishedSequence(long nextSequence, long availableSequence);

        /// <summary>
        /// Force the publication of a cursor sequence.
        /// 
        /// Only use this method when forcing a sequence and you are sure only one publisher exists.
        /// This will cause the cursor to advance to this sequence.
        /// </summary>
        /// <param name="sequence">sequence which is to be forced for publication.</param>
        public void ForcePublish(long sequence)
        {
            _cursor.LazySet(sequence);
            _waitStrategy.SignalAllWhenBlocking();
        }

        /// <summary>
        /// Add the specified gating sequences to this instance of the Disruptor.  They will
        /// safely and atomically added to the list of gating sequences. 
        /// </summary>
        /// <param name="gatingSequences">The sequences to add.</param>
        public void AddGatingSequences(params Sequence[] gatingSequences)
        {
            SequenceGroups.AddSequences(ref _gatingSequences, this, gatingSequences);
        }

        /// <summary>
        /// Remove the specified sequence from this sequencer.
        /// </summary>
        /// <param name="sequence">to be removed.</param>
        /// <returns>true if this sequence was found, false otherwise.</returns>
        public bool RemoveGatingSequence(Sequence sequence)
        {
            return SequenceGroups.RemoveSequence(ref _gatingSequences, sequence);
        }

        /// <summary>
        /// Get the current cursor value.
        /// </summary>
        /// <returns></returns>
        public long GetCursor()
        {
            return _cursor.Value;
        }

        /// <summary>
        /// Get the minimum sequence value from all of the gating sequences
        /// added to this ringBuffer.
        /// </summary>
        /// <returns>The minimum gating sequence or the cursor sequence if no sequences have been added.</returns>
        public long GetMinimumSequence()
        {
            return Util.GetMinimumSequence(_gatingSequences.ReadUnfenced(), _cursor.Value);
        }

        public EventPoller<T> NewPoller<T>(IDataProvider<T> provider, params Sequence[] gatingSequences)
        {
            return EventPoller<T>.NewInstance(provider, this, new Sequence(), _cursor, gatingSequences);
        }
    }
}