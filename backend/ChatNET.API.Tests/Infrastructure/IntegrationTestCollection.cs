namespace ChatNET.API.Tests.Infrastructure;

// A collection fixture is created once and shared by every test class in the collection.
// Tests within one collection run one after another, which is what lets them share the same
// containers. Marking a test class with [Collection(IntegrationTestCollection.Name)] opts it in.
[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<ChatApiFactory>
{
    public const string Name = "Integration";
}
