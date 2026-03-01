namespace SVL.Core.Stardew.Instance;

public enum StardewInstanceCardType
{
    Normal,
    Starred,
    Recent,
    New
}

public interface IStardewInstance
{
    string Path { get; }
    string Name { get; }
    StardewInstanceCardType CardType { get; set; }
    string Description { get; set; }
    string Logo { get; set; }
    bool IsStarred { get; }
    StardewInstanceInfo InstanceInfo { get; set; }
    bool IsSMAPIInstance { get; }
    bool EnableIsolation { get; }

    void Load();
}
