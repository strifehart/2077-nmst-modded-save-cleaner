namespace Nmst.SaveCleaner.Core.Saves;

/// <summary>
/// The sticky "modded" byte: the ModdingSystem node in sav.dat is 5 bytes, a u32 (the node's own index) and a u8
/// flag (1 = modded). On every save the game ORs it back into isModded, so clearing only the JSON doesn't last.
/// </summary>
public static class ModdedFlag
{
    public const int NodeSize = 5;
    public const int FlagOffsetInNode = 4;

    /// <summary>True/false for the flag; null when the save has no ModdingSystem node (older patches).</summary>
    public static bool? ReadSticky(string savPath) => ReadSticky(SavFile.Load(savPath));

    public static bool? ReadSticky(SavFile sav)
    {
        var node = sav.FindNode(SavFile.ModdingSystemNode);
        if (node is null) return null;
        if (node.Size != NodeSize) throw new InvalidDataException($"ModdingSystem node is {node.Size} bytes, expected {NodeSize}");
        return sav.ReadLogical(node.Offset + FlagOffsetInNode, 1)[0] != 0;
    }
}
