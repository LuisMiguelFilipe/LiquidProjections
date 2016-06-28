﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEventStore;
using NEventStore.Persistence;

namespace LiquidProjections.NEventStore
{
    public class NEventStoreAdapter : IEventStore
    {
        private readonly TimeSpan pollInterval;
        private readonly int maxPageSize;
        private readonly IPersistStreams eventStore;
        private readonly HashSet<Subscription> subscriptions = new HashSet<Subscription>();
        private volatile bool isDisposed;
        private readonly object subscriptionLock = new object();
        private Task<Page> currentLoader;
        private readonly LruCache<string, Transaction> transactionCache;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private CheckpointRequestTimestamp lastExistingCheckpointRequest;

        public NEventStoreAdapter(IPersistStreams eventStore, int cacheSize, TimeSpan pollInterval, int maxPageSize)
        {
            this.eventStore = eventStore;
            this.pollInterval = pollInterval;
            this.maxPageSize = maxPageSize;
            transactionCache = new LruCache<string, Transaction>(cacheSize);
        }

        public IObservable<IReadOnlyList<Transaction>> Subscribe(string checkpoint)
        {
            return new PagesAfterCheckpoint(this, checkpoint);
        }

        private async Task<Page> GetNextPage(string checkpoint)
        {
            if (isDisposed)
            {
                throw new ObjectDisposedException(typeof(NEventStoreAdapter).FullName);
            }

            Page pageFromCache = TryGetNextPageFromCache(checkpoint);

            if (pageFromCache.Transactions.Count > 0)
            {
                return pageFromCache;
            }

            Page loadedPage = await LoadNextPageSequentially(checkpoint);

            if (loadedPage.Transactions.Count == maxPageSize)
            {
                StartPreloadingNextPage(loadedPage.LastCheckpoint);
            }

            return loadedPage;
        }

        private Page TryGetNextPageFromCache(string checkpoint)
        {
            Transaction cachedNextTransaction;

            if (transactionCache.TryGet(checkpoint, out cachedNextTransaction))
            {
                var resultPage = new List<Transaction>(maxPageSize) {cachedNextTransaction};

                while (resultPage.Count < maxPageSize)
                {
                    string lastCheckpoint = cachedNextTransaction.Checkpoint;

                    if (transactionCache.TryGet(lastCheckpoint, out cachedNextTransaction))
                    {
                        resultPage.Add(cachedNextTransaction);
                    }
                    else
                    {
                        StartPreloadingNextPage(lastCheckpoint);
                        break;
                    }
                }

                return new Page(checkpoint, resultPage);
            }

            return new Page(checkpoint, new Transaction[0]);
        }

        private void StartPreloadingNextPage(string checkpoint)
        {
            // Ignore result.
            Task _ = LoadNextPageSequentially(checkpoint);
        }

        private async Task<Page> LoadNextPageSequentially(string checkpoint)
        {
            while (true)
            {
                if (isDisposed)
                {
                    return new Page(checkpoint, new Transaction[0]);
                }

                CheckpointRequestTimestamp effectiveLastExistingCheckpointRequest =
                    Volatile.Read(ref lastExistingCheckpointRequest);

                if ((effectiveLastExistingCheckpointRequest != null) &&
                    (effectiveLastExistingCheckpointRequest.Checkpoint == checkpoint))
                {
                    TimeSpan timeAfterPreviousRequest = DateTime.UtcNow - effectiveLastExistingCheckpointRequest.DateTimeUtc;

                    if (timeAfterPreviousRequest < pollInterval)
                    {
                        await Task.Delay(pollInterval - timeAfterPreviousRequest);
                    }
                }

                Page candidatePage = await TryLoadNextPageSequentiallyOrWaitForCurrentLoadingToFinish(checkpoint);

                if (candidatePage.PreviousCheckpoint == checkpoint && candidatePage.Transactions.Count > 0)
                {
                    return candidatePage;
                }
            }
        }

