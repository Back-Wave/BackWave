using System.Collections.Immutable;

namespace BackWave.SourceGenerators.Tests;

public class JsonContextIndexTests
{
    [Fact]
    public void TypeThatTwoContextsListUnusably_ResolvesToTheOrdinalFirstContext_WhateverTheDiscoveryOrder()
    {
        // BW0013 names one context, so the pick must not depend on the order the generator discovers contexts.
        var index = new JsonContextIndex([
            Context("global::Acme.ZetaJson", new UnusableListing("global::Acme.Box", "zeta reason")),
            null,
            Context("global::Acme.AlphaJson", new UnusableListing("global::Acme.Box", "alpha reason")),
        ]);

        var resolution = index.Resolve("global::Acme.Box");

        Assert.Equal(JsonContextResolutionKind.Unusable, resolution.Kind);
        Assert.Equal("global::Acme.AlphaJson", resolution.ContextFqn);
        Assert.Equal("alpha reason", resolution.Reason);
        Assert.Empty(resolution.Contexts);
    }

    private static JsonContextInfo Context(string contextFqn, UnusableListing unusable)
        => new(contextFqn, ImmutableArray<string>.Empty, ImmutableArray.Create(unusable), SerializationOnlyReason: null);
}
