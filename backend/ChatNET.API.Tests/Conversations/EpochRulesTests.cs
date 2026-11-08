using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.KeyEpochs;

namespace ChatNET.API.Tests.Conversations;

// The rule that decides whether an epoch is complete, on its own: no database, no request.
public class EpochRulesTests
{
    private static readonly Guid Ana = Guid.Parse("00000000-0000-4000-8000-00000000000a");
    private static readonly Guid Ben = Guid.Parse("00000000-0000-4000-8000-00000000000b");
    private static readonly Guid Cy = Guid.Parse("00000000-0000-4000-8000-00000000000c");

    private static EpochSubmission Submission(int epoch, params (Guid UserId, int Generation)[] sealedFor) => new(
        epoch,
        new byte[32],
        new byte[64],
        sealedFor.Select(e => new EnvelopeSubmission(e.UserId, e.Generation, new byte[94])).ToList());

    private static Dictionary<Guid, int> Generations(params (Guid UserId, int Generation)[] keys) =>
        keys.ToDictionary(k => k.UserId, k => k.Generation);

    private static string? CodeOf(Action action) =>
        Assert.Throws<ConflictException>(action).Code;

    [Fact]
    public void The_next_epoch_for_exactly_the_members_at_their_current_keys_is_accepted()
    {
        EpochRules.Check(
            3, [Ana, Ben], Generations((Ana, 1), (Ben, 2)),
            Submission(4, (Ben, 2), (Ana, 1)));
    }

    [Fact]
    public void The_first_epoch_follows_epoch_zero()
    {
        EpochRules.Check(0, [Ana], Generations((Ana, 1)), Submission(1, (Ana, 1)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(1)]
    public void An_epoch_that_is_not_the_next_one_is_stale(int offered)
    {
        var code = CodeOf(() => EpochRules.Check(3, [Ana], Generations((Ana, 1)), Submission(offered, (Ana, 1))));

        Assert.Equal(KeyEpochCodes.Stale, code);
    }

    [Fact]
    public void A_member_left_out_is_a_change_of_members()
    {
        var code = CodeOf(() => EpochRules.Check(1, [Ana, Ben], Generations((Ana, 1), (Ben, 1)), Submission(2, (Ana, 1))));

        Assert.Equal(KeyEpochCodes.MembersChanged, code);
    }

    [Fact]
    public void Someone_who_is_not_a_member_is_a_change_of_members_even_when_every_member_is_there()
    {
        var code = CodeOf(() => EpochRules.Check(
            1, [Ana, Ben], Generations((Ana, 1), (Ben, 1), (Cy, 1)), Submission(2, (Ana, 1), (Ben, 1), (Cy, 1))));

        Assert.Equal(KeyEpochCodes.MembersChanged, code);
    }

    [Fact]
    public void Someone_who_is_not_a_member_in_place_of_a_member_is_a_change_of_members()
    {
        var code = CodeOf(() => EpochRules.Check(1, [Ana, Ben], Generations((Ana, 1), (Ben, 1), (Cy, 1)), Submission(2, (Ana, 1), (Cy, 1))));

        Assert.Equal(KeyEpochCodes.MembersChanged, code);
    }

    [Fact]
    public void One_member_twice_does_not_stand_in_for_two()
    {
        var code = CodeOf(() => EpochRules.Check(1, [Ana, Ben], Generations((Ana, 1), (Ben, 1)), Submission(2, (Ana, 1), (Ana, 1))));

        Assert.Equal(KeyEpochCodes.MembersChanged, code);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void An_envelope_sealed_to_another_generation_than_the_current_one_is_a_change_of_members(int current, int sealedTo)
    {
        var code = CodeOf(() => EpochRules.Check(1, [Ana], Generations((Ana, current)), Submission(2, (Ana, sealedTo))));

        Assert.Equal(KeyEpochCodes.MembersChanged, code);
    }

    [Fact]
    public void A_member_with_no_keys_is_named_as_such_whether_or_not_an_envelope_was_offered_for_them()
    {
        Assert.Equal(
            KeyEpochCodes.MemberHasNoKeys,
            CodeOf(() => EpochRules.Check(1, [Ana, Ben], Generations((Ana, 1)), Submission(2, (Ana, 1)))));
        Assert.Equal(
            KeyEpochCodes.MemberHasNoKeys,
            CodeOf(() => EpochRules.Check(1, [Ana, Ben], Generations((Ana, 1)), Submission(2, (Ana, 1), (Ben, 1)))));
    }

    [Fact]
    public void Staleness_is_reported_before_anything_else_is_looked_at()
    {
        var code = CodeOf(() => EpochRules.Check(1, [Ana, Ben], Generations((Ana, 1)), Submission(1)));

        Assert.Equal(KeyEpochCodes.Stale, code);
    }
}
