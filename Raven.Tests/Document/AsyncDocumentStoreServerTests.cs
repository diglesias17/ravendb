//-----------------------------------------------------------------------
// <copyright file="AsyncDocumentStoreServerTests.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions.Commands;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Client;
using Raven.Client.Connection.Async;
using Raven.Client.Extensions;
using Raven.Database.Server;
using Raven.Json.Linq;
using Xunit;

namespace Raven.Tests.Document
{
    public class AsyncEmbeddedDocumentStoreServerTests : AsyncDocumentStoreServerTests
    {
        private readonly RavenTest ravenTest;

        public AsyncEmbeddedDocumentStoreServerTests()
        {
            ravenTest = new RavenTest();
            DocumentStore = ravenTest.NewDocumentStore().Initialize();
        }

        public override void Dispose()
        {
            DocumentStore.Dispose();
            ravenTest.Dispose();
        }
    }

    public class AsyncRemoteDocumentStoreServerTests : AsyncDocumentStoreServerTests
    {
        private readonly string path;
        private readonly RemoteClientTest ravenTest;

        public AsyncRemoteDocumentStoreServerTests()
        {
            ravenTest = new RemoteClientTest();
            NonAdminHttp.EnsureCanListenToWhenInNonAdminContext(8079);
            DocumentStore = ravenTest.NewRemoteDocumentStore().Initialize();
        }

        public override void Dispose()
        {
            DocumentStore.Dispose();
            ravenTest.Dispose();
        }
    }

    public abstract class AsyncDocumentStoreServerTests : IDisposable
    {
        protected IDocumentStore DocumentStore { get; set; }

        public virtual void Dispose()
        {
        }

        [Fact]
        public void Can_insert_sync_and_get_async()
        {
            var entity = new Company {Name = "Async Company"};
            using (var session = DocumentStore.OpenSession())
            {
                session.Store(entity);
                session.SaveChanges();
            }

            using (var session = DocumentStore.OpenAsyncSession())
            {
                var task = session.LoadAsync<Company>(entity.Id);

                Assert.Equal("Async Company", task.Result.Name);
            }
        }

        [Fact]
        public void Can_insert_async_and_get_sync()
        {
            var entity = new Company {Name = "Async Company"};
            using (var session = DocumentStore.OpenAsyncSession())
            {
                session.Store(entity);
                session.SaveChangesAsync().Wait();
            }

            using (var session = DocumentStore.OpenSession())
            {
                var company = session.Load<Company>(entity.Id);

                Assert.Equal("Async Company", company.Name);
            }
        }

        [Fact]
        public void Can_insert_async_and_multi_get_async()
        {
            var entity1 = new Company {Name = "Async Company #1"};
            var entity2 = new Company {Name = "Async Company #2"};
            using (var session = DocumentStore.OpenAsyncSession())
            {
                session.Store(entity1);
                session.Store(entity2);
                session.SaveChangesAsync().Wait();
            }

            using (var session = DocumentStore.OpenAsyncSession())
            {
                var task = session.LoadAsync<Company>(new[] {entity1.Id, entity2.Id});
                Assert.Equal(entity1.Name, task.Result[0].Name);
                Assert.Equal(entity2.Name, task.Result[1].Name);
            }
        }

