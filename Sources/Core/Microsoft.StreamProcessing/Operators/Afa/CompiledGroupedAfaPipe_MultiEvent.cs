﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Microsoft.StreamProcessing.Internal;
using Microsoft.StreamProcessing.Internal.Collections;

namespace Microsoft.StreamProcessing
{
    [DataContract]
    internal sealed class CompiledGroupedAfaPipe_MultiEvent<TKey, TPayload, TRegister, TAccumulator> : CompiledAfaPipeBase<TKey, TPayload, TRegister, TAccumulator>
    {
        [DataMember]
        private FastMap<GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>> activeStates;
        [DataMember]
        private FastMap<TKey> keyHeads;

        // Traversers
        private FastMap<GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>>.FindTraverser activeFindTraverser;
        private FastMap<TKey>.FindTraverser keyHeadsFindTraverser;
        private FastMap<TKey>.VisibleTraverser keyHeadsVisibleTraverser;

        [DataMember]
        private long lastSyncTime;

        [Obsolete("Used only by serialization. Do not call directly.")]
        public CompiledGroupedAfaPipe_MultiEvent() { }

        public CompiledGroupedAfaPipe_MultiEvent(Streamable<TKey, TRegister> stream, IStreamObserver<TKey, TRegister> observer, object afa, long maxDuration)
            : base(stream, observer, afa, maxDuration)
        {
            this.activeStates = new FastMap<GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>>(1);
            this.activeFindTraverser = new FastMap<GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>>.FindTraverser(this.activeStates);

            this.keyHeads = new FastMap<TKey>(1);
            this.keyHeadsFindTraverser = new FastMap<TKey>.FindTraverser(this.keyHeads);
            this.keyHeadsVisibleTraverser = new FastMap<TKey>.VisibleTraverser(this.keyHeads);

            this.lastSyncTime = -1;
        }

        public override int CurrentlyBufferedInputCount => this.activeStates.Count;

        private void ProcessCurrentTimestamp()
        {
            this.keyHeadsVisibleTraverser.currIndex = 0;
            bool ended = true;

            while (this.keyHeadsVisibleTraverser.Next(out int index, out int hash))
            {
                if (this.activeFindTraverser.Find(hash))
                {
                    while (this.activeFindTraverser.Next(out int activeFind_index))
                    {
                        var state2 = this.activeStates.Values[activeFind_index];
                        if (!this.keyEqualityComparer(state2.key, this.keyHeads.Values[index])) continue;

                        // We guarantee by construction that new transitions are the end of the linked list
                        if (state2.arcinfo.fromStartState && !this.AllowOverlappingInstances && !ended)
                        {
                            state2.arcinfo.Dispose?.Invoke(this.activeStates.Values[activeFind_index].accumulator);
                            this.activeFindTraverser.Remove();
                        }
                        else
                        {
                            // Found tentative entry, complete transition
                            if (state2.arcinfo.Fence(this.lastSyncTime, state2.accumulator, state2.register))
                            {
                                if (state2.arcinfo.Transfer != null) this.activeStates.Values[activeFind_index].register = state2.arcinfo.Transfer(this.lastSyncTime, state2.accumulator, state2.register);
                                state2.arcinfo.Dispose?.Invoke(this.activeStates.Values[activeFind_index].accumulator);

                                if (this.isFinal[state2.toState])
                                {
                                    this.batch.vsync.col[this.iter] = this.lastSyncTime;
                                    this.batch.vother.col[this.iter] = Math.Min(state2.PatternStartTimestamp + this.MaxDuration, StreamEvent.InfinitySyncTime);
                                    this.batch.payload.col[this.iter] = this.activeStates.Values[activeFind_index].register;
                                    this.batch.key.col[this.iter] = state2.key;
                                    this.batch.hash.col[this.iter] = hash;
                                    this.iter++;

                                    if (this.iter == Config.DataBatchSize)
                                        FlushContents();
                                }

                                if (this.hasOutgoingArcs[state2.toState])
                                    ended = false;
                                else
                                    this.activeFindTraverser.Remove();
                            }
                            else
                            {
                                state2.arcinfo.Dispose?.Invoke(this.activeStates.Values[activeFind_index].accumulator);
                                this.activeFindTraverser.Remove();
                            }
                        }
                    }
                }
            }

            this.keyHeads.Clear();
        }

