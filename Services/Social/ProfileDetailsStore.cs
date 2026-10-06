using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services.Social
{
    public sealed class ProfileDetailsStore
    {
        private const string StoreFileName = "zink-social-profile-details.json";
        private const string AvatarDirectoryName = "profile-pictures";

        public static ProfileDetailsStore Instance { get; } = new ProfileDetailsStore();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private ProfileDetailsStore()
        {
        }

        public async Task<ProfileDetails> GetAsync(long userId)
        {
            var profiles = await ReadAsync();
            if (profiles.TryGetValue(userId.ToString(), out var profile) && profile != null)
                return profile;

            return new ProfileDetails();
        }

        public async Task SaveAsync(long userId, ProfileDetails details)
        {
            if (details == null)
                throw new ArgumentNullException(nameof(details));

            var profiles = await ReadAsync();
            profiles[userId.ToString()] = details;
            await File.WriteAllTextAsync(GetStorePath(), JsonSerializer.Serialize(profiles, _jsonOptions));
        }

        public async Task<string> SaveAvatarAsync(long userId, StorageFile sourceFile)
        {
            if (sourceFile == null)
                throw new ArgumentNullException(nameof(sourceFile));

            var directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, AvatarDirectoryName);
            Directory.CreateDirectory(directory);

            var extension = Path.GetExtension(sourceFile.Name);
            var destinationPath = Path.Combine(directory, $"{userId}{extension}");
            File.Copy(sourceFile.Path, destinationPath, true);
            return destinationPath;
        }

        private async Task<Dictionary<string, ProfileDetails>> ReadAsync()
        {
            var path = GetStorePath();
            if (!File.Exists(path))
                return new Dictionary<string, ProfileDetails>();

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<Dictionary<string, ProfileDetails>>(json, _jsonOptions)
                    ?? new Dictionary<string, ProfileDetails>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, ProfileDetails>();
            }
        }

        private static string GetStorePath()
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, StoreFileName);
        }
    }

    public sealed class ProfileDetails
    {
        public DateTimeOffset? SignedUpAtUtc { get; set; }
        public string? AvatarPath { get; set; }
        public List<SocialLink> SocialLinks { get; set; } = new();
    }

    public sealed class SocialLink
    {
        public string Service { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
