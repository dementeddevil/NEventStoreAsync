﻿using NEventStore.Logging;
using NEventStore.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace NEventStore.Persistence.AzureBlob
{
	/// <summary>
	/// Main engine for using Azure blob storage for event sourcing.
	/// The general pattern is that all commits for a given stream live within a single page blob.
	/// As new commits are added, they are placed at the end of the page blob.
	/// </summary>
	public class AzureBlobPersistenceEngine : IPersistStreams
	{
		private static readonly ILog Logger = LogFactory.BuildLogger( typeof( AzureBlobPersistenceEngine ) );

		public const string _headerMetadataKey = "header";
		private string _connectionString;
		private ISerialize _serializer;
		private AzureBlobPersistenceOptions _options;
		private CloudStorageAccount _storageAccount;
		private CloudBlobClient _blobClient;
		private CloudBlobContainer _blobContainer;
		private int _initialized;
		private bool _disposed;

		/// <summary>
		/// Create a new engine.
		/// </summary>
		/// <param name="connectionString">The Azure blob storage connection string.</param>
		/// <param name="serializer">The serializer to use.</param>
		/// <param name="options">Options for the Azure blob storage.</param>
		public AzureBlobPersistenceEngine( string connectionString, ISerialize serializer, AzureBlobPersistenceOptions options = null )
		{
			if ( String.IsNullOrEmpty( connectionString ) )
			{ throw new ArgumentException( "connectionString cannot be null or empty" ); }
			if ( serializer == null )
			{ throw new ArgumentNullException( "serializer" ); }
			if ( options == null )
			{ throw new ArgumentNullException( "options" ); }

			_connectionString = connectionString;
			_serializer = serializer;
			_options = options;
		}

		/// <summary>
		/// Is the engine disposed?
		/// </summary>
		public bool IsDisposed
		{ get { return _disposed; } }

		/// <summary>
		/// Connect to Azure storage and get a reference to the container object,
		/// creating it if it does not exist.
		/// </summary>
		public void Initialize()
		{
			if ( Interlocked.Increment( ref _initialized ) > 1 )
			{ return; }

			_storageAccount = CloudStorageAccount.Parse( _connectionString );
			_blobClient = _storageAccount.CreateCloudBlobClient();
			_blobContainer = _blobClient.GetContainerReference( GetContainerName() );
			_blobContainer.CreateIfNotExists();
		}

		/// <summary>
		/// Not Implemented.
		/// </summary>
		/// <param name="checkpointToken"></param>
		/// <returns></returns>
		public ICheckpoint GetCheckpoint(string checkpointToken = null)
		{ throw new NotImplementedException(); }

		/// <summary>
		/// Gets the list of commits from a given blobEntry, starting from a given date
		/// until the present.
		/// </summary>
		/// <param name="bucketId">The blobEntry id to pull commits from.</param>
		/// <param name="start">The starting date for commits.</param>
		/// <returns>The list of commits from the given blobEntry and greater than or equal to the start date.</returns>
		public IEnumerable<ICommit> GetFrom( string bucketId, DateTime start )
		{ return GetFromTo(bucketId, start, DateTime.MaxValue); }

		/// <summary>
		/// Not Implemented.
		/// </summary>
		/// <param name="checkpointToken"></param>
		/// <returns></returns>
		public IEnumerable<ICommit> GetFrom( string checkpointToken = null )
		{ throw new NotImplementedException("support for get from leveraging checkpoints is not yet implemented"); }

		/// <summary>
		/// Gets the list of commits from a given blobEntry, starting from a given date
		/// until the end date.
		/// </summary>
		/// <param name="bucketId">The blobEntry id to pull commits from.</param>
		/// <param name="start">The starting date for commits.</param>
		/// <param name="end">The ending date for commits.</param>
		/// <returns>The list of commits from the given blobEntry and greater than or equal to the start date and less than or equal to the end date.</returns>
		public IEnumerable<ICommit> GetFromTo( string bucketId, DateTime start, DateTime end )
		{
			int startPage = 0;
			int endPage = 0;
			int startIndex = 0;
			int numberOfCommits = 0;
			List<ICommit> commits = new List<ICommit>();

			// Get listing of all blobs.
			var blobs = _blobContainer
			.ListBlobs( GetContainerName() + "/" + bucketId, true, BlobListingDetails.Metadata )
			.OfType<CloudPageBlob>();

			foreach ( var pageBlob in blobs )
			{
				startPage = 0;
				endPage = 0;
				startIndex = 0;
				numberOfCommits = 0;

				var header = GetHeader( pageBlob );
				foreach (var commitDefinition in header.PageBlobCommitDefinitions)
				{
					if (start > commitDefinition.CommitStampUtc)
					{
						++startIndex;
						startPage += commitDefinition.TotalPagesUsed;
					}
					else if (end < commitDefinition.CommitStampUtc)
					{ break; }
					else
					{ ++numberOfCommits; }

					endPage += commitDefinition.TotalPagesUsed;
				}

				// download all the data
				var totalBytes = (endPage - startPage + 1) * 512;
				var byteContainer = new byte[totalBytes];
				using (var ms = new MemoryStream(totalBytes))
				{
					var offset = startPage * 512;
					pageBlob.DownloadRangeToStream( ms, offset, totalBytes );
					ms.Position = 0;

					// now walk it and make it so
					for (int i = startIndex; i != startIndex + numberOfCommits; ++i)
					{
						ms.Read(byteContainer, 0, header.PageBlobCommitDefinitions[i].DataSizeBytes);
						using (var ms2 = new MemoryStream(byteContainer, 0, header.PageBlobCommitDefinitions[i].DataSizeBytes, false))
						{
							var bucket = _serializer.Deserialize<AzureBlobEntry>(ms2);
							commits.Add(CreateCommitFromAzureBlobEntry(bucket));
						}

						var remainder = header.PageBlobCommitDefinitions[i].DataSizeBytes % 512;
						ms.Read(byteContainer, 0, 512 - remainder);
					}
				}
			} 
			return commits.OrderBy(c => c.CommitStamp);
		}

		/// <summary>
		/// Gets commits from a given blobEntry and stream id that fall within min and max revisions.
		/// </summary>
		/// <param name="bucketId">The blobEntry id to pull from.</param>
		/// <param name="streamId">The stream id.</param>
		/// <param name="minRevision">The minimum revision.</param>
		/// <param name="maxRevision">The maximum revision.</param>
		/// <returns></returns>
		public IEnumerable<ICommit> GetFrom(string bucketId, string streamId, int minRevision, int maxRevision)
		{
			var commits = new List<ICommit>();
			var pageBlobReference = _blobContainer.GetPageBlobReference( GetContainerName() + "/" + bucketId + "/" + streamId );
			try
			{
				pageBlobReference.FetchAttributes();
				var header = GetHeader( pageBlobReference );

				// find out how many pages we are reading
				int startPage = 0;
				int endPage = 0;
				int startIndex = 0;
				int numberOfCommits = 0;
				foreach ( var commitDefinition in header.PageBlobCommitDefinitions )
				{
					if ( minRevision > commitDefinition.Revision )
					{
						++startIndex;
						startPage += commitDefinition.TotalPagesUsed;
					}
					else if ( maxRevision < commitDefinition.Revision )
					{ break; }
					else
					{ ++numberOfCommits; }

					endPage += commitDefinition.TotalPagesUsed;
				}

				// download all the data
				var totalBytes = ( endPage - startPage + 1 ) * 512;
				var byteContainer = new byte[totalBytes];
				using ( var ms = new MemoryStream( totalBytes ) )
				{
					var offset = startPage * 512;
					pageBlobReference.DownloadRangeToStream( ms, offset, totalBytes );
					ms.Position = 0;

					// now walk it and make it so
					for ( int i = startIndex; i != startIndex + numberOfCommits; ++i )
					{
						ms.Read( byteContainer, 0, header.PageBlobCommitDefinitions[i].DataSizeBytes );
						using ( var ms2 = new MemoryStream( byteContainer, 0, header.PageBlobCommitDefinitions[i].DataSizeBytes, false ) )
						{
							var bucket = _serializer.Deserialize<AzureBlobEntry>( ms2 );
							commits.Add( CreateCommitFromAzureBlobEntry( bucket ) );
						}

						var remainder = header.PageBlobCommitDefinitions[i].DataSizeBytes % 512;
						ms.Read( byteContainer, 0, 512 - remainder );
					}
				}
			}
			catch ( Microsoft.WindowsAzure.Storage.StorageException ex )
			{
				if ( ex.Message.Contains( "404" ) )
				{ Logger.Warn( "tried to get from stream that does not exist, stream id:  ", streamId ); }
				else
				{ throw; }
			}

			return commits.OrderBy(c => c.StreamRevision);
		}

		/// <summary>
		/// Gets all undispatched commits across all buckets.
		/// </summary>
		/// <returns>A list of all undispatched commits.</returns>
		public IEnumerable<ICommit> GetUndispatchedCommits()
		{
			// this is most likely extremely ineficcient as the size of our store grows to 100's of millions of aggregates (possibly even just 1000's)
			var sw = new Stopwatch();
			sw.Start();
			var blobs = _blobContainer
							.ListBlobs(useFlatBlobListing: true, blobListingDetails: BlobListingDetails.Metadata)
							.OfType<CloudPageBlob>();

			List<ICommit> commits = new List<ICommit>();
			long containerSizeBytes = 0;

			foreach ( var blob in blobs )
			{
				var header = GetHeader( blob );

				if ( header.UndispatchedCommitCount > 0 )
				{
					var pageIndex = 0;
					containerSizeBytes += blob.Properties.Length;
					foreach ( var blobDefinition in header.PageBlobCommitDefinitions )
					{
						if ( !blobDefinition.IsDispatched )
						{
							var startIndexBytes = pageIndex * 512;
							var commitBytes = new byte[blobDefinition.DataSizeBytes];

							using ( var ms = new MemoryStream( blobDefinition.DataSizeBytes ) )
							{
								blob.DownloadRangeToStream( ms, startIndexBytes, blobDefinition.DataSizeBytes );
								ms.Position = 0;

								AzureBlobEntry bucket;
								try
								{ bucket = _serializer.Deserialize<AzureBlobEntry>( ms ); }
								catch ( Exception ex )
								{
									// we hope this does not happen
									var message = string.Format( "Blob with uri [{0}] is corrupt.", blob.Uri );
									throw new InvalidDataException( message, ex );
								}

								commits.Add( CreateCommitFromAzureBlobEntry( bucket ) );
							}
						}
						pageIndex += blobDefinition.TotalPagesUsed;
					}
				}
			}
			return commits;
		}

		/// <summary>
		/// Marks a stream Id's commit as dispatched.
		/// </summary>
		/// <param name="commit">The commit object to mark as dispatched.</param>
		public void MarkCommitAsDispatched( ICommit commit )
		{
			var pageBlobReference = _blobContainer.GetPageBlobReference(GetContainerName() + "/" + commit.BucketId + "/" + commit.StreamId);
			try
			{
				pageBlobReference.FetchAttributes();
				string eTag = pageBlobReference.Properties.ETag;
				var header = GetHeader( pageBlobReference );

				// we must commit at a page offset, we will just track how many pages in we must start writing at
				foreach ( var commitDefinition in header.PageBlobCommitDefinitions )
				{
					if ( commit.CommitId == commitDefinition.CommitId )
					{
						commitDefinition.IsDispatched = true;
						--header.UndispatchedCommitCount;
					}
				}
				pageBlobReference.Metadata[_headerMetadataKey] = Convert.ToBase64String( _serializer.Serialize( header ) );
				try
				{
					pageBlobReference.SetMetadata( AccessCondition.GenerateIfMatchCondition( eTag ) );
				}
				catch ( Microsoft.WindowsAzure.Storage.StorageException ex )
				{
					if ( ex.Message.Contains( "412" ) )
					{ throw new ConcurrencyException( "concurrency exception in markcommitasdispachted", ex ); }
					else
					{ throw; }
				}
			}
			catch ( Microsoft.WindowsAzure.Storage.StorageException ex )
			{
				if ( ex.Message.Contains( "404" ) )
				{ Logger.Warn("tried to mark as dispatched commit that does not exist, commit id: ", commit.CommitId); }
				else 
				{ throw; }
			}
		}

		/// <summary>
		/// Purge a container.
		/// </summary>
		public void Purge()
		{ _blobContainer.Delete(); }

		/// <summary>
		/// Not yet implemented.
		/// </summary>
		/// <param name="bucketId"></param>
		public void Purge( string bucketId )
		{
			throw new NotImplementedException();
		}

		/// <summary>
		/// Drop a container.
		/// </summary>
		public void Drop()
		{ _blobContainer.Delete(); }

		/// <summary>
		/// Deletes a stream by blobEntry and stream id.
		/// </summary>
		/// <param name="bucketId">The blobEntry id.</param>
		/// <param name="streamId">The stream id.</param>
		public void DeleteStream( string bucketId, string streamId )
		{
			var pageBlobReference = _blobContainer.GetPageBlobReference(GetContainerName() + "/" + bucketId + "/" + streamId);
			string leaseId = pageBlobReference.AcquireLease( new TimeSpan( 0, 0, 60 ), null );
			pageBlobReference.Delete(accessCondition: AccessCondition.GenerateLeaseCondition(leaseId));
			pageBlobReference.ReleaseLease( AccessCondition.GenerateLeaseCondition( leaseId ) );
		}

		/// <summary>
		/// Disposes this object.
		/// </summary>
		public void Dispose()
		{
			Dispose( true );
			GC.SuppressFinalize( this );
		}

		protected virtual void Dispose( bool disposing )
		{
			if ( !disposing || _disposed )
			{
				return;
			}

			Logger.Debug( "Disposing..." );
			_disposed = true;
		}

		/// <summary>
		/// Adds a commit to a stream.
		/// </summary>
		/// <param name="attempt">The commit attempt to be added.</param>
		/// <returns>An Commit if successful.</returns>
		public ICommit Commit( CommitAttempt attempt )
		{
			var pageBlobReference = _blobContainer.GetPageBlobReference(GetContainerName() + "/" + attempt.BucketId + "/" + attempt.StreamId);
			CreateIfNotExistsAndFetchAttributes(pageBlobReference);

			string leaseId = pageBlobReference.AcquireLease( new TimeSpan( 0, 0, 60 ), null );
			var header = GetHeader(pageBlobReference);

			// we must commit at a page offset, we will just track how many pages in we must start writing at
			var startPage = 0;
			foreach (var commit in header.PageBlobCommitDefinitions)
			{
				if (commit.CommitId == attempt.CommitId)
				{ throw new DuplicateCommitException("Duplicate Commit Attempt"); }

				startPage += commit.TotalPagesUsed;
			}

			var bucket = new AzureBlobEntry();
			bucket.BucketId = attempt.BucketId;
			bucket.CommitId = attempt.CommitId;
			bucket.CommitSequence = attempt.CommitSequence;
			bucket.CommitStampUtc = attempt.CommitStamp;
			bucket.Events = attempt.Events.ToList();
			bucket.Headers = attempt.Headers;
			bucket.StreamId = attempt.StreamId;
			bucket.StreamRevision = attempt.StreamRevision;
			var serializedBucket = _serializer.Serialize(bucket);

			var remainder = serializedBucket.Length % 512;
			var newBucket = new byte[serializedBucket.Length + (512-remainder)];
			Array.Copy(serializedBucket, newBucket, serializedBucket.Length);

			header.AppendPageBlobCommitDefinition(new PageBlobCommitDefinition(serializedBucket.Length, attempt.CommitId, attempt.StreamRevision, attempt.CommitStamp));
			++header.UndispatchedCommitCount;
			using (var ms = new MemoryStream(newBucket, false))
			{
				// if the header write fails, we will throw out.  the application will need to try again.  it will be as if
				// this commit never succeeded.  we need to also autogrow the page blob if we are going to exceed its max.
				var bytesRequired = startPage * 512 + ms.Length;
				if (pageBlobReference.Properties.Length < bytesRequired)
				{
					var currentSize = pageBlobReference.Properties.Length;
					var newSize = Math.Max((long)(currentSize * _options.BlobGrowthRatePercent), bytesRequired);
					var remainder2 = newSize % 512;
					newSize = newSize + 512 - remainder2;
					pageBlobReference.Resize(newSize, AccessCondition.GenerateLeaseCondition(leaseId));
				}

				pageBlobReference.WritePages(ms, startPage * 512, accessCondition: AccessCondition.GenerateLeaseCondition(leaseId));
				pageBlobReference.Metadata[_headerMetadataKey] = Convert.ToBase64String(_serializer.Serialize(header));
				pageBlobReference.SetMetadata(AccessCondition.GenerateLeaseCondition(leaseId));
			}

			pageBlobReference.ReleaseLease( AccessCondition.GenerateLeaseCondition( leaseId ) );

			return CreateCommitFromAzureBlobEntry( bucket );
		}

		/// <summary>
		/// Not yet implemented.  Returns null to ensure functionality.
		/// </summary>
		/// <param name="bucketId"></param>
		/// <param name="streamId"></param>
		/// <param name="maxRevision"></param>
		/// <returns></returns>
		public ISnapshot GetSnapshot( string bucketId, string streamId, int maxRevision )
		{
			return null;
		}

		/// <summary>
		/// Not yet implemented.
		/// </summary>
		/// <param name="snapshot"></param>
		/// <returns></returns>
		public bool AddSnapshot( ISnapshot snapshot )
		{
			throw new NotImplementedException();
		}

		/// <summary>
		/// Not yet implemented.
		/// </summary>
		/// <param name="bucketId"></param>
		/// <param name="maxThreshold"></param>
		/// <returns></returns>
		public IEnumerable<IStreamHead> GetStreamsToSnapshot( string bucketId, int maxThreshold )
		{
			throw new NotImplementedException();
		}

		#region private helpers

		/// <summary>
		/// Creates a Commit object from an AzureBlobEntry.
		/// </summary>
		/// <param name="blobEntry">The source AzureBlobEntry.</param>
		/// <returns>The populated Commit.</returns>
		private ICommit CreateCommitFromAzureBlobEntry(AzureBlobEntry blobEntry)
		{
			var commit = new Commit(blobEntry.BucketId,
									   blobEntry.StreamId,
									   blobEntry.StreamRevision,
									   blobEntry.CommitId,
									   blobEntry.CommitSequence,
									   blobEntry.CommitStampUtc,
									   "1",
									   blobEntry.Headers,
									   blobEntry.Events);
			return commit;
		}

		/// <summary>
		/// Gets the deserialized header from the blob.
		/// </summary>
		/// <param name="blob">The Blob.</param>
		/// <returns>A populated PageBlobHeader.</returns>
		private PageBlobHeader GetHeader(CloudPageBlob blob)
		{
			string serializedHeader;
			blob.Metadata.TryGetValue(_headerMetadataKey, out serializedHeader);

			var header = new PageBlobHeader();
			if (serializedHeader != null)
			{ header = _serializer.Deserialize<PageBlobHeader>(Convert.FromBase64String(serializedHeader)); }
			return header;
		}

		/// <summary>
		/// Build the container name.
		/// </summary>
		/// <returns>The container name.</returns>
		private string GetContainerName()
		{
			string containerSuffix = _options.ContainerType.ToString().ToLower();
			return _options.ContainerName.ToLower() + containerSuffix;
		}

		/// <summary>
		/// Tries to fetch a blob's attributes.  Creates the blob if it does not exist.
		/// </summary>
		/// <param name="blob">The blob.</param>
		private void CreateIfNotExistsAndFetchAttributes(CloudPageBlob blob)
		{
			try
			{
				blob.FetchAttributes();
			}
			catch (Microsoft.WindowsAzure.Storage.StorageException ex)
			{
				if (ex.Message.Contains("404"))
				{
					blob.Create(1024 * _options.DefaultStartingBlobSizeKb);
					blob.FetchAttributes();
				}
				else
				{ throw; }
			}
		}
		#endregion
	}
}