//-----------------------------------------------------------------------
// <copyright file="IndexingExecuter.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Json;
using Raven.Database.Plugins;
using Raven.Database.Storage;

namespace Raven.Database.Indexing
{
	public class IndexingExecuter : AbstractIndexingExecuter
	{
		public IndexingExecuter(ITransactionalStorage transactionalStorage, WorkContext context, TaskScheduler scheduler)
			: base(transactionalStorage, context, scheduler)
		{
		}

		protected override bool IsIndexStale(IndexStats indexesStat, IStorageActionsAccessor actions)
		{
			return actions.Staleness.IsMapStale(indexesStat.Name);
		}

		protected override void FlushAllIndexes()
		{
			context.IndexStorage.FlushMapIndexes();
		}

		protected override IndexToWorkOn GetIndexToWorkOn(IndexStats indexesStat)
		{
			return new IndexToWorkOn
			{
				IndexName = indexesStat.Name,
				LastIndexedEtag = indexesStat.LastIndexedEtag
			};
		}


		protected override void ExecuteIndexingWorkOnMultipleThreads(IList<IndexToWorkOn> indexesToWorkOn)
		{
			ExecuteIndexingInternal(indexesToWorkOn, (results) => Parallel.ForEach(results, new ParallelOptions
			{
				MaxDegreeOfParallelism = context.Configuration.MaxNumberOfParallelIndexTasks,
				TaskScheduler = scheduler,
			}, result => transactionalStorage.Batch(actions => IndexDocuments(actions, result.Item1.IndexName, result.Item2))));
		}

		protected override void ExecuteIndexingWorkOnSingleThread(IList<IndexToWorkOn> indexesToWorkOn)
		{
			ExecuteIndexingInternal(indexesToWorkOn, results =>
			{
				foreach (var indexToWorkOn in results)
				{
					var index = indexToWorkOn.Item1;
					var docs = indexToWorkOn.Item2;
					transactionalStorage.Batch(
						actions => IndexDocuments(actions, index.IndexName, docs));
				}
			});
		}

		private void ExecuteIndexingInternal(IList<IndexToWorkOn> indexesToWorkOn, Action<IEnumerable<Tuple<IndexToWorkOn, IEnumerable<JsonDocument>>>> indexingOp)
		{
			var lastIndexedGuidForAllIndexes = indexesToWorkOn.Min(x => new ComparableByteArray(x.LastIndexedEtag.ToByteArray())).ToGuid();

			JsonDocument[] jsonDocs = null;
			try
			{
				transactionalStorage.Batch(actions =>
				{
					jsonDocs = actions.Documents.GetDocumentsAfter(lastIndexedGuidForAllIndexes)
						.Where(x => x != null)
						.Select(doc =>
						{
							DocumentRetriever.EnsureIdInMetadata(doc);
							return doc;
						})
						.Take(context.Configuration.MaxNumberOfItemsToIndexInSingleBatch) // ensure that we won't go overboard with reading and blow up with OOM
						.ToArray();
				});

				if (jsonDocs.Length > 0)
				{
					var result = FilterIndexes(indexesToWorkOn, jsonDocs).ToList();
					indexingOp(result);
				}
			}
			finally
			{
				if (jsonDocs != null && jsonDocs.Length > 0)
				{
					var last = jsonDocs.Last();
					
					Debug.Assert(last.Etag != null);
					Debug.Assert(last.LastModified != null);

					var lastEtag = last.Etag.Value;
					var lastModified = last.LastModified.Value;

					var lastIndexedEtag = new ComparableByteArray(lastEtag.ToByteArray());
					// whatever we succeeded in indexing or not, we have to update this
					// because otherwise we keep trying to re-index failed documents
					transactionalStorage.Batch(actions =>
					{
						foreach (var indexToWorkOn in indexesToWorkOn)
						{
							MarkIndexes(indexToWorkOn, lastIndexedEtag, actions, lastEtag, lastModified);
						}
					});
				}
			}
		}

		private void MarkIndexes(IndexToWorkOn indexToWorkOn, ComparableByteArray lastIndexedEtag, IStorageActionsAccessor actions, Guid lastEtag, DateTime lastModified)
		{
			if (new ComparableByteArray(indexToWorkOn.LastIndexedEtag.ToByteArray()).CompareTo(lastIndexedEtag) > 0)
				return;
			actions.Indexing.UpdateLastIndexed(indexToWorkOn.IndexName, lastEtag, lastModified);
		}

		private IEnumerable<Tuple<IndexToWorkOn, IEnumerable<JsonDocument>>> FilterIndexes(IEnumerable<IndexToWorkOn> indexesToWorkOn, JsonDocument[] jsonDocs)
		{
			var last = jsonDocs.Last();

			Debug.Assert(last.Etag != null);
			Debug.Assert(last.LastModified != null);

			var lastEtag = last.Etag.Value;
			var lastModified = last.LastModified.Value;

			var lastIndexedEtag = new ComparableByteArray(lastEtag.ToByteArray());

			foreach (var indexToWorkOn in indexesToWorkOn)
			{
				var indexLastInedexEtag = new ComparableByteArray(indexToWorkOn.LastIndexedEtag.ToByteArray());
				if (indexLastInedexEtag.CompareTo(lastIndexedEtag) >= 0) 
					continue;

				yield return Tuple.Create(indexToWorkOn, jsonDocs.Where(doc => indexLastInedexEtag.CompareTo(new ComparableByteArray(doc.Etag.Value.ToByteArray())) < 0));
				//actions.Indexing.UpdateLastIndexed(indexToWorkOn.IndexName, lastEtag, lastModified);
			}
		}

		protected override bool IsValidIndex(IndexStats indexesStat)
		{
			return true;
		}


		private void IndexDocuments(IStorageActionsAccessor actions, string index, IEnumerable<JsonDocument> jsonDocs)
		{
			var viewGenerator = context.IndexDefinitionStorage.GetViewGenerator(index);
			if (viewGenerator == null)
				return; // index was deleted, probably
			var jsonDocsArray = jsonDocs.ToArray();
			var dateTime = jsonDocsArray.Min(x => x.LastModified) ?? DateTime.MinValue;

			var documentRetriever = new DocumentRetriever(null, context.ReadTriggers);
			try
			{
				log.Debug("Indexing {0} documents for index: {1}", jsonDocsArray.Length, index);
				context.IndexStorage.Index(index, viewGenerator,
					jsonDocsArray
					.Select(doc => documentRetriever
						.ExecuteReadTriggers(doc, null, ReadOperation.Index))
					.Where(doc => doc != null)
					.Select(x => JsonToExpando.Convert(x.ToJson())), context, actions, dateTime);
			}
			catch (Exception e)
			{
				if (actions.IsWriteConflict(e))
					return;
				log.WarnException(
					string.Format("Failed to index documents for index: {0}", index),
					e);
			}
		}

	}
}