﻿namespace Raven.Database.Storage.Voron.StorageActions
{
	using System;

	using Raven.Abstractions.Data;
	using Raven.Abstractions.Exceptions;
	using Raven.Abstractions.Extensions;
	using Raven.Database.Storage.Voron.Impl;

	using global::Voron;
	using global::Voron.Impl;

	public class StalenessStorageActions : StorageActionsBase, IStalenessStorageActions
	{
		private readonly TableStorage tableStorage;

		public StalenessStorageActions(TableStorage tableStorage, SnapshotReader snapshot)
			: base(snapshot)
		{
			this.tableStorage = tableStorage;
		}

		public bool IsIndexStale(string name, DateTime? cutOff, Etag cutoffEtag)
		{
			ushort version;
			var indexingStats = LoadJson(tableStorage.IndexingStats, name, out version);
			if (indexingStats == null)
				return false; // index does not exists

			var lastIndexedEtags = LoadJson(tableStorage.LastIndexedEtags, name, out version);

			if (IsMapStale(name) || IsReduceStale(name))
			{
				if (cutOff != null)
				{
					var lastIndexedTime = lastIndexedEtags.Value<DateTime>("lastTimestamp");
					if (cutOff.Value >= lastIndexedTime)
						return true;

					var lastReducedTime = lastIndexedEtags.Value<DateTime?>("lastReducedTimestamp");
					if (lastReducedTime != null && cutOff.Value >= lastReducedTime.Value)
						return true;
				}
				else if (cutoffEtag != null)
				{
					var lastIndexedEtag = lastIndexedEtags.Value<byte[]>("lastEtag");

					if (Buffers.Compare(lastIndexedEtag, cutoffEtag.ToByteArray()) < 0)
						return true;
				}
				else
				{
					return true;
				}
			}

			var tasksByIndex = tableStorage.Tasks.GetIndex(Tables.Tasks.Indices.ByIndex);
			using (var iterator = tasksByIndex.MultiRead(Snapshot, name))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return false;

				if (cutOff == null)
					return true;

				do
				{
					var value = LoadJson(tableStorage.Tasks, iterator.CurrentKey, out version);
					var time = value.Value<DateTime>("time");

					if (time <= cutOff.Value)
						return true;
				}
				while (iterator.MoveNext());
			}

			return false;
		}

		public bool IsReduceStale(string view)
		{
			var scheduledReductionsByView = tableStorage.ScheduledReductions.GetIndex(Tables.ScheduledReductions.Indices.ByView);
			using (var iterator = scheduledReductionsByView.MultiRead(Snapshot, view))
			{
				if (!iterator.Seek(Slice.BeforeAllKeys))
					return false;

				return true;
			}
		}

		public bool IsMapStale(string name)
		{
			ushort version;
			var read = LoadJson(tableStorage.LastIndexedEtags, name, out version);
			if (read == null)
				return false;

			var lastIndexedEtag = Etag.Parse(read.Value<byte[]>("lastEtag"));
			var lastDocumentEtag = GetMostRecentDocumentEtag();

			return lastDocumentEtag.CompareTo(lastIndexedEtag) > 0;
		}

		public Tuple<DateTime, Etag> IndexLastUpdatedAt(string name)
		{
			ushort version;
			var indexingStats = LoadJson(tableStorage.IndexingStats, name, out version);
			if (indexingStats == null)
				throw new IndexDoesNotExistsException("Could not find index named: " + name);

			var lastIndexedEtags = LoadJson(tableStorage.LastIndexedEtags, name, out version);
			if (lastIndexedEtags.Value<object>("lastReducedTimestamp") != null)
			{
				return Tuple.Create(
					lastIndexedEtags.Value<DateTime>("lastReducedTimestamp"),
					Etag.Parse(lastIndexedEtags.Value<byte[]>("lastReducedEtag")));
			}

			return Tuple.Create(lastIndexedEtags.Value<DateTime>("lastTimestamp"),
				Etag.Parse(lastIndexedEtags.Value<byte[]>("lastEtag")));
		}

		public Etag GetMostRecentDocumentEtag()
		{
			var documentsByEtag = tableStorage.Documents.GetIndex(Tables.Documents.Indices.KeyByEtag);
			using (var iterator = documentsByEtag.Iterate(Snapshot))
			{
				if (!iterator.Seek(Slice.AfterAllKeys))
					return Etag.Empty;

				return Etag.Parse(iterator.CurrentKey.ToString());
			}
		}

		public Etag GetMostRecentAttachmentEtag()
		{
			var attachmentsByEtag = tableStorage.Attachments.GetIndex(Tables.Attachments.Indices.ByEtag);
			using (var iterator = attachmentsByEtag.Iterate(Snapshot))
			{
				if (!iterator.Seek(Slice.AfterAllKeys))
					return Etag.Empty;

				return Etag.Parse(iterator.CurrentKey.ToString());
			}
		}

		public int GetIndexTouchCount(string name)
		{
			ushort version;
			var indexingStats = LoadJson(tableStorage.IndexingStats, name, out version);

			if (indexingStats == null)
				throw new IndexDoesNotExistsException("Could not find index named: " + name);

			return indexingStats.Value<int>("touches");
		}
	}
}