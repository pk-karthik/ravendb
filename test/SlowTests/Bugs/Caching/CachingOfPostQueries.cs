using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FastTests;
using Raven.Client;
using Raven.Client.Data;
using Raven.Client.Document.Async;
using Raven.Client.Indexes;
using Xunit;

namespace SlowTests.Bugs.Caching
{
    public class CachingOfPostQueries : RavenTestBase
    {
        private class Person
        {
            public string Name { get; set; }
            public int Age { get; set; }
        }

        private class PersonsIndex : AbstractIndexCreationTask<Person>
        {
            public PersonsIndex()
            {
                Map = results => from result in results
                                 select new Person
                                 {
                                     Name = result.Name
                                 };
            }
        }

        private void InitData(IDocumentStore store)
        {
            using (var session = store.OpenSession())
            {
                session.Store(new Person
                {
                    Name = "Johnny",
                    Age = 26
                });
                session.SaveChanges();
            }
        }

        private IDocumentStore GetTestStore()
        {
            var store = GetDocumentStore();

            new PersonsIndex().Execute(store);
            InitData(store);
            WaitForIndexing(store);
            return store;
        }

        [Fact]
        public void CachedGetQyuery()
        {
            using (var store = GetTestStore())
            {
                using (var session = store.OpenSession())
                {
                    var response = session.Query<Person, PersonsIndex>().FirstOrDefault(x => x.Name == "Johnny");
                    Assert.Equal(session.Advanced.NumberOfRequests, 1);
                    Assert.Equal(0, store.JsonRequestFactory.NumberOfCachedRequests);
                    response = session.Query<Person, PersonsIndex>().FirstOrDefault(x => x.Name == "Johnny");
                    Assert.Equal(session.Advanced.NumberOfRequests, 2);
                    Assert.Equal(1, store.JsonRequestFactory.NumberOfCachedRequests);
                }
            }
        }

        [Fact]
        public void CachedPostQyuery()
        {
            using (var store = GetTestStore())
            {
                var maxLengthOfGetRequest = store.Conventions.MaxLengthOfQueryUsingGetUrl;
                store.Conventions.MaxLengthOfQueryUsingGetUrl = 10;
                using (var session = store.OpenSession())
                {
                    var response = session.Query<Person, PersonsIndex>().FirstOrDefault(x => x.Name != "Jane" && x.Name != "Mika" && x.Name != "Michael" && x.Name != "Samuel");
                    Assert.Equal(session.Advanced.NumberOfRequests, 1);
                    Assert.Equal(0, store.JsonRequestFactory.NumberOfCachedRequests);
                    response = session.Query<Person, PersonsIndex>().FirstOrDefault(x => x.Name != "Jane" && x.Name != "Mika" && x.Name != "Michael" && x.Name != "Samuel");
                    Assert.Equal(session.Advanced.NumberOfRequests, 2);
                    Assert.Equal(1, store.JsonRequestFactory.NumberOfCachedRequests);
                }
                store.Conventions.MaxLengthOfQueryUsingGetUrl = maxLengthOfGetRequest;
            }
        }

        [Fact]
        public void CachedFacetsGetRequest()
        {
            using (var store = GetTestStore())
            {
                using (var session = store.OpenSession())
                {
                    var response = session.Query<Person, PersonsIndex>().Where(x => x.Name == "Johnny").ToFacets(new[]
                    {
                        new Facet()
                        {
                            Name = "Age"
                        }
                    });
                    Assert.Equal(0, store.JsonRequestFactory.NumberOfCachedRequests);
                    response = session.Query<Person, PersonsIndex>().Where(x => x.Name == "Johnny").ToFacets(new[]
                    {
                        new Facet()
                        {
                            Name = "Age"
                        }
                    });
                    Assert.Equal(1, store.JsonRequestFactory.NumberOfCachedRequests);
                }
            }
        }
        [Fact]
        public void CachedFacetsPostRequest()
        {
            using (var store = GetTestStore())
            {
                using (var session = store.OpenSession())
                {
                    var response = session.Query<Person, PersonsIndex>().Where(x => x.Name == "Johnny").ToFacets(Enumerable.Repeat(1, 200).Select(x => new Facet()
                    {
                        Name = "Age"
                    }));
                    Assert.Equal(0, store.JsonRequestFactory.NumberOfCachedRequests);
                    response = session.Query<Person, PersonsIndex>().Where(x => x.Name == "Johnny").ToFacets(Enumerable.Repeat(1, 200).Select(x => new Facet()
                    {
                        Name = "Age"
                    }));
                    Assert.Equal(1, store.JsonRequestFactory.NumberOfCachedRequests);
                }
            }
        }

        [Fact]
        public async Task CachedMultiFacetsRequest()
        {
            using (var store = GetTestStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await ((AsyncDocumentSession)session).AsyncDatabaseCommands.GetMultiFacetsAsync(new[]
                    {
                        new FacetQuery()
                        {
                            Query = "Name:Johnny",
                            IndexName = "PersonsIndex",
                            Start = 0,
                            PageSize = 16,
                            Facets = new List<Facet>()
                            {
                                new Facet()
                                {
                                    Name = "Age"
                                }
                            }
                        }
                    }).ConfigureAwait(false);
                    Assert.Equal(0, store.JsonRequestFactory.NumberOfCachedRequests);
                    await ((AsyncDocumentSession)session).AsyncDatabaseCommands.GetMultiFacetsAsync(new FacetQuery[]
                    {
                        new FacetQuery()
                        {
                            Query = "Name:Johnny",
                            IndexName = "PersonsIndex",
                            Start = 0,
                            PageSize = 16,
                            Facets = new List<Facet>()
                            {
                                new Facet()
                                {
                                    Name = "Age"
                                }
                            }
                        }
                    }).ConfigureAwait(false);
                    Assert.Equal(1, store.JsonRequestFactory.NumberOfCachedRequests);
                }
            }
        }


    }
}