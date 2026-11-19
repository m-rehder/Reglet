namespace Reglet;

/// <summary>
/// ODF has no table inside a list item, and LibreOffice drops one found there. A list holding a
/// table is therefore written as lists around the table; lists of one definition continue each
/// other, so the numbering runs on.
/// </summary>
internal static class OdtListTables
{
    public static bool HasTable(ListBlock list)
        => list.Items.Any(item => item.Blocks.Any(block => block is TableBlock || (block is ListBlock nested && HasTable(nested))));

    /// <remarks>shortcut: what follows a table in its item starts a new item and takes a number; keep the item open with a list header if that matters.</remarks>
    public static List<Block> Split(ListBlock list)
    {
        var result = new List<Block>();
        var items = new List<ListItem>();
        var current = new List<Block>();

        void CloseItem()
        {
            if (current.Count > 0)
            {
                items.Add(new ListItem(current));
                current = [];
            }
        }

        void CloseList()
        {
            CloseItem();
            if (items.Count > 0)
            {
                result.Add(new ListBlock(list.NumberingName, items) { NestingLevel = list.NestingLevel });
                items = [];
            }
        }

        foreach (var item in list.Items)
        {
            if (item.Blocks.Count == 0)
            {
                items.Add(item);
            }

            foreach (var block in item.Blocks)
            {
                foreach (var part in block is ListBlock nested && HasTable(nested) ? Split(nested) : [block])
                {
                    if (part is TableBlock)
                    {
                        CloseList();
                        result.Add(part);
                    }
                    else
                    {
                        current.Add(part);
                    }
                }
            }

            CloseItem();
        }

        CloseList();
        return result;
    }
}
