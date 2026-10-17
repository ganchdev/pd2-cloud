using PD2Cloud.Core;
using Xunit;
using PD2Cloud.Desktop.ViewModels;

namespace PD2Cloud.Tests;

public sealed class FileBrowserTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2458, "2.4 KB")]
    [InlineData(6144, "6 KB")]
    [InlineData(262144, "256 KB")]
    [InlineData(524288, "512 KB")]
    [InlineData(1048576, "1 MB")]
    public void Sizes_are_readable_and_round_without_trailing_zeroes(long bytes, string expected) =>
        Assert.Equal(expected, FileBrowser.FormatSize(bytes));

    [Theory]
    [InlineData("Xardian.d2s", "D2S")]
    [InlineData("Xardian.key", "KEY")]
    [InlineData("Xardian.ma0", "MA0")]
    [InlineData("Xardian.ma3", "MA3")]
    [InlineData("Xardian.map", "MAP")]
    [InlineData(".DS_Store", "SYS")]
    [InlineData("notes.txt", "TXT")]
    [InlineData("noextension", "FILE")]
    public void Type_labels_describe_the_file(string path, string expected) =>
        Assert.Equal(expected, Assert.Single(Assert.Single(FileBrowser.Group([Entry(path)], null)).Rows).TypeLabel);

    [Fact]
    public void Character_and_other_files_are_grouped_separately()
    {
        var groups = FileBrowser.Group(
            [Entry("Xardian.d2s"), Entry("Xardian.key"), Entry(".DS_Store")], null);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Character Files", groups[0].Title);
        Assert.Equal("(2)", groups[0].CountText);
        Assert.Equal("Other Files", groups[1].Title);
        Assert.Equal("(1)", groups[1].CountText);
        Assert.Equal("Xardian.d2s", groups[0].Rows[0].Name);
        Assert.Equal(".DS_Store", groups[1].Rows[0].Name);
    }

    [Fact]
    public void Empty_inventory_produces_no_groups() => Assert.Empty(FileBrowser.Group([], null));

    // Timestamps are only read from a real folder; a cloud inventory shows no local timestamp.
    [Fact]
    public void Missing_local_timestamp_is_shown_as_a_dash() =>
        Assert.Equal("—", Assert.Single(Assert.Single(FileBrowser.Group([Entry("Xardian.d2s")], null)).Rows).ModifiedText);

    private static FileEntry Entry(string path) => new(path, 1024, new string('a', 64));
}
