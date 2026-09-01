using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class SelectedItemPreserverTests
{
    [TestMethod]
    public void Preserve_InsertsMissingSelectedItemAtItsPreviousIndex()
    {
        var selectedItem = new Item(2);

        var result = SelectedItemPreserver.Preserve(
            [new Item(1), new Item(3)],
            selectedItem,
            selectedIndex: 1,
            item => item.Id);

        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, result.Select(item => item.Id).ToArray());
        Assert.AreSame(selectedItem, result[1]);
    }

    private sealed record Item(long Id);
}