        [Fact]
        public void Can_defer_commands_until_savechanges_async()
        {
            using (var session = DocumentStore.OpenAsyncSession())
            {
                var commands = new ICommandData[]
                {
                    new PutCommandData
                    {
                        Document =
                            RavenJObject.FromObject(new Company {Name = "Hibernating Rhinos"}),
                        Etag = null,
                        Key = "rhino1",
                        Metadata = new RavenJObject(),
                    },
                    new PutCommandData
                    {
                        Document =
                            RavenJObject.FromObject(new Company {Name = "Hibernating Rhinos"}),
                        Etag = null,
                        Key = "rhino2",
                        Metadata = new RavenJObject(),
                    }
                };

                session.Advanced.Defer(commands);
                session.Advanced.Defer(new DeleteCommandData
                {
                    Etag = null,
                    Key = "rhino2"
                });

                Assert.Equal(0, session.Advanced.NumberOfRequests);

                session.SaveChangesAsync().Wait();
                Assert.Equal(1, session.Advanced.NumberOfRequests); // This returns 0 for some reason in async mode

                // Make sure that session is empty
                session.SaveChangesAsync().Wait();
                Assert.Equal(1, session.Advanced.NumberOfRequests);
            }

            Assert.Null(DocumentStore.DatabaseCommands.Get("rhino2"));
            Assert.NotNull(DocumentStore.DatabaseCommands.Get("rhino1"));
        }

        [Fact]
        public void Can_query_by_index()
        {
            var entity = new Company {Name = "Async Company #1", Id = "companies/1"};

            using (var session = DocumentStore.OpenAsyncSession())
            {
                session.Store(entity);
                session.SaveChangesAsync().Wait();
            }

            DocumentStore.AsyncDatabaseCommands
                         .PutIndexAsync("Test", new IndexDefinition
                         {
                             Map = "from doc in docs.Companies select new { doc.Name }"
                         }, true).Wait();

            Task<QueryResult> query;
            while (true)
            {
                query = DocumentStore.AsyncDatabaseCommands.QueryAsync("Test", new IndexQuery(), null);

                if (query.Result.IsStale == false)
                {
                    break;
                }

                Thread.Sleep(100);
            }
            Assert.NotEqual(0, query.Result.TotalResults);
        }

        [Fact]
        public void Can_project_value_from_collection()
        {
            using (var session = DocumentStore.OpenAsyncSession())
            {
                session.Store(new Company
                {
                    Name = "Project Value Company",
                    Contacts = new List<Contact>
                    {
                        new Contact {Surname = "Abbot"},
                        new Contact {Surname = "Costello"}
                    }
                });

                session.SaveChangesAsync().Wait();

                Task<QueryResult> query;
                while (true)
                {
                    query = DocumentStore.AsyncDatabaseCommands
                                         .QueryAsync("dynamic",
                                                     new IndexQuery
                                                     {
                                                         FieldsToFetch = new[] {"Contacts,Surname"}
                                                     },
                                                     new string[0]);

                    if (query.Result.IsStale == false)
                    {
                        break;
                    }

                    Thread.Sleep(100);
                }

                var ravenJToken = (RavenJArray) query.Result.Results[0]["Contacts"];
                Assert.Equal(2, ravenJToken.Count());
                Assert.Equal("Abbot", ravenJToken[0].Value<string>("Surname"));
                Assert.Equal("Costello", ravenJToken[1].Value<string>("Surname"));
            }
        }

        [Fact]
        public void CanInsertAsyncAndDeleteAsync()
        {
            var entity = new Company {Name = "Async Company #1", Id = "companies/1"};
            using (var session = DocumentStore.OpenAsyncSession())
            {
                session.Store(entity);
                session.SaveChangesAsync().Wait();
            }

            using (var for_loading = DocumentStore.OpenAsyncSession())
            {
	            var company = for_loading.LoadAsync<Company>(entity.Id).Result;
	            Assert.NotNull(company);
            }

	        using (var for_deleting = DocumentStore.OpenAsyncSession())
            {
				var e = for_deleting.LoadAsync<Company>(entity.Id).Result;
                for_deleting.Delete(e);
                for_deleting.SaveChangesAsync().Wait();
            }

            using (var for_verifying = DocumentStore.OpenAsyncSession())
            {
				var company = for_verifying.LoadAsync<Company>(entity.Id).Result;
				Assert.Null(company);
			}
		}

