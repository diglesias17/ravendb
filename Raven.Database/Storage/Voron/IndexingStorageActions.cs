﻿// -----------------------------------------------------------------------
//  <copyright file="IndexingStorageActions.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
namespace Raven.Database.Storage.Voron
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.IO;
	using System.Linq;

	using Raven.Abstractions;
	using Raven.Abstractions.Data;
	using Raven.Abstractions.Exceptions;
	using Raven.Abstractions.Extensions;
	using Raven.Database.Indexing;
	using Raven.Database.Storage.Voron.Impl;
	using Raven.Json.Linq;

	using global::Voron;
	using global::Voron.Impl;

	public class IndexingStorageActions : IIndexingStorageActions
	{
		private readonly TableStorage tableStorage;

		private readonly SnapshotReader snapshot;

		private readonly WriteBatch writeBatch;

		public IndexingStorageActions(TableStorage tableStorage, SnapshotReader snapshot, WriteBatch writeBatch)
		{
			this.tableStorage = tableStorage;
			this.snapshot = snapshot;
			this.writeBatch = writeBatch;
		}

		public void Dispose()
		{
		}

		public IEnumerable<IndexStats> GetIndexesStats()
		{
			using (var indexingStatsIterator = tableStorage.IndexingStats.Iterate(snapshot))
			using (var lastIndexedEtagIterator = tableStorage.LastIndexedEtags.Iterate(snapshot))
			{
				if (!indexingStatsIterator.Seek(Slice.BeforeAllKeys))
					yield break;

				lastIndexedEtagIterator.Seek(Slice.BeforeAllKeys);

				do
				{
					var indexStats = indexingStatsIterator
						.CreateStreamForCurrent()
						.ToJObject();

					lastIndexedEtagIterator.Seek(indexingStatsIterator.CurrentKey);

					var lastIndexedEtags = lastIndexedEtagIterator
						.CreateStreamForCurrent()
						.ToJObject();

					yield return GetIndexStats(indexStats, lastIndexedEtags);
				}
				while (indexingStatsIterator.MoveNext());
			}
		}

		public IndexStats GetIndexStats(string name)
		{
			ushort indexStatsVersion;
			ushort lastIndexedEtagsVersion;

			var indexStats = Load(tableStorage.IndexingStats, name, out indexStatsVersion);
			var lastIndexedEtags = Load(tableStorage.LastIndexedEtags, name, out lastIndexedEtagsVersion);

			return GetIndexStats(indexStats, lastIndexedEtags);
		}

		public void AddIndex(string name, bool createMapReduce)
		{
			if (tableStorage.IndexingStats.Contains(snapshot, name))
				throw new ArgumentException(string.Format("There is already an index with the name: '{0}'", name));

			tableStorage.IndexingStats.Add(
				writeBatch,
				name,
				new RavenJObject
				{
					{ "index", name },
					{ "attempts", 0 },
					{ "successes", 0 },
					{ "failures", 0 },
					{ "priority", 1 },
					{ "touches", 0 },
					{ "createdTimestamp", SystemTime.UtcNow },
					{ "lastIndexingTime", SystemTime.UtcNow },
					{ "reduce_attempts", createMapReduce ? 0 : (RavenJToken)RavenJValue.Null },
					{ "reduce_successes", createMapReduce ? 0 : (RavenJToken)RavenJValue.Null },
					{ "reduce_failures", createMapReduce ? 0 : (RavenJToken)RavenJValue.Null },
					{ "lastReducedEtag", createMapReduce ? Guid.Empty.ToByteArray() : (RavenJToken)RavenJValue.Null },
					{ "lastReducedTimestamp", createMapReduce ? DateTime.MinValue : (RavenJToken)RavenJValue.Null }
				});

			tableStorage.LastIndexedEtags.Add(
				writeBatch,
				name,
				new RavenJObject
				{
					{ "index", name },
					{ "lastEtag", Etag.Empty.ToByteArray() },
					{ "lastTimestamp", DateTime.MinValue },
				});
		}

		public void DeleteIndex(string name)
		{
			tableStorage.IndexingStats.Delete(writeBatch, name);
			tableStorage.LastIndexedEtags.Delete(writeBatch, name);
		}

		public void SetIndexPriority(string name, IndexingPriority priority)
		{
			ushort version;
			var index = Load(tableStorage.IndexingStats, name, out version);

			index["priority"] = (int)priority;

			tableStorage.IndexingStats.AddOrUpdate(writeBatch, name, index, version);
		}

		public IndexFailureInformation GetFailureRate(string name)
		{
			ushort version;
			var index = Load(tableStorage.IndexingStats, name, out version);

			var indexFailureInformation = new IndexFailureInformation
			{
				Attempts = index.Value<int>("attempts"),
				Errors = index.Value<int>("failures"),
				Successes = index.Value<int>("successes"),
				ReduceAttempts = index.Value<int?>("reduce_attempts"),
				ReduceErrors = index.Value<int?>("reduce_failures"),
				ReduceSuccesses = index.Value<int?>("reduce_successes"),
				Name = index.Value<string>("index"),
			};

			return indexFailureInformation;
		}

		public void UpdateLastIndexed(string name, Etag etag, DateTime timestamp)
		{
			ushort version;
			var index = Load(tableStorage.LastIndexedEtags, name, out version);

			if (Buffers.Compare(index.Value<byte[]>("lastEtag"), etag.ToByteArray()) >= 0)
				return;

			index["lastEtag"] = etag.ToByteArray();
			index["lastTimestamp"] = timestamp;

			tableStorage.LastIndexedEtags.AddOrUpdate(writeBatch, name, index, version);
		}

		public void UpdateLastReduced(string name, Etag etag, DateTime timestamp)
		{
			ushort version;
			var index = Load(tableStorage.IndexingStats, name, out version);

			if (Buffers.Compare(index.Value<byte[]>("lastReducedEtag"), etag.ToByteArray()) >= 0)
				return;

			index["lastReducedEtag"] = etag.ToByteArray();
			index["lastReducedTimestamp"] = timestamp;

			tableStorage.IndexingStats.AddOrUpdate(writeBatch, name, index, version);
		}

		public void TouchIndexEtag(string name)
		{
			ushort version;
			var index = Load(tableStorage.IndexingStats, name, out version);

			index["touches"] = index.Value<int>("touches") + 1;

			tableStorage.IndexingStats.AddOrUpdate(writeBatch, name, index, version);
		}

		public void UpdateIndexingStats(string name, IndexingWorkStats stats)
		{
			ushort version;
			var index = Load(tableStorage.IndexingStats, name, out version);

			index["attempts"] = index.Value<int>("attempts") + stats.IndexingAttempts;
			index["successes"] = index.Value<int>("successes") + stats.IndexingSuccesses;
			index["failures"] = index.Value<int>("failures") + stats.IndexingErrors;
			index["lastIndexingTime"] = SystemTime.UtcNow;

			tableStorage.IndexingStats.AddOrUpdate(writeBatch, name, index, version);
		}

		public void UpdateReduceStats(string name, IndexingWorkStats stats)
		{
			ushort version;
			var index = Load(tableStorage.IndexingStats, name, out version);

			index["reduce_attempts"] = index.Value<int>("reduce_attempts") + stats.ReduceAttempts;
			index["reduce_successes"] = index.Value<int>("reduce_successes") + stats.ReduceSuccesses;
			index["reduce_failures"] = index.Value<int>("reduce_failures") + stats.ReduceErrors;

			tableStorage.IndexingStats.AddOrUpdate(writeBatch, name, index, version);
		}

		public void RemoveAllDocumentReferencesFrom(string key)
		{
			RemoveDocumentReference(key);
		}

		public void UpdateDocumentReferences(string view, string key, HashSet<string> references)
		{
			var documentReferencesByKey = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByKey);
			var documentReferencesByRef = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByRef);
			var documentReferencesByView = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByView);
			var documentReferencesByViewAndKey = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByViewAndKey);

			using (var iterator = documentReferencesByViewAndKey.MultiRead(snapshot, view + "/" + key))
			{
				if (iterator.Seek(Slice.BeforeAllKeys))
				{
					do
					{
						RemoveDocumentReference(iterator.CurrentKey);
					}
					while (iterator.MoveNext());
				}
			}

			foreach (var reference in references)
			{
				var newKey = Guid.NewGuid().ToString();
				var value = new RavenJObject
				            {
					            { "view", view }, 
								{ "key", key }, 
								{ "ref", reference }
				            };

				tableStorage.DocumentReferences.Add(writeBatch, newKey, value);
				documentReferencesByKey.MultiAdd(writeBatch, key, newKey);
				documentReferencesByRef.MultiAdd(writeBatch, reference, newKey);
				documentReferencesByView.MultiAdd(writeBatch, view, newKey);
				documentReferencesByViewAndKey.MultiAdd(writeBatch, view + "/" + key, newKey);
			}
		}

		public IEnumerable<string> GetDocumentsReferencing(string reference)
		{
			var documentReferencesByRef = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByRef);

			using (var iterator = documentReferencesByRef.MultiRead(snapshot, reference))
			{
				var result = new List<string>();

				if (!iterator.Seek(Slice.BeforeAllKeys))
					return result;

				do
				{
					var key = iterator.CurrentKey;
					using (var read = tableStorage.DocumentReferences.Read(snapshot, key))
					{
						var value = read.Stream.ToJObject();
						result.Add(value.Value<string>("key"));
					}
				}
				while (iterator.MoveNext());

				return result.Distinct(StringComparer.OrdinalIgnoreCase);
			}
		}

		public int GetCountOfDocumentsReferencing(string reference)
		{
			var documentReferencesByRef = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByRef);

			using (var iterator = documentReferencesByRef.MultiRead(snapshot, reference))
			{
				var count = 0;

				if (!iterator.Seek(Slice.BeforeAllKeys)) return
					count;

				do
				{
					count++;
				}
				while (iterator.MoveNext());

				return count;
			}
		}

		public IEnumerable<string> GetDocumentsReferencesFrom(string key)
		{
			var documentReferencesByKey = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByKey);

			using (var iterator = documentReferencesByKey.MultiRead(snapshot, key))
			{
				var result = new List<string>();

				if (!iterator.Seek(Slice.BeforeAllKeys))
					return result;

				do
				{
					using (var read = tableStorage.DocumentReferences.Read(snapshot, iterator.CurrentKey))
					{
						var value = read.Stream.ToJObject();
						result.Add(value.Value<string>("ref"));
					}
				}
				while (iterator.MoveNext());

				return result.Distinct(StringComparer.OrdinalIgnoreCase);
			}
		}

		private RavenJObject Load(Table table, string name, out ushort version)
		{
			using (var read = table.Read(snapshot, name))
			{
				if (read == null) throw new IndexDoesNotExistsException(string.Format("There is no index with the name: '{0}'", name));

				version = read.Version;
				return read.Stream.ToJObject();
			}
		}

		private static IndexStats GetIndexStats(RavenJToken indexingStats, RavenJToken lastIndexedEtags)
		{
			return new IndexStats
			{
				TouchCount = indexingStats.Value<int>("touches"),
				IndexingAttempts = indexingStats.Value<int>("attempts"),
				IndexingErrors = indexingStats.Value<int>("failures"),
				IndexingSuccesses = indexingStats.Value<int>("successes"),
				ReduceIndexingAttempts = indexingStats.Value<int?>("reduce_attempts"),
				ReduceIndexingErrors = indexingStats.Value<int?>("reduce_failures"),
				ReduceIndexingSuccesses = indexingStats.Value<int?>("reduce_successes"),
				Name = indexingStats.Value<string>("index"),
				Priority = (IndexingPriority)indexingStats.Value<int>("priority"),
				LastIndexedEtag = Etag.Parse(lastIndexedEtags.Value<byte[]>("lastEtag")),
				LastIndexedTimestamp = lastIndexedEtags.Value<DateTime>("lastTimestamp"),
				CreatedTimestamp = indexingStats.Value<DateTime>("createdTimestamp"),
				LastIndexingTime = indexingStats.Value<DateTime>("lastIndexingTime"),
				LastReducedEtag =
					indexingStats.Value<byte[]>("lastReducedEtag") != null
						? Etag.Parse(indexingStats.Value<byte[]>("lastReducedEtag"))
						: null,
				LastReducedTimestamp = indexingStats.Value<DateTime?>("lastReducedTimestamp")
			};
		}

		private void RemoveDocumentReference(Slice key)
		{
			var documentReferencesByKey = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByKey);
			var documentReferencesByRef = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByRef);
			var documentReferencesByView = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByView);
			var documentReferencesByViewAndKey = tableStorage.DocumentReferences.GetIndex(Tables.DocumentReferences.Indices.ByViewAndKey);

			using (var iterator = documentReferencesByKey.MultiRead(snapshot, key))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return;

				do
				{
					var currentKey = iterator.CurrentKey;

					using (var read = tableStorage.DocumentReferences.Read(snapshot, currentKey))
					{
						var value = read.Stream.ToJObject();

						Debug.Assert(value.Value<string>("key") == key.ToString());
						var reference = value.Value<string>("ref");
						var view = value.Value<string>("view");

						tableStorage.DocumentReferences.Delete(writeBatch, currentKey);
						documentReferencesByKey.MultiDelete(writeBatch, key, currentKey);
						documentReferencesByRef.MultiDelete(writeBatch, reference, currentKey);
						documentReferencesByView.MultiDelete(writeBatch, view, currentKey);
						documentReferencesByViewAndKey.MultiDelete(writeBatch, view + "/" + key, currentKey);
					}
				}
				while (iterator.MoveNext());
			}
		}
	}
}