        private Task<Page> TryLoadNextPageSequentiallyOrWaitForCurrentLoadingToFinish(string checkpoint)
        {
            TaskCompletionSource<Page> taskCompletionSource = null;
            bool isTaskOwner = false;

            try
            {
                Task<Page> loader = Volatile.Read(ref currentLoader);

                if (loader == null)
                {
                    taskCompletionSource = new TaskCompletionSource<Page>();
                    Task<Page> oldLoader = Interlocked.CompareExchange(ref currentLoader, taskCompletionSource.Task, null);
                    isTaskOwner = oldLoader == null;
                    loader = isTaskOwner ? taskCompletionSource.Task : oldLoader;

                    if (isDisposed)
                    {
                        taskCompletionSource = null;
                        isTaskOwner = false;
                        return Task.FromResult(new Page(checkpoint, new Transaction[0]));
                    }
                }

                return loader;
            }
            finally
            {
                if (isTaskOwner)
                {
                    // Ignore result.
                    Task _ = TryLoadNextPageAndMakeLoaderComplete(checkpoint, taskCompletionSource);
                }
            }
        }

        private  async Task TryLoadNextPageAndMakeLoaderComplete(string checkpoint,
            TaskCompletionSource<Page> loaderCompletionSource)
        {
            Page nextPage;

            try
            {
                nextPage = await TryLoadNextPage(checkpoint);
            }
            catch (Exception exception)
            {
                loaderCompletionSource.SetException(exception);
                return;
            }
            finally
            {
                Volatile.Write(ref currentLoader, null);
            }

            loaderCompletionSource.SetResult(nextPage);
        }

        private async Task<Page> TryLoadNextPage(string checkpoint)
        {
            // Maybe it's just loaded to cache.
            Page cachedPage = TryGetNextPageFromCache(checkpoint);

            if (cachedPage.Transactions.Count > 0)
            {
                return cachedPage;
            }

            DateTime timeOfRequestUtc = DateTime.UtcNow;

            List<Transaction> transactions = await Task.Run(() =>
            {
                try
                {
                    return eventStore
                        .GetFrom(checkpoint)
                        .Take(maxPageSize)
                        .Select(ToTransaction)
                        .ToList();

                }
                catch
                {
                    // TODO: Properly log the exception
                    return new List<Transaction>();
                }
            });

            if (transactions.Count > 0)
            {
                if (transactions.Count < maxPageSize)
                {
                    Volatile.Write(
                        ref lastExistingCheckpointRequest,
                        new CheckpointRequestTimestamp(transactions[transactions.Count - 1].Checkpoint, timeOfRequestUtc));
                }

                /* Add to cache in reverse order to prevent other projectors
                    from requesting already loaded transactions which are not added to cache yet. */
                for (int index = transactions.Count - 1; index > 0; index--)
                {
                    transactionCache.Set(transactions[index - 1].Checkpoint, transactions[index]);
                }

                transactionCache.Set(checkpoint, transactions[0]);
            }

            return new Page(checkpoint, transactions);
        }

        private Transaction ToTransaction(ICommit commit)
        {
            return new Transaction
            {
                Id = commit.CommitId.ToString(),
                StreamId = commit.StreamId,
                Checkpoint = commit.CheckpointToken,
                TimeStampUtc = commit.CommitStamp,
                Events = new List<EventEnvelope>(commit.Events.Select(@event => new EventEnvelope
                {
                    Body = @event.Body,
                    Headers = @event.Headers
                }))
            };
        }

        public void Dispose()
        {
            lock (subscriptionLock)
            {
                if (!isDisposed)
                {
                    isDisposed = true;

                    cancellationTokenSource.Cancel();

                    foreach (var subscription in subscriptions)
                    {
                        subscription.Complete();
                    }

                    Task loaderToWaitFor = Volatile.Read(ref currentLoader);
                    loaderToWaitFor?.Wait();

                    cancellationTokenSource.Dispose();
                    eventStore.Dispose();
                }
            }
        }

        private sealed class Page
        {
            public Page(string previousCheckpoint, IReadOnlyList<Transaction> transactions)
            {
                PreviousCheckpoint = previousCheckpoint;
                Transactions = transactions;
            }

