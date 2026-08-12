namespace MovieTheater.Db
{
    /// <summary>Provenance of <see cref="PhotoAsset.LocationLabel"/> (photos-plan.md §2.4). Stamped
    /// because the reverse-geocode label is a suggestion from a disposable sidecar: dropping Immich
    /// must leave it obvious which labels were machine-derived and can be re-derived.</summary>
    public enum PhotoLocationSource
    {
        Unknown = 0,

        /// <summary>Immich's bundled offline reverse-geocode over the asset's GPS tags.</summary>
        ImmichGeocode = 1,

        /// <summary>A Google Takeout sidecar's location fields (§2.10).</summary>
        GoogleSidecar = 2,

        /// <summary>Typed by a family member; never overwritten by a machine source.</summary>
        Manual = 3,
    }
}
