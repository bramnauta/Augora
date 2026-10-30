using Augora.Contracts;
using Augora.Core.Media;

namespace Augora.Tests.Core;

public sealed class MediaItemRefTests
{
    [Fact]
    public void Round_trips_ids_that_contain_colons_and_query_strings()
    {
        var original = new MediaItemRef("tunein", MediaType.Folder, "Browse.ashx?id=c57943&filter=s:popular");

        var parsed = MediaItemRef.Parse(original.ToString());

        Assert.Equal(original, parsed);
        Assert.Equal("tunein:folder:Browse.ashx?id=c57943&filter=s:popular", original.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tunein")]
    [InlineData("tunein:radio")]
    [InlineData(":radio:s1")]
    [InlineData("tunein:unknown:s1")]
    public void Rejects_invalid_values(string? value) => Assert.False(MediaItemRef.TryParse(value, out _));

    [Fact]
    public void Allows_an_empty_id_for_provider_roots() =>
        Assert.Equal(new MediaItemRef("tunein", MediaType.Folder, ""), MediaItemRef.Parse("tunein:folder:"));
}
