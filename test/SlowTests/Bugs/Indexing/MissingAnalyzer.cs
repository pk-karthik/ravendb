//-----------------------------------------------------------------------
// <copyright file="MissingAnalyzer.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using FastTests;
using Raven.Client.Exceptions;
using Raven.Client.Indexing;
using Xunit;

namespace SlowTests.Bugs.Indexing
{
    public class MissingAnalyzer : RavenTestBase
    {
        [Fact (Skip = "Missing feature: RavenDB-6153")]
        public void Should_give_clear_error_when_starting()
        {
            using (var store = GetDocumentStore())
            {
                var e = Assert.Throws<IndexCompilationException>(() => store.DatabaseCommands.PutIndex("foo",
                                                                                               new IndexDefinition
                                                                                               {
                                                                                                   Maps = { "from doc in docs select new { doc.Name }"},
                                                                                                   Fields =
                                                                                                   {
                                                                                                       {"Name" , new IndexFieldOptions {Analyzer = "foo bar"} }
                                                                                                   }

                                                                                               }));

                Assert.Equal("Cannot find analyzer type 'foo bar' for field: Name", e.Message);
            }
        }
    }
}