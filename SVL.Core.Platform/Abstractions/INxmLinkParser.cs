namespace SVL.Core.Platform.Abstractions;

public enum NxmResourceType
{
    ModFile,
    Collection
}

public interface INxmLinkParser
{
    bool TryParse(string? link, out NxmLinkInfo info, out string errorMessage);
}

public sealed class NxmLinkInfo
{
    public NxmResourceType ResourceType { get; init; } = NxmResourceType.ModFile;

    public string GameDomain { get; init; } = string.Empty;

    public long ModId { get; init; }

    public long FileId { get; init; }

    public string CollectionSlug { get; init; } = string.Empty;

    public int RevisionNumber { get; init; } = -1;

    public string? Key { get; init; }

    public long? Expires { get; init; }

    public long? UserId { get; init; }

    public override string ToString()
    {
        if (ResourceType == NxmResourceType.Collection)
        {
            var revisionText = RevisionNumber < 0 ? "latest" : RevisionNumber.ToString();
            return $"{GameDomain} / Collection {CollectionSlug} / Revision {revisionText}";
        }

        return $"{GameDomain} / Mod {ModId} / File {FileId}";
    }
}