            public string PreviousCheckpoint { get; }
            public IReadOnlyList<Transaction> Transactions { get; }

            public string LastCheckpoint => (Transactions.Count == 0) ? null : Transactions[Transactions.Count - 1].Checkpoint;
        }

        private sealed class PagesAfterCheckpoint : IObservable<IReadOnlyList<Transaction>>
        {
            private readonly NEventStoreAdapter eventStoreClient;
            private readonly string checkpoint;

            public PagesAfterCheckpoint(NEventStoreAdapter eventStoreClient, string checkpoint)
            {
                this.eventStoreClient = eventStoreClient;
                this.checkpoint = checkpoint;
            }

            public IDisposable Subscribe(IObserver<IReadOnlyList<Transaction>> observer)
            {
                if (observer == null)
                {
                    throw new ArgumentNullException(nameof(observer));
                }

                Subscription subscription;

                lock (eventStoreClient.subscriptionLock)
                {
                    if (eventStoreClient.isDisposed)
                    {
                        throw new ObjectDisposedException(typeof(NEventStoreAdapter).FullName);
                    }

                    subscription = new Subscription(eventStoreClient, checkpoint, observer);
                    eventStoreClient.subscriptions.Add(subscription);
                }

                subscription.Start();
                return subscription;
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly NEventStoreAdapter eventStoreClient;
            private CancellationTokenSource cancellationTokenSource;
            private readonly object syncRoot = new object();
            private bool isDisposed;
            private string lastCheckpoint;
            private readonly IObserver<IReadOnlyList<Transaction>> observer;
            private volatile bool hasFailed;

            public Subscription(NEventStoreAdapter eventStoreClient, string checkpoint,
                IObserver<IReadOnlyList<Transaction>> observer)
            {
                this.eventStoreClient = eventStoreClient;
                lastCheckpoint = checkpoint;
                this.observer = observer;
            }

            public Task Task { get; private set; }

            public void Start()
            {
                if (Task != null)
                {
                    throw new InvalidOperationException("Already started.");
                }

                lock (syncRoot)
                {
                    cancellationTokenSource = new CancellationTokenSource();
                    Task = Task.Factory
                        .StartNew(
                            RunAsync,
                            cancellationTokenSource.Token,
                            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                            TaskScheduler.Default)
                        .Unwrap();
                }
            }

            public void Complete()
            {
                Dispose();

                if (!hasFailed)
                {
                    observer.OnCompleted();
                }
            }

            public void Dispose()
            {
                bool isDisposing;

                lock (syncRoot)
                {
                    isDisposing = !isDisposed;

                    if (isDisposing)
                    {
                        isDisposed = true;
                    }
                }

                if (isDisposing)
                {
                    if (cancellationTokenSource != null)
                    {
                        if (!cancellationTokenSource.IsCancellationRequested)
                        {
                            cancellationTokenSource.Cancel();
                        }

                        Task?.Wait();
                        cancellationTokenSource.Dispose();
                    }

                    lock (eventStoreClient.subscriptionLock)
                    {
                        eventStoreClient.subscriptions.Remove(this);
                    }
                }
            }

            private async Task RunAsync()
            {
                try
                {
                    while (!cancellationTokenSource.IsCancellationRequested)
                    {
                        var page = await eventStoreClient.GetNextPage(lastCheckpoint);
                        observer.OnNext(page.Transactions);
                        lastCheckpoint = page.LastCheckpoint;
                    }
                }
                catch (Exception exception)
                {
                    hasFailed = true;
                    observer.OnError(exception);
                    throw;
                }
            }
        }

        private sealed class CheckpointRequestTimestamp
        {
            public CheckpointRequestTimestamp(string checkpoint, DateTime dateTimeUtc)
            {
                Checkpoint = checkpoint;
                DateTimeUtc = dateTimeUtc;
            }

            public string Checkpoint { get; }
            public DateTime DateTimeUtc { get; }
        }
    }
}