		[Fact]
		public void Can_patch_existing_document_when_present()
		{
			var company = new Company { Name = "Hibernating Rhinos" };

			using (var session = DocumentStore.OpenAsyncSession())
			{
				session.StoreAsync(company).Wait();
				session.SaveChangesAsync().Wait();
			}

			DocumentStore.AsyncDatabaseCommands.PatchAsync(
				company.Id,
				new[]
				{
					new PatchRequest
					{
						Type = PatchCommandType.Set,
						Name = "Name",
						Value = "Existing",
					}
				},
				new[]
				{
					new PatchRequest
					{
						Type = PatchCommandType.Set,
						Name = "Name",
						Value = "New",
					}
				},
				new RavenJObject()).Wait();

			using (var session = DocumentStore.OpenAsyncSession())
			{
				var company2 = session.LoadAsync<Company>(company.Id).Result;

				Assert.NotNull(company2);
				Assert.Equal(company2.Name, "Existing");
			}
		}

		[Fact]
		public void Can_patch_default_document_when_missing()
		{
			DocumentStore.AsyncDatabaseCommands.PatchAsync(
				"Company/1",
				new[]
				{
					new PatchRequest
					{
						Type = PatchCommandType.Set,
						Name = "Name",
						Value = "Existing",
					}
				},
				new[]
				{
					new PatchRequest
					{
						Type = PatchCommandType.Set,
						Name = "Name",
						Value = "New",
					}
				},
				new RavenJObject()).Wait();

			using (var session = DocumentStore.OpenAsyncSession())
			{
				var company = session.LoadAsync<Company>("Company/1").Result;

				Assert.NotNull(company);
				Assert.Equal(company.Name, "New");
			}
		}

		[Fact]
		public void Should_not_throw_when_ignore_missing_true()
		{
			Assert.DoesNotThrow(
				() => DocumentStore.AsyncDatabaseCommands.PatchAsync(
					"Company/1",
					new[]
					{
						new PatchRequest
						{
							Type = PatchCommandType.Set,
							Name = "Name",
							Value = "Existing",
						}
					}).Wait());

			Assert.DoesNotThrow(
				() => DocumentStore.AsyncDatabaseCommands.PatchAsync(
					"Company/1",
					new[]
					{
						new PatchRequest
						{
							Type = PatchCommandType.Set,
							Name = "Name",
							Value = "Existing",
						}
					}, true).Wait());
		}

		[Fact]
		public void Should_throw_when_ignore_missing_false()
		{
			Assert.Throws<DocumentDoesNotExistsException>(
				() =>
				{
					try
					{
						DocumentStore.AsyncDatabaseCommands.PatchAsync(
							"Company/1",
							new[]
							{
								new PatchRequest
								{
									Type = PatchCommandType.Set,
									Name = "Name",
									Value = "Existing",
								}
							}, false).Wait();
					}
					catch (AggregateException e)
					{
						throw e.ExtractSingleInnerException();
					}
				});
		}

		[Fact]
		public void Should_return_false_on_batch_delete_when_document_missing()
		{
			BatchResult[] batchResult = DocumentStore.AsyncDatabaseCommands.BatchAsync(new[] { new DeleteCommandData { Key = "Company/1" } }).Result;

			Assert.NotNull(batchResult);
			Assert.Equal(1, batchResult.Length);
			Assert.NotNull(batchResult[0].Deleted);
			Assert.False(batchResult[0].Deleted ?? true);
		}

		[Fact]
		public void Should_return_true_on_batch_delete_when_document_present()
		{
			DocumentStore.AsyncDatabaseCommands.PutAsync("Company/1", null, new RavenJObject(), new RavenJObject()).Wait();

			BatchResult[] batchResult = DocumentStore.AsyncDatabaseCommands.BatchAsync(new[] { new DeleteCommandData { Key = "Company/1" } }).Result;

			Assert.NotNull(batchResult);
			Assert.Equal(1, batchResult.Length);
			Assert.NotNull(batchResult[0].Deleted);
			Assert.True(batchResult[0].Deleted ?? false);
		}
	}
}