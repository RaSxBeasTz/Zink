namespace Zink.Pages.Social
{
    public sealed class FriendProfilePageArgs
    {
        public long UserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string Username { get; set; } = "";
        public string? AvatarUrl { get; set; }
        public string AddedText { get; set; } = "Not available";
        public string SignedUpText { get; set; } = "Not available";
    }
}
