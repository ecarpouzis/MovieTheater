namespace MovieTheater.Db
{
    /// <summary>What a <see cref="PhotoAsset"/> row points at (photos-plan.md §3). Decided by file
    /// extension at inventory time — the cheap pass that must not open files — so it is a coarse
    /// bucket, not a container probe.</summary>
    public enum PhotoAssetKind
    {
        Photo = 0,
        Video = 1,
    }
}
