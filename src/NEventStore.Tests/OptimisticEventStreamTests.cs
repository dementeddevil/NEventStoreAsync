using System.Threading;

#pragma warning disable 169

namespace NEventStore
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;

    using FakeItEasy;
    using FluentAssertions;

    using NEventStore.Persistence;
    using NEventStore.Persistence.AcceptanceTests;
    using NEventStore.Persistence.AcceptanceTests.BDD;
#if MSTEST
    using Microsoft.VisualStudio.TestTools.UnitTesting;
#endif
#if NUNIT
    using NUnit.Framework;
#endif
#if XUNIT
    using Xunit;
    using Xunit.Should;
#endif

#if MSTEST
    [TestClass]
#endif
    public class when_building_a_stream : on_the_event_stream
    {
        private const int MinRevision = 2;
        private const int MaxRevision = 7;
        private readonly int _eachCommitHas = 2.Events();
        private ICommit[] _committed;

        protected override Task Context()
        {
            _committed = new[]
            {
                BuildCommitStub(2, 1, _eachCommitHas), // 1-2
                BuildCommitStub(4, 2, _eachCommitHas), // 3-4
                BuildCommitStub(6, 3, _eachCommitHas), // 5-6
                BuildCommitStub(8, 3, _eachCommitHas) // 7-8
            };

            _committed[0].Headers["Common"] = string.Empty;
            _committed[1].Headers["Common"] = string.Empty;
            _committed[2].Headers["Common"] = string.Empty;
            _committed[3].Headers["Common"] = string.Empty;
            _committed[0].Headers["Unique"] = string.Empty;

            A.CallTo(() => Persistence.GetFromAsync(BucketId, StreamId, MinRevision, MaxRevision)).Returns(_committed);
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            Stream = new OptimisticEventStream(BucketId, StreamId, Persistence, MinRevision, MaxRevision, CancellationToken.None);
            return Task.CompletedTask;
        }

        [Fact]
        public void should_have_the_correct_stream_identifier()
        {
            Stream.StreamId.Should().Be(StreamId);
        }

        [Fact]
        public void should_have_the_correct_head_stream_revision()
        {
            Stream.StreamRevision.Should().Be(MaxRevision);
        }

        [Fact]
        public void should_have_the_correct_head_commit_sequence()
        {
            Stream.CommitSequence.Should().Be(_committed.Last().CommitSequence);
        }

        [Fact]
        public void should_not_include_events_below_the_minimum_revision_indicated()
        {
            Stream.CommittedEvents.First().Should().Be(_committed.First().Events.Last());
        }

        [Fact]
        public void should_not_include_events_above_the_maximum_revision_indicated()
        {
            Stream.CommittedEvents.Last().Should().Be(_committed.Last().Events.First());
        }

        [Fact]
        public void should_have_all_of_the_committed_events_up_to_the_stream_revision_specified()
        {
            Stream.CommittedEvents.Count.Should().Be(MaxRevision - MinRevision + 1);
        }

        [Fact]
        public void should_contain_the_headers_from_the_underlying_commits()
        {
            Stream.CommittedHeaders.Count.Should().Be(2);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_the_head_event_revision_is_less_than_the_max_desired_revision : on_the_event_stream
    {
        private readonly int _eventsPerCommit = 2.Events();
        private ICommit[] _committed;

        protected override Task Context()
        {
            _committed = new[]
            {
                BuildCommitStub(2, 1, _eventsPerCommit), // 1-2
                BuildCommitStub(4, 2, _eventsPerCommit), // 3-4
                BuildCommitStub(6, 3, _eventsPerCommit), // 5-6
                BuildCommitStub(8, 3, _eventsPerCommit) // 7-8
            };

            A.CallTo(() => Persistence.GetFromAsync(BucketId, StreamId, 0, int.MaxValue)).Returns(_committed);
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            Stream = new OptimisticEventStream(BucketId, StreamId, Persistence, 0, int.MaxValue, CancellationToken.None);
            return Task.CompletedTask;
        }

        [Fact]
        public void should_set_the_stream_revision_to_the_revision_of_the_most_recent_event()
        {
            Stream.StreamRevision.Should().Be(_committed.Last().StreamRevision);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_adding_a_null_event_message : on_the_event_stream
    {
        private Exception _thrown;

        protected override Task Because()
        {
            _thrown = Catch.Exception(() => Stream.Add(null));
            return Task.CompletedTask;
        }

        [Fact]
        public void should_throw()
        {
            _thrown.Should().BeOfType<ArgumentNullException>();
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_adding_an_unpopulated_event_message : on_the_event_stream
    {
        private Exception _thrown;

        protected override Task Because()
        {
            _thrown = Catch.Exception(() => Stream.Add(new EventMessage { Body = null }));
            return Task.CompletedTask;
        }

        [Fact]
        public void should_throw()
        {
            _thrown.Should().BeOfType<ArgumentNullException>();
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_adding_a_fully_populated_event_message : on_the_event_stream
    {
        protected override Task Because()
        {
            Stream.Add(new EventMessage { Body = "populated" });
            return Task.CompletedTask;
        }

        [Fact]
        public void should_add_the_event_to_the_set_of_uncommitted_events()
        {
            Stream.UncommittedEvents.Count.Should().Be(1);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_adding_multiple_populated_event_messages : on_the_event_stream
    {
        protected override Task Because()
        {
            Stream.Add(new EventMessage { Body = "populated" });
            Stream.Add(new EventMessage { Body = "also populated" });
            return Task.CompletedTask;
        }

        [Fact]
        public void should_add_all_of_the_events_provided_to_the_set_of_uncommitted_events()
        {
            Stream.UncommittedEvents.Count.Should().Be(2);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_adding_a_simple_object_as_an_event_message : on_the_event_stream
    {
        private const string MyEvent = "some event data";

        protected override Task Because()
        {
            Stream.Add(new EventMessage { Body = MyEvent });
            return Task.CompletedTask;
        }

        [Fact]
        public void should_add_the_uncommited_event_to_the_set_of_uncommitted_events()
        {
            Stream.UncommittedEvents.Count.Should().Be(1);
        }

        [Fact]
        public void should_wrap_the_uncommited_event_in_an_EventMessage_object()
        {
            Stream.UncommittedEvents.First().Body.Should().Be(MyEvent);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_clearing_any_uncommitted_changes : on_the_event_stream
    {
        protected override Task Context()
        {
            Stream.Add(new EventMessage { Body = string.Empty });
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            Stream.ClearChanges();
            return Task.CompletedTask;
        }

        [Fact]
        public void should_clear_all_uncommitted_events()
        {
            Stream.UncommittedEvents.Count.Should().Be(0);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_committing_an_empty_changeset : on_the_event_stream
    {
        protected override Task Because()
        {
            return Stream.CommitChangesAsync(Guid.NewGuid(), CancellationToken.None);
        }

        [Fact]
        public void should_not_call_the_underlying_infrastructure()
        {
            A.CallTo(() => Persistence.CommitAsync(A<CommitAttempt>._, CancellationToken.None)).MustNotHaveHappened();
        }

        [Fact]
        public void should_not_increment_the_current_stream_revision()
        {
            Stream.StreamRevision.Should().Be(0);
        }

        [Fact]
        public void should_not_increment_the_current_commit_sequence()
        {
            Stream.CommitSequence.Should().Be(0);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_committing_any_uncommitted_changes : on_the_event_stream
    {
        private readonly Guid _commitId = Guid.NewGuid();
        private readonly Dictionary<string, object> _headers = new Dictionary<string, object> { { "key", "value" } };
        private readonly EventMessage _uncommitted = new EventMessage { Body = string.Empty };
        private CommitAttempt _constructed;

        protected override Task Context()
        {
            A.CallTo(() => Persistence.CommitAsync(A<CommitAttempt>._, CancellationToken.None))
                .Invokes((CommitAttempt _) => _constructed = _)
                .ReturnsLazily((CommitAttempt attempt) => new Commit(
                    attempt.BucketId,
                    attempt.StreamId,
                    attempt.StreamRevision,
                    attempt.CommitId,
                    attempt.CommitSequence,
                    attempt.CommitStamp,
                    0,
                    attempt.Headers,
                    attempt.Events));
            Stream.Add(_uncommitted);
            foreach (var item in _headers)
            {
                Stream.UncommittedHeaders[item.Key] = item.Value;
            }
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            return Stream.CommitChangesAsync(_commitId, CancellationToken.None);
        }

        [Fact]
        public void should_provide_a_commit_to_the_underlying_infrastructure()
        {
            A.CallTo(() => Persistence.CommitAsync(A<CommitAttempt>._, CancellationToken.None)).MustHaveHappened(Repeated.Exactly.Once);
        }

        [Fact]
        public void should_build_the_commit_with_the_correct_bucket_identifier()
        {
            _constructed.BucketId.Should().Be(BucketId);
        }

        [Fact]
        public void should_build_the_commit_with_the_correct_stream_identifier()
        {
            _constructed.StreamId.Should().Be(StreamId);
        }

        [Fact]
        public void should_build_the_commit_with_the_correct_stream_revision()
        {
            _constructed.StreamRevision.Should().Be(DefaultStreamRevision);
        }

        [Fact]
        public void should_build_the_commit_with_the_correct_commit_identifier()
        {
            _constructed.CommitId.Should().Be(_commitId);
        }

        [Fact]
        public void should_build_the_commit_with_an_incremented_commit_sequence()
        {
            _constructed.CommitSequence.Should().Be(DefaultCommitSequence);
        }

        [Fact]
        public void should_build_the_commit_with_the_correct_commit_stamp()
        {
            SystemTime.UtcNow.Should().Be(_constructed.CommitStamp);
        }

        [Fact]
        public void should_build_the_commit_with_the_headers_provided()
        {
            _constructed.Headers[_headers.First().Key].Should().Be(_headers.First().Value);
        }

        [Fact]
        public void should_build_the_commit_containing_all_uncommitted_events()
        {
            _constructed.Events.Count.Should().Be(_headers.Count);
        }

        [Fact]
        public void should_build_the_commit_using_the_event_messages_provided()
        {
            _constructed.Events.First().Should().Be(_uncommitted);
        }

        [Fact]
        public void should_contain_a_copy_of_the_headers_provided()
        {
            _constructed.Headers.Should().NotBeEmpty();
        }

        [Fact]
        public void should_update_the_stream_revision()
        {
            Stream.StreamRevision.Should().Be(_constructed.StreamRevision);
        }

        [Fact]
        public void should_update_the_commit_sequence()
        {
            Stream.CommitSequence.Should().Be(_constructed.CommitSequence);
        }

        [Fact]
        public void should_add_the_uncommitted_events_the_committed_events()
        {
            Stream.CommittedEvents.Last().Should().Be(_uncommitted);
        }

        [Fact]
        public void should_clear_the_uncommitted_events_on_the_stream()
        {
            Stream.UncommittedEvents.Should().BeEmpty();
        }

        [Fact]
        public void should_clear_the_uncommitted_headers_on_the_stream()
        {
            Stream.UncommittedHeaders.Should().BeEmpty();
        }

        [Fact]
        public void should_copy_the_uncommitted_headers_to_the_committed_stream_headers()
        {
            Stream.CommittedHeaders.Count.Should().Be(_headers.Count);
        }
    }

    /// <summary>
    ///     This behavior is primarily to support a NoSQL storage solution where CommitId is not being used as the "primary key"
    ///     in a NoSQL environment, we'll most likely use StreamId + CommitSequence, which also enables optimistic concurrency.
    /// </summary>
#if MSTEST
    [TestClass]
#endif
    public class when_committing_with_an_identifier_that_was_previously_read : on_the_event_stream
    {
        private ICommit[] _committed;
        private Guid _dupliateCommitId;
        private Exception _thrown;

        protected override Task Context()
        {
            _committed = new[] { BuildCommitStub(1, 1, 1) };
            _dupliateCommitId = _committed[0].CommitId;

            A.CallTo(() => Persistence.GetFromAsync(BucketId, StreamId, 0, int.MaxValue)).Returns(_committed);

            Stream = new OptimisticEventStream(BucketId, StreamId, Persistence, 0, int.MaxValue, CancellationToken.None);
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            _thrown = Catch.Exception(() => Stream.CommitChangesAsync(_dupliateCommitId, CancellationToken.None).GetAwaiter().GetResult());
            return Task.CompletedTask;
        }

        [Fact]
        public void should_throw_a_DuplicateCommitException()
        {
            _thrown.Should().BeOfType<DuplicateCommitException>();
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_committing_after_another_thread_or_process_has_moved_the_stream_head : on_the_event_stream
    {
        private const int StreamRevision = 1;
        private readonly EventMessage _uncommitted = new EventMessage { Body = string.Empty };
        private ICommit[] _committed;
        private ICommit[] _discoveredOnCommit;
        private CommitAttempt _constructed;
        private Exception _thrown;

        protected override Task Context()
        {
            _committed = new[] { BuildCommitStub(1, 1, 1) };
            _discoveredOnCommit = new[] { BuildCommitStub(3, 2, 2) };

            A.CallTo(() => Persistence.CommitAsync(A<CommitAttempt>._, A<CancellationToken>._)).Throws(new ConcurrencyException());
            A.CallTo(() => Persistence.GetFromAsync(BucketId, StreamId, StreamRevision, int.MaxValue)).Returns(_committed);
            A.CallTo(() => Persistence.GetFromAsync(BucketId, StreamId, StreamRevision + 1, int.MaxValue)).Returns(_discoveredOnCommit);

            Stream = new OptimisticEventStream(BucketId, StreamId, Persistence, StreamRevision, int.MaxValue, CancellationToken.None);
            Stream.Add(_uncommitted);
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            _thrown = Catch.Exception(() => Stream.CommitChangesAsync(Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult());
            return Task.CompletedTask;
        }

        [Fact]
        public void should_throw_a_ConcurrencyException()
        {
            _thrown.Should().BeOfType<ConcurrencyException>();
        }

        [Fact]
        public void should_query_the_underlying_storage_to_discover_the_new_commits()
        {
            A.CallTo(() => Persistence.GetFromAsync(BucketId, StreamId, StreamRevision + 1, int.MaxValue)).MustHaveHappened(Repeated.Exactly.Once);
        }

        [Fact]
        public void should_update_the_stream_revision_accordingly()
        {
            Stream.StreamRevision.Should().Be(_discoveredOnCommit[0].StreamRevision);
        }

        [Fact]
        public void should_update_the_commit_sequence_accordingly()
        {
            Stream.CommitSequence.Should().Be(_discoveredOnCommit[0].CommitSequence);
        }

        [Fact]
        public void should_add_the_newly_discovered_committed_events_to_the_set_of_committed_events_accordingly()
        {
            Stream.CommittedEvents.Count.Should().Be(_discoveredOnCommit[0].Events.Count + 1);
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_attempting_to_invoke_behavior_on_a_disposed_stream : on_the_event_stream
    {
        private Exception _thrown;

        protected override Task Context()
        {
            Stream.Dispose();
            return Task.CompletedTask;
        }

        protected override Task Because()
        {
            _thrown = Catch.Exception(() => Stream.CommitChangesAsync(Guid.NewGuid(), CancellationToken.None).GetAwaiter().GetResult());
            return Task.CompletedTask;
        }

        [Fact]
        public void should_throw_a_ObjectDisposedException()
        {
            _thrown.Should().BeOfType<ObjectDisposedException>();
        }
    }

#if MSTEST
    [TestClass]
#endif
    public class when_attempting_to_modify_the_event_collections : on_the_event_stream
    {
        [Fact]
        public void should_throw_an_exception_when_adding_to_the_committed_collection()
        {
            Catch.Exception(() => Stream.CommittedEvents.Add(null)).Should().BeOfType<NotSupportedException>();
        }

        [Fact]
        public void should_throw_an_exception_when_adding_to_the_uncommitted_collection()
        {
            Catch.Exception(() => Stream.UncommittedEvents.Add(null)).Should().BeOfType<NotSupportedException>();
        }

        [Fact]
        public void should_throw_an_exception_when_clearing_the_committed_collection()
        {
            Catch.Exception(() => Stream.CommittedEvents.Clear()).Should().BeOfType<NotSupportedException>();
        }

        [Fact]
        public void should_throw_an_exception_when_clearing_the_uncommitted_collection()
        {
            Catch.Exception(() => Stream.UncommittedEvents.Clear()).Should().BeOfType<NotSupportedException>();
        }

        [Fact]
        public void should_throw_an_exception_when_removing_from_the_committed_collection()
        {
            Catch.Exception(() => Stream.CommittedEvents.Remove(null)).Should().BeOfType<NotSupportedException>();
        }

        [Fact]
        public void should_throw_an_exception_when_removing_from_the_uncommitted_collection()
        {
            Catch.Exception(() => Stream.UncommittedEvents.Remove(null)).Should().BeOfType<NotSupportedException>();
        }
    }

    public abstract class on_the_event_stream : SpecificationBase
#if XUNIT
        , IUseFixture<FakeTimeFixture>
#endif
    {
        protected const int DefaultStreamRevision = 1;
        protected const int DefaultCommitSequence = 1;
        private ICommitEvents _persistence;
        private OptimisticEventStream _stream;
        protected const string BucketId = "bucket";
        protected readonly string StreamId = Guid.NewGuid().ToString();

#if MSTEST
        // todo: we have a problem with ClassInitialize and inheritance, they are not called
        // a possible workaround is to use the appdomain unload: https://vijayvepa.wordpress.com/2011/06/16/test-classinitialize-and-classcleanup-inheritance/
        // but each test is run in isolation in MSTest
        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            SystemTime.Resolver = () => new DateTime(2012, 1, 1, 13, 0, 0);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            SystemTime.Resolver = null;
        }

        public on_the_event_stream()
        {
            SystemTime.Resolver = () => new DateTime(2012, 1, 1, 13, 0, 0);
        }

        protected override Task Cleanup()
        {
            SystemTime.Resolver = null;
        }
#endif

#if NUNIT
        // can also consider using the NUnit test attrbutes instead of the constructor

        protected on_the_event_stream()
        {
            SystemTime.Resolver = () => new DateTime(2012, 1, 1, 13, 0, 0);
        }

        protected override Task Cleanup()
        {
            SystemTime.Resolver = null;
            return Task.CompletedTask;
        }

#endif

        protected ICommitEvents Persistence
        {
            get { return _persistence ?? (_persistence = A.Fake<ICommitEvents>()); }
        }

        protected OptimisticEventStream Stream
        {
            get { return _stream ?? (_stream = new OptimisticEventStream(BucketId, StreamId, Persistence)); }
            set { _stream = value; }
        }

        public void SetFixture(FakeTimeFixture data)
        { }

        protected ICommit BuildCommitStub(int revision, int sequence, int eventCount)
        {
            var events = new List<EventMessage>(eventCount);
            for (int i = 0; i < eventCount; i++)
            {
                events.Add(new EventMessage());
            }

            return new Commit(Bucket.Default, StreamId, revision, Guid.NewGuid(), sequence, SystemTime.UtcNow, 0, null, events);
        }
    }

    public class FakeTimeFixture : IDisposable
    {
        public FakeTimeFixture()
        {
            SystemTime.Resolver = () => new DateTime(2012, 1, 1, 13, 0, 0);
        }

        public void Dispose()
        {
            SystemTime.Resolver = null;
        }
    }
}

#pragma warning restore 169