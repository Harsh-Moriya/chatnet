using System.Text.RegularExpressions;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Tests.Conversations;

// DirectKey is a pure function (no database, no I/O), so these are plain unit tests that
// run in milliseconds. The property that matters is symmetry: both participants of a
// direct chat must compute the same key, otherwise the unique index could not prevent
// duplicate conversations.
public class DirectKeyTests
{
    private static readonly Guid Low = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Mid = Guid.Parse("7a3f9c10-2b4d-4e8a-9f61-0c5d8e2b7a44");
    private static readonly Guid High = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    [Fact]
    public void Smaller_id_comes_first_whichever_way_the_arguments_are_given()
    {
        var expected = $"{Low}:{High}";

        Assert.Equal(expected, DirectKey.For(Low, High));
        Assert.Equal(expected, DirectKey.For(High, Low));
    }

    // A [Theory] runs the same test body once per [InlineData] row; a [Fact] has no inputs.
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000002")]
    [InlineData("7a3f9c10-2b4d-4e8a-9f61-0c5d8e2b7a44", "7a3f9c10-2b4d-4e8a-9f61-0c5d8e2b7a45")]
    [InlineData("0fffffff-ffff-ffff-ffff-ffffffffffff", "f0000000-0000-0000-0000-000000000000")]
    [InlineData("abcdef01-0000-0000-0000-000000000000", "ABCDEF01-0000-0000-0000-000000000001")]
    public void Key_is_symmetric(string first, string second)
    {
        var a = Guid.Parse(first);
        var b = Guid.Parse(second);

        Assert.Equal(DirectKey.For(a, b), DirectKey.For(b, a));
    }

    [Fact]
    public void Key_is_symmetric_for_many_random_pairs()
    {
        for (var i = 0; i < 500; i++)
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();

            Assert.True(
                DirectKey.For(a, b) == DirectKey.For(b, a),
                $"Asymmetric key for {a} and {b}");
        }
    }

    [Fact]
    public void Different_pairs_produce_different_keys()
    {
        var keys = new[]
        {
            DirectKey.For(Low, Mid),
            DirectKey.For(Low, High),
            DirectKey.For(Mid, High),
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Key_is_lowercase_hyphenated_ids_joined_by_a_colon()
    {
        var key = DirectKey.For(High, Mid);

        const string guid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
        Assert.Matches(new Regex($"^{guid}:{guid}$"), key);
    }

    [Fact]
    public void Key_is_the_same_on_every_call()
    {
        Assert.Equal(DirectKey.For(Low, Mid), DirectKey.For(Low, Mid));
    }
}
