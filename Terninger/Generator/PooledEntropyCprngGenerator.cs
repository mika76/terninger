﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.Cryptography;

using MurrayGrant.Terninger.Helpers;
using MurrayGrant.Terninger.Accumulator;
using MurrayGrant.Terninger.EntropySources;

using BigMath;

namespace MurrayGrant.Terninger.Generator
{
    public class PooledEntropyCprngGenerator : IDisposableRandomNumberGenerator
    {
        // The main random number generator for Fortuna and Terniner.
        // As specified in sections TODO

        // The PRNG based on a cypher or other crypto primitive, as specifeid in section 9.4.
        private readonly IReseedableRandomNumberGenerator _Prng;
        private readonly object _PrngLock = new object();
        // The entropy accumulator, as specified in section 9.5.
        private readonly EntropyAccumulator _Accumulator;
        private readonly object _AccumulatorLock = new object();
        // Multiple entropy sources, as specified in section 9.5.1.
        private readonly List<IEntropySource> _EntropySources;
        public int SourceCount => this._EntropySources.Count;

        // A thread used to schedule reading from entropy sources.
        // TODO: make an interface so we don't need to own a real thread (eg: instead use WinForms timers).
        private readonly Thread _SchedulerThread;

        private bool _Disposed = false;

        public int MaxRequestBytes => _Prng.MaxRequestBytes;
 
        public Guid UniqueId { get; private set; }

        public Int128 BytesRequested { get; private set; }
        public Int128 ReseedCount => this._Accumulator.TotalReseedEvents;

        /// <summary>
        /// Reports how aggressively the generator is trying to read entropy.
        /// </summary>
        public EntropyPriority EntropyPriority { get; private set; }

        /// <summary>
        /// True if the generator is currently gathering entropy.
        /// </summary>
        public bool IsRunning => _SchedulerThread.IsAlive;
        private readonly CancellationTokenSource _ShouldStop = new CancellationTokenSource();
        private readonly EventWaitHandle _WakeSignal = new EventWaitHandle(false, EventResetMode.AutoReset);
        private readonly WaitHandle[] _AllSignals;

        /// <summary>
        /// Event is raised after each time the generator is reseeded.
        /// </summary>
        public event EventHandler<PooledEntropyCprngGenerator> OnReseed;


        public PooledEntropyCprngGenerator(IEnumerable<IEntropySource> initialisedSources) 
            : this(initialisedSources, new EntropyAccumulator(), CypherBasedPrngGenerator.CreateWithNullKey()) { }
        public PooledEntropyCprngGenerator(IEnumerable<IEntropySource> initialisedSources, EntropyAccumulator accumulator)
            : this(initialisedSources, accumulator, CypherBasedPrngGenerator.CreateWithCheapKey()) { }
        public PooledEntropyCprngGenerator(IEnumerable<IEntropySource> initialisedSources, IReseedableRandomNumberGenerator prng)
            : this(initialisedSources, new EntropyAccumulator(), prng) { }

        /// <summary>
        /// Initialise the CPRNG with the given PRNG, accumulator, entropy sources and thread.
        /// This does not start the generator.
        /// </summary>
        public PooledEntropyCprngGenerator(IEnumerable<IEntropySource> initialisedSources, EntropyAccumulator accumulator, IReseedableRandomNumberGenerator prng)
        {
            if (initialisedSources == null) throw new ArgumentNullException(nameof(initialisedSources));
            if (accumulator == null) throw new ArgumentNullException(nameof(accumulator));
            if (prng == null) throw new ArgumentNullException(nameof(prng));

            this.UniqueId = Guid.NewGuid();
            this._Prng = prng;      // Note that this is keyed with a low entropy key.
            this._Accumulator = accumulator;
            this._EntropySources = new List<IEntropySource>(initialisedSources);
            this._SchedulerThread = new Thread(ThreadLoop, 256 * 1024);
            _SchedulerThread.Name = "Terninger Worker Thread - " + UniqueId.ToString("X");
            _SchedulerThread.IsBackground = true;
            this.EntropyPriority = EntropyPriority.High;        // A new generator must reseed as quickly as possible.
            _AllSignals = new[] { _WakeSignal, _ShouldStop.Token.WaitHandle };
        }

        public void Dispose()
        {
            if (_Disposed) return;

            lock (_PrngLock)
            {
                this.RequestStop();

                if (_Prng != null)
                    _Prng.TryDispose();
                if (_EntropySources != null)
                    foreach (var s in _EntropySources)
                        s.TryDispose();
                _Disposed = true;
            }
        }

        // Methods to get random bytes, as part of IRandomNumberGenerator.
        public void FillWithRandomBytes(byte[] toFill) => FillWithRandomBytes(toFill, 0, toFill.Length);
        public void FillWithRandomBytes(byte[] toFill, int offset, int count)
        {
            if (this.ReseedCount == 0)
                throw new InvalidOperationException("The random number generator has not accumulated enough entropy to be used. Please wait until ReSeedCount > 1, or await StartAndWaitForFirstSeed().");

            lock (_PrngLock)
            {
                _Prng.FillWithRandomBytes(toFill, offset, count);
            }
            this.BytesRequested = this.BytesRequested + count;
        }




