using Nmst.SaveCleaner.Core;

namespace Nmst.SaveCleaner.ViewModels;

public static class DisclaimerText
{
    public const string Title = "Before you continue";

    public const string Body =
        AppInfo.Name + " has one job: it removes the \"modded\" tag from Cyberpunk 2077 saves, the flag behind the " +
        "modded icon and warning, and the broken-trophy icon on consoles. To do that it changes save files, and " +
        "changing save files can corrupt them and lose progress.\n\n" +
        "• It's an unofficial fan tool, a companion to the No Modded Save Tag plugin. It isn't made, supported or endorsed by CD Projekt RED.\n" +
        "• The app backs up every save before changing it, but backups can fail too. Keep your own copies of saves you care about.\n" +
        "• Close the game before changing saves. Saves synced by Steam Cloud, GOG Galaxy or Cross Progression go wherever the game sends them.\n" +
        "• Game patches can change the save format at any time and break this app.\n\n" +
        "The software is provided \"as is\", without warranty of any kind. You use it entirely at your own risk. " +
        "The author isn't liable for any lost, damaged or corrupted saves, lost progress, achievements or trophies, " +
        "or any other damage that comes from using it.";

    public const string Accept = "I understand the risks and accept them";

    public const string EmptyState = "Pick a save on the left to see whether it's tagged as modded and clean it, or tick several to clean them in one go.";
}
