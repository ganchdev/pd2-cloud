using PD2Cloud.Core;
using Xunit;

namespace PD2Cloud.Tests;

public sealed class SyncPlannerTests
{
    private static readonly FileEntry A = new("nested/a", 1, new string('a', 64));
    private static readonly FileEntry B = new("b", 2, new string('b', 64));
    private static FileEntry[] Files(string state) => state switch
    {
        "empty" => [],
        "base" => [A, B],
        "local-edit" => [A with { Sha256 = new string('c', 64) }, B],
        "remote-edit" => [A, B with { Sha256 = new string('d', 64) }],
        "added" => [A, B, new("extra", 0, new string('e', 64))],
        "missing-a" => [B],
        "missing-b" => [A],
        "renamed" => [A with { RelativePath = "renamed" }, B],
        "resized" => [A with { SizeBytes = 9 }, B],
        _ => throw new ArgumentException(state)
    };

    [Theory]
    // No sync recorded yet: an empty side receives the other; two different populated sides need a choice.
    [InlineData(null, "empty", "empty", SyncAction.InSync)]
    [InlineData(null, "base", "base", SyncAction.InSync)]
    [InlineData(null, "base", "empty", SyncAction.Upload)]
    [InlineData(null, "empty", "base", SyncAction.Download)]
    [InlineData(null, "local-edit", "remote-edit", SyncAction.Conflict)]
    [InlineData(null, "base", "added", SyncAction.Conflict)]
    // After a sync: whichever side changed since then wins; both changing needs a choice.
    [InlineData("base", "base", "base", SyncAction.InSync)]
    [InlineData("base", "local-edit", "local-edit", SyncAction.InSync)]
    [InlineData("base", "local-edit", "base", SyncAction.Upload)]
    [InlineData("base", "added", "base", SyncAction.Upload)]
    [InlineData("base", "base", "remote-edit", SyncAction.Download)]
    [InlineData("base", "base", "added", SyncAction.Download)]
    [InlineData("base", "local-edit", "remote-edit", SyncAction.Conflict)]
    [InlineData("base", "local-edit", "added", SyncAction.Conflict)]
    // Deletions and renames are ordinary changes on the side where they happened.
    [InlineData("base", "missing-a", "base", SyncAction.Upload)]
    [InlineData("base", "base", "missing-b", SyncAction.Download)]
    [InlineData("base", "renamed", "base", SyncAction.Upload)]
    [InlineData("base", "missing-a", "missing-b", SyncAction.Conflict)]
    [InlineData("base", "missing-a", "missing-a", SyncAction.InSync)]
    [InlineData("base", "empty", "base", SyncAction.Upload)]
    [InlineData("empty", "empty", "base", SyncAction.Download)]
    [InlineData("empty", "base", "empty", SyncAction.Upload)]
    // Size is part of identity even though the collection fingerprint excludes it.
    [InlineData("base", "resized", "base", SyncAction.Upload)]
    public void DecisionTable(string? lastSynced, string local, string remote, SyncAction expected) =>
        Assert.Equal(expected, SyncPlanner.Plan(Files(local), Files(remote), lastSynced is null ? null : Files(lastSynced)));

    [Fact]
    public void InputOrderIsIgnored()
    {
        Assert.True(SyncPlanner.SameFiles([A, B], [B, A]));
        Assert.Equal(SyncAction.InSync, SyncPlanner.Plan([B, A], [A, B], null));
    }
}