        public override unsafe void OnNext(StreamMessage<TKey, TPayload> batch)
        {
            var count = batch.Count;
            var srckey = batch.key.col;

            fixed (long* src_bv = batch.bitvector.col, src_vsync = batch.vsync.col)
            {
                fixed (int* src_hash = batch.hash.col)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if ((src_bv[i >> 6] & (1L << (i & 0x3f))) == 0 || batch.vother.col[i] < 0)
                        {
                            long synctime = src_vsync[i];

                            if (synctime > this.lastSyncTime) // move time forward
                            {
                                ProcessCurrentTimestamp();
                                this.lastSyncTime = synctime;
                            }

                            if (batch.vother.col[i] < 0)
                            {
                                OnPunctuation(synctime);
                                continue;
                            }

                            int keyHeads_index;
                            bool keyHeadExists = false;
                            if (this.keyHeadsFindTraverser.Find(src_hash[i]))
                            {
                                while (this.keyHeadsFindTraverser.Next(out keyHeads_index))
                                {
                                    if (!this.keyEqualityComparer(this.keyHeads.Values[keyHeads_index], srckey[i])) continue;

                                    // Found entry, this key has been processed before
                                    keyHeadExists = true;
                                    break;
                                }
                            }

                            if (!keyHeadExists)
                            {
                                // Apply new transitions, update existing transitions
                                bool found = this.activeFindTraverser.Find(src_hash[i]);

                                if (found)
                                {
                                    // Track which active states need to be inserted after the current traversal
                                    var newActiveStates = new List<GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>>();
                                    while (this.activeFindTraverser.Next(out int activeFind_index))
                                    {
                                        var state = this.activeStates.Values[activeFind_index];
                                        if (!this.keyEqualityComparer(state.key, srckey[i])) continue;

                                        // TODO: Found entry, create and accumulate new tentative transitions from current state
                                        if (state.PatternStartTimestamp + this.MaxDuration > synctime)
                                        {
                                            var currentStateMap = this.multiEventStateMap[state.toState];
                                            if (currentStateMap != null)
                                            {
                                                var m = currentStateMap.Length;
                                                for (int cnt = 0; cnt < m; cnt++)
                                                {
                                                    var arcinfo = currentStateMap[cnt];
                                                    var accumulator = arcinfo.Initialize(synctime, state.register);

                                                    // Since we will eventually remove this state/index from activeStates, attempt to reuse this index for the outgoing state instead of deleting/re-adding
                                                    // If index is already -1, this means we've already reused the state and must allocate/insert a new index for the outgoing state.
                                                    if (activeFind_index != -1)
                                                    {
                                                        this.activeStates.Values[activeFind_index].arcinfo = arcinfo;
                                                        this.activeStates.Values[activeFind_index].key = state.key;
                                                        this.activeStates.Values[activeFind_index].fromState = state.toState;
                                                        this.activeStates.Values[activeFind_index].toState = arcinfo.toState;
                                                        this.activeStates.Values[activeFind_index].PatternStartTimestamp = state.PatternStartTimestamp;
                                                        this.activeStates.Values[activeFind_index].register = state.register;
                                                        this.activeStates.Values[activeFind_index].accumulator = arcinfo.Accumulate(synctime, batch.payload.col[i], state.register, accumulator);
                                                        activeFind_index = -1;
                                                    }
                                                    else
                                                    {
                                                        // Do not attempt to insert directly into activeStates, as that could corrupt the traversal state.
                                                        newActiveStates.Add(new GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>
                                                        {
                                                            arcinfo = arcinfo,
                                                            key = state.key,
                                                            fromState = state.toState,
                                                            toState = arcinfo.toState,
                                                            PatternStartTimestamp = state.PatternStartTimestamp,
                                                            register = state.register,
                                                            accumulator = arcinfo.Accumulate(synctime, batch.payload.col[i], state.register, accumulator),
                                                        });
                                                    }
                                                }
                                            }
                                        }

                                        // Remove current state
                                        if (activeFind_index != -1) this.activeFindTraverser.Remove();
                                    }

                                    // Now that we are done traversing the current active states, add any new ones.
                                    foreach (var newActiveState in newActiveStates)
                                    {
                                        this.activeStates.Insert(src_hash[i], newActiveState);
                                    }
                                }

                                // Insert & accumulate new tentative transitions from start state
                                for (int counter = 0; counter < this.numStartStates; counter++)
                                {
                                    int startState = this.startStates[counter];
                                    var startStateMap = this.multiEventStateMap[startState];
                                    var m = startStateMap.Length;
                                    for (int cnt = 0; cnt < m; cnt++)
                                    {
                                        var arcinfo = startStateMap[cnt];

                                        int index = this.activeFindTraverser.InsertAt(); // have to ensure the new states go to the end of the list
                                        this.activeStates.Values[index].arcinfo = arcinfo;
                                        this.activeStates.Values[index].key = srckey[i];
                                        this.activeStates.Values[index].fromState = startState;
                                        this.activeStates.Values[index].toState = arcinfo.toState;
                                        this.activeStates.Values[index].PatternStartTimestamp = synctime;
                                        this.activeStates.Values[index].register = this.defaultRegister;
                                        this.activeStates.Values[index].accumulator = arcinfo.Initialize(synctime, this.defaultRegister);
                                        this.activeStates.Values[index].accumulator = arcinfo.Accumulate(synctime, batch.payload.col[i], this.defaultRegister, this.activeStates.Values[index].accumulator);
                                    }
                                }

                                // Update keyHeads to indicate that this key has been inserted
                                keyHeads_index = this.keyHeads.Insert(src_hash[i]);
                                this.keyHeads.Values[keyHeads_index] = srckey[i];

                                // Done processing this event
                                continue;
                            }

                            // Not the first insert of this key for this timestamp, perform accumulate for all tentative states
                            if (this.activeFindTraverser.Find(src_hash[i]))
                            {
                                while (this.activeFindTraverser.Next(out int activeFind_index))
                                {
                                    var state2 = this.activeStates.Values[activeFind_index];
                                    if (!this.keyEqualityComparer(state2.key, srckey[i])) continue;

                                    // Found tentative entry, accumulate
                                    this.activeStates.Values[activeFind_index].accumulator = state2.arcinfo.Accumulate(synctime, batch.payload.col[i], state2.register, state2.accumulator);
                                }
                            }
                        }
                    }
                }
            }
            batch.Free();
        }

        protected override void UpdatePointers()
        {
            this.activeFindTraverser = new FastMap<GroupedActiveStateAccumulator<TKey, TPayload, TRegister, TAccumulator>>.FindTraverser(this.activeStates);
            this.keyHeadsFindTraverser = new FastMap<TKey>.FindTraverser(this.keyHeads);
            this.keyHeadsVisibleTraverser = new FastMap<TKey>.VisibleTraverser(this.keyHeads);
        }
    }
}