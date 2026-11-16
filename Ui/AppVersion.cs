using static Shawn.Utils.VersionHelper;

namespace _1RM
{
    public static class AppVersion
    {
        public const uint Major = 1;
        public const uint Minor = 3;
        public const uint Patch = 2;
        public const uint Build = 0;
        public const string BuildDate = "";
        public const string PreRelease = ""; // e.g. "alpha" "beta.2"

        public static readonly Version VersionData = new Version(Major, Minor, Patch, Build, PreRelease);
        public static string Version => VersionData.ToString();

        public static string[] UpdateCheckUrls => new[]
        {
            "https://github.com/Shermanxwz/1Remote/releases",
            "https://github.com/Shermanxwz/1Remote",
        };

        public static string[] UpdatePublishUrls => new[]
        {
            "https://github.com/Shermanxwz/1Remote/releases",
            "https://github.com/Shermanxwz/1Remote",
        };
    }
}
