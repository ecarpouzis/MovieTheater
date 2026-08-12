using System.IO;

namespace MovieTheater.Core
{
    /// <summary>
    /// "Is this data-plane root configured on this host, and if so where?" — one answer, used by every
    /// gateway lane that serves files out of a configured directory.
    ///
    /// <para>It exists because the obvious spelling is wrong in a way that only bites in production.
    /// <c>config["PhotoRootDir"] is string s ? Path.GetFullPath(s) : null</c> reads as "unconfigured
    /// means null", but ASP.NET's configuration binder answers a JSON <c>null</c> — and a key present
    /// with an empty value — with the EMPTY STRING, not null. <c>is string</c> matches it,
    /// <see cref="Path.GetFullPath(string)"/> throws on it, and the throw happens during startup: a host
    /// that merely has the newer appsettings deployed without photos configured loses the whole gateway,
    /// music and movies with it. Unconfigured has to mean "null OR blank", stated once, where a test can
    /// reach it.</para>
    ///
    /// <para>A configured-but-malformed path is deliberately NOT swallowed: that is a real
    /// misconfiguration, and failing loudly at startup is better than 404ing every request forever.</para>
    /// </summary>
    public static class ConfiguredRoot
    {
        /// <summary>The absolute form of a configured directory setting, or null when the host has not
        /// configured it at all (missing key, JSON null, empty or whitespace).</summary>
        public static string? FullPathOrNull(string? configuredValue)
        {
            if (string.IsNullOrWhiteSpace(configuredValue)) return null;
            return Path.GetFullPath(configuredValue!.Trim());
        }
    }
}