        public void Start()
        {
            this._SchedulerThread.Start();
        }

        public Task StartAndWaitForFirstSeed() => StartAndWaitForNthSeed(1);
        public Task StartAndWaitForNthSeed(Int128 seedNumber)
        {
            if (this.ReseedCount >= seedNumber)
                return Task.FromResult(0);
            this.Start();
            return WaitForNthSeed(seedNumber);
        }

        private async Task WaitForNthSeed(Int128 seedNumber)
        {
            // TODO: work out how to do this without polling.
            while (this.ReseedCount < seedNumber)
                await Task.Delay(100);
        }

        public void RequestStop()
        {
            _ShouldStop.Cancel();
        }
        public async Task Stop()
        {
            _ShouldStop.Cancel();
            await Task.Delay(1);
            // TODO: work out how to do this without polling. Thread.Join() perhaps.
            while (this._SchedulerThread.IsAlive)
                await Task.Delay(100);
        }

        public void StartReseed()
        {
            Reseed();
        }
        public Task Reseed() {
            this.EntropyPriority = EntropyPriority.High;
            this._WakeSignal.Set();
            return WaitForNthSeed(this.ReseedCount + 1);
        }

        public void AddInitialisedSource(IEntropySource source)
        {
            lock(_EntropySources)
            {
                _EntropySources.Add(source);
            }
        }


        private void ThreadLoop()
        {
            while (!_ShouldStop.IsCancellationRequested)
            {
                // Read and randomise sources.
                IEntropySource[] sources;
                lock(_EntropySources)
                {
                    sources = _EntropySources.ToArray();
                }
                // There may not be any sources until some time after the generator is started.
                if (sources.Length == 0)
                {
                    // The thread should be woken on cancellation or external signal.
                    int wakeIdx = WaitHandle.WaitAny(_AllSignals, 100);
                    var wasTimeout = wakeIdx == WaitHandle.WaitTimeout;
                    continue;
                }
                lock (_PrngLock)
                {
                    // Sources are shuffled so the last source isn't easily determined (the last source can bias the accumulator, particularly if malicious).
                    sources.ShuffleInPlace(_Prng);
                }

                // Poll all sources.
                // TODO: read up to N in parallel.
                foreach (var source in _EntropySources)
                {
                    byte[] maybeEntropy;
                    try
                    {
                        // These may come from 3rd parties, use external hardware or do IO: anything could go wrong!
                        maybeEntropy = source.GetEntropyAsync(this.EntropyPriority).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // TODO: log exception
                        // TODO: if a particular source keeps throwing, should we ignore it??
                        maybeEntropy = null;
                    }
                    if (maybeEntropy != null)
                    {
                        lock (_AccumulatorLock)
                        {
                            _Accumulator.Add(new EntropyEvent(maybeEntropy, source));
                        }
                    }
                    if (_ShouldStop.IsCancellationRequested)
                        break;
                }

                // Determine if we should re-seed.
                if (this.ShouldReseed())
                {
                    byte[] seedMaterial;
                    lock (_AccumulatorLock)
                    {
                        seedMaterial = _Accumulator.NextSeed();
                    }
                    lock (this._PrngLock)
                    {
                        this._Prng.Reseed(seedMaterial);
                    }
                    if (this.EntropyPriority == EntropyPriority.High)
                        this.EntropyPriority = EntropyPriority.Normal;
                    this.OnReseed?.Invoke(this, this);
                }

                // Wait for some period of time before polling again.
                var sleepTime = WaitTimeBetweenPolls();
                if (sleepTime > TimeSpan.Zero)
                {
                    // The thread should be woken on cancellation or external signal.
                    int wakeIdx = WaitHandle.WaitAny(_AllSignals, sleepTime);
                    var wasTimeout = wakeIdx == WaitHandle.WaitTimeout;
                }
            }
        }

        private bool ShouldReseed()
        {
            // TODO: Fortuna requires minimum of 100ms between reseed events.
            if (this._ShouldStop.IsCancellationRequested)
                return false;
            else if (this.EntropyPriority == EntropyPriority.High)
                // TODO: configure how much entropy we need to accumulate before reseed.
                return this._Accumulator.PoolZeroEntropyBytesSinceLastSeed > 48;
            else if (this.EntropyPriority == EntropyPriority.Low)
                // TODO: use priority, rate of consumption and date / time to determine when to reseed.
                return this._Accumulator.MinPoolEntropyBytesSinceLastSeed > 256;
            else
                // TODO: use priority, rate of consumption and date / time to determine when to reseed.
                return this._Accumulator.MinPoolEntropyBytesSinceLastSeed > 96;
        }

        // TODO: work out how often to poll based on minimum and rate entropy is being consumed.
        // TODO: use a PRNG to introduce a random bias into this??
        private TimeSpan WaitTimeBetweenPolls() => EntropyPriority == EntropyPriority.High ? TimeSpan.FromMilliseconds(1)
                                                 : EntropyPriority == EntropyPriority.Normal ? TimeSpan.FromSeconds(5)
                                                 : EntropyPriority == EntropyPriority.Low ? TimeSpan.FromSeconds(30)
                                                 : TimeSpan.FromSeconds(1);     // Impossible case.
    }
}
