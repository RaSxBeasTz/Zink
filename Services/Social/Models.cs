using System;
using System.Collections.Generic;

namespace Zink.Services.Social
{
    public sealed class UserSummaryDto
    {
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? AvatarUrl { get; set; }
        public string Presence { get; set; } = "";
        public bool IsFriend { get; set; }
        public DateTimeOffset? SignedUpAtUtc { get; set; }
        public List<SocialLink> SocialLinks { get; set; } = new();
    }

    public sealed class FriendDto
    {
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? AvatarUrl { get; set; }
        public string Presence { get; set; } = "";
        public bool IsOnline { get; set; } // matches server
        public DateTimeOffset? SignedUpAtUtc { get; set; }
        public List<SocialLink> SocialLinks { get; set; } = new();
    }

    public sealed class FriendRequestDto
    {
        public long RequestId { get; set; }
        public long FromUserId { get; set; }
        public string FromUsername { get; set; } = "";
        public string FromDisplayName { get; set; } = "";

        public long ToUserId { get; set; }
        public string ToUsername { get; set; } = "";
        public string ToDisplayName { get; set; } = "";

        public string Status { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
    }

    public sealed class MeDto
    {
        public long UserId { get; set; }
        public string Email { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? AvatarUrl { get; set; }
        public bool EmailVerified { get; set; }
        public string Presence { get; set; } = "";
        public DateTimeOffset? SignedUpAtUtc { get; set; }
        public List<SocialLink> SocialLinks { get; set; } = new();
    }

    public sealed class LoginRequest
    {
        public LoginRequest() { }

        public LoginRequest(string emailOrUsername, string password)
        {
            EmailOrUsername = emailOrUsername;
            Password = password;
        }

        public string EmailOrUsername { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public sealed class RegisterRequest
    {
        public RegisterRequest() { }

        public RegisterRequest(string email, string password, string username, string displayName)
        {
            Email = email;
            Password = password;
            Username = username;
            DisplayName = displayName;
        }

        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public sealed class AuthResponse
    {
        public long UserId { get; set; }
        public string Email { get; set; } = "";
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool EmailVerified { get; set; }
        public string AccessToken { get; set; } = "";
    }
}