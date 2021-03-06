namespace NEventStore
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using NEventStore.Logging;

    [SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix",
        Justification = "This behaves like a stream--not a .NET 'Stream' object, but a stream nonetheless.")]
    public sealed class OptimisticEventStream : IEventStream
    {
        private static readonly ILog Logger = LogFactory.BuildLogger(typeof (OptimisticEventStream));
        private readonly ICollection<EventMessage> _committed = new LinkedList<EventMessage>();
        private readonly ICollection<EventMessage> _events = new LinkedList<EventMessage>();
        private readonly ICollection<Guid> _identifiers = new HashSet<Guid>();
        private readonly ICommitEvents _persistence;
        private bool _disposed;

        public OptimisticEventStream(string bucketId, string streamId, ICommitEvents persistence)
        {
            BucketId = bucketId;
            StreamId = streamId;
            _persistence = persistence;
        }

        public OptimisticEventStream(string bucketId, string streamId, ICommitEvents persistence, int minRevision, int maxRevision, CancellationToken cancellationToken)
            : this(bucketId, streamId, persistence)
        {
            var commits = persistence
                .GetFromAsync(bucketId, streamId, minRevision, maxRevision, cancellationToken)
                .GetAwaiter().GetResult();
            PopulateStream(minRevision, maxRevision, commits);

            if (minRevision > 0 && _committed.Count == 0)
            {
                throw new StreamNotFoundException(String.Format(Messages.StreamNotFoundException, streamId, BucketId));
            }
        }

        public OptimisticEventStream(ISnapshot snapshot, ICommitEvents persistence, int maxRevision, CancellationToken cancellationToken)
            : this(snapshot.BucketId, snapshot.StreamId, persistence)
        {
            var commits = persistence
                .GetFromAsync(snapshot.BucketId, snapshot.StreamId, snapshot.StreamRevision, maxRevision, cancellationToken)
                .GetAwaiter().GetResult();
            PopulateStream(snapshot.StreamRevision + 1, maxRevision, commits);
            StreamRevision = snapshot.StreamRevision + _committed.Count;
        }

        public string BucketId { get; }

        public string StreamId { get; }

        public int StreamRevision { get; private set; }

        public int CommitSequence { get; private set; }

        public ICollection<EventMessage> CommittedEvents => new ImmutableCollection<EventMessage>(_committed);

        public IDictionary<string, object> CommittedHeaders { get; } = new Dictionary<string, object>();

        public ICollection<EventMessage> UncommittedEvents => new ImmutableCollection<EventMessage>(_events);

        public IDictionary<string, object> UncommittedHeaders { get; } = new Dictionary<string, object>();

        public void Add(EventMessage uncommittedEvent)
        {
            if (uncommittedEvent == null)
            {
                throw new ArgumentNullException(nameof(uncommittedEvent));
            }

            if (uncommittedEvent.Body == null)
            {
                throw new ArgumentNullException("uncommittedEvent.Body");
            }

            Logger.Verbose(Resources.AppendingUncommittedToStream, uncommittedEvent.Body.GetType(), StreamId);
            _events.Add(uncommittedEvent);
        }

        public async Task CommitChangesAsync(Guid commitId, CancellationToken cancellationToken)
        {
            Logger.Verbose(Resources.AttemptingToCommitChanges, StreamId);

            if (_identifiers.Contains(commitId))
            {
                throw new DuplicateCommitException(String.Format(Messages.DuplicateCommitIdException, commitId));
            }

            if (!HasChanges())
            {
                return;
            }

            try
            {
                await PersistChanges(commitId, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException cex)
            {
                Logger.Debug(Resources.UnderlyingStreamHasChanged, StreamId);
                var commits = _persistence
                    .GetFromAsync(BucketId, StreamId, StreamRevision + 1, int.MaxValue, cancellationToken)
                    .GetAwaiter().GetResult();
                PopulateStream(StreamRevision + 1, int.MaxValue, commits);

                throw;
            }
        }

        public void ClearChanges()
        {
            Logger.Verbose(Resources.ClearingUncommittedChanges, StreamId);
            _events.Clear();
            UncommittedHeaders.Clear();
        }

        private void PopulateStream(int minRevision, int maxRevision, IEnumerable<ICommit> commits)
        {
            foreach (var commit in commits ?? Enumerable.Empty<ICommit>())
            {
                Logger.Verbose(Resources.AddingCommitsToStream, commit.CommitId, commit.Events.Count, StreamId);
                _identifiers.Add(commit.CommitId);

                CommitSequence = commit.CommitSequence;
                var currentRevision = commit.StreamRevision - commit.Events.Count + 1;
                if (currentRevision > maxRevision)
                {
                    return;
                }

                CopyToCommittedHeaders(commit);
                CopyToEvents(minRevision, maxRevision, currentRevision, commit);
            }
        }

        private void CopyToCommittedHeaders(ICommit commit)
        {
            foreach (var key in commit.Headers.Keys)
            {
                CommittedHeaders[key] = commit.Headers[key];
            }
        }

        private void CopyToEvents(int minRevision, int maxRevision, int currentRevision, ICommit commit)
        {
            foreach (var @event in commit.Events)
            {
                if (currentRevision > maxRevision)
                {
                    Logger.Debug(Resources.IgnoringBeyondRevision, commit.CommitId, StreamId, maxRevision);
                    break;
                }

                if (currentRevision++ < minRevision)
                {
                    Logger.Debug(Resources.IgnoringBeforeRevision, commit.CommitId, StreamId, maxRevision);
                    continue;
                }

                _committed.Add(@event);
                StreamRevision = currentRevision - 1;
            }
        }

        private bool HasChanges()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(Resources.AlreadyDisposed);
            }

            if (_events.Count > 0)
            {
                return true;
            }

            Logger.Info(Resources.NoChangesToCommit, StreamId);
            return false;
        }

        private async Task PersistChanges(Guid commitId, CancellationToken cancellationToken)
        {
            var attempt = BuildCommitAttempt(commitId);

            Logger.Debug(Resources.PersistingCommit, commitId, StreamId);
            var commit = await _persistence
                .CommitAsync(attempt, cancellationToken)
                .ConfigureAwait(false);

            PopulateStream(StreamRevision + 1, attempt.StreamRevision, new[] { commit });
            ClearChanges();
        }

        private CommitAttempt BuildCommitAttempt(Guid commitId)
        {
            Logger.Verbose(Resources.BuildingCommitAttempt, commitId, StreamId);
            return new CommitAttempt(
                BucketId,
                StreamId,
                StreamRevision + _events.Count,
                commitId,
                CommitSequence + 1,
                SystemTime.UtcNow,
                UncommittedHeaders.ToDictionary(x => x.Key, x => x.Value),
                _events.ToList());
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}