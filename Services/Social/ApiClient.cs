using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Zink.Services.Calling;

namespace Zink.Services.Social
{
    public sealed class ApiClient
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _jsonOptions;

        public ApiClient()
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(CallServerConfig.BaseHttpUrl + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        private async Task AddAuthHeaderAsync()
        {
            var token = await TokenStore.Instance.GetTokenAsync();

            _http.DefaultRequestHeaders.Authorization = null;

            if (!string.IsNullOrWhiteSpace(token))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        }

        public async Task<AuthResponse> LoginAsync(LoginRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var payload = new
            {
                Username = request.EmailOrUsername?.Trim() ?? "",
                Password = request.Password ?? ""
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync("api/login", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);

            var data = JsonSerializer.Deserialize<ServerLoginResponse>(body, _jsonOptions)
                       ?? throw new Exception("Invalid login response from server.");

            var auth = new AuthResponse
            {
                UserId = data.UserId,
                Email = "",
                Username = data.Username ?? "",
                DisplayName = data.DisplayName ?? "",
                EmailVerified = true,
                AccessToken = data.Token ?? ""
            };

            await TokenStore.Instance.SaveAsync(new Zink.Services.Calling.LoginResponse
            {
                Success = true,
                UserId = checked((int)data.UserId),
                Username = data.Username,
                DisplayName = data.DisplayName,
                Token = data.Token
            });

            return auth;
        }

        public async Task<AuthResponse> LoginAsync(string username, string password)
        {
            return await LoginAsync(new LoginRequest
            {
                EmailOrUsername = username,
                Password = password
            });
        }

        public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var payload = new
            {
                Username = request.Username?.Trim() ?? "",
                Password = request.Password ?? "",
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? request.Username?.Trim() ?? ""
                    : request.DisplayName.Trim()
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync("api/register", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);

            return await LoginAsync(new LoginRequest
            {
                EmailOrUsername = request.Username,
                Password = request.Password
            });
        }

        public async Task<List<UserSummaryDto>> SearchUsersAsync(string query)
        {
            await AddAuthHeaderAsync();

            if (string.IsNullOrWhiteSpace(query))
                return new List<UserSummaryDto>();

            var encoded = Uri.EscapeDataString(query.Trim());

            using var response = await _http.GetAsync($"api/users/search?q={encoded}");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);

            return JsonSerializer.Deserialize<List<UserSummaryDto>>(body, _jsonOptions) ?? new List<UserSummaryDto>();
        }

        public Task SendFriendRequestAsync(long targetUserId)
        {
            throw new NotSupportedException("This server currently adds friends by username, not by user ID.");
        }

        public async Task AddFriendByUsernameAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is required.", nameof(username));

            await AddAuthHeaderAsync();

            var payload = new
            {
                Username = username.Trim()
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync("api/friends/add", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);
        }

        public async Task<List<FriendDto>> GetFriendsAsync()
        {
            await AddAuthHeaderAsync();

            using var response = await _http.GetAsync("api/friends");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);

            return JsonSerializer.Deserialize<List<FriendDto>>(body, _jsonOptions) ?? new List<FriendDto>();
        }

        public async Task<List<FriendRequestDto>> GetRequestsAsync()
        {
            await AddAuthHeaderAsync();

            using var response = await _http.GetAsync("api/friends/requests");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);

            return JsonSerializer.Deserialize<List<FriendRequestDto>>(body, _jsonOptions) ?? new List<FriendRequestDto>();
        }

        public async Task RespondRequestAsync(long requestId, bool accept)
        {
            await AddAuthHeaderAsync();

            var action = accept ? "accept" : "decline";
            using var response = await _http.PostAsync($"api/friends/requests/{requestId}/{action}", null);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);
        }

        public Task BlockUserAsync(long targetUserId)
        {
            return Task.CompletedTask;
        }

        public async Task<MeDto> GetMeAsync()
        {
            await AddAuthHeaderAsync();

            using var response = await _http.GetAsync("api/me");
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception(body);

            var data = JsonSerializer.Deserialize<ServerMeResponse>(body, _jsonOptions)
                       ?? throw new Exception("Invalid me response from server.");

            return new MeDto
            {
                UserId = data.Id,
                Email = "",
                Username = data.Username ?? "",
                DisplayName = data.DisplayName ?? "",
                AvatarUrl = data.AvatarUrl,
                EmailVerified = true,
                Presence = "",
                SignedUpAtUtc = data.SignedUpAtUtc,
                SocialLinks = data.SocialLinks ?? new List<SocialLink>()
            };
        }

        public Task UpdateProfileAsync(string displayName, string? avatarUrl)
        {
            return Task.CompletedTask;
        }

        public async Task LogoutAsync()
        {
            await AddAuthHeaderAsync();

            try
            {
                await _http.PostAsync("api/logout", null);
            }
            finally
            {
                await TokenStore.Instance.ClearAsync();
            }
        }

        private sealed class ServerLoginResponse
        {
            public long UserId { get; set; }
            public string? Username { get; set; }
            public string? DisplayName { get; set; }
            public string? Token { get; set; }
        }

        private sealed class ServerMeResponse
        {
            public long Id { get; set; }
            public string? Username { get; set; }
            public string? DisplayName { get; set; }
            public string? AvatarUrl { get; set; }
            public DateTimeOffset? SignedUpAtUtc { get; set; }
            public List<SocialLink>? SocialLinks { get; set; }
        }
    }
}
