using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services
{
    public enum ZinkVpnConnectionState
    {
        Disconnected,
        Connected,
        Unknown
    }

    public sealed record ZinkVpnCommandResult(bool Success, string Message, string Detail = "");

    public sealed class ZinkVpnService
    {
        public const string DefaultProfileName = "Zink VPN";

        public Task<ZinkVpnConnectionState> GetConnectionStateAsync(
            string profileName = DefaultProfileName,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                var result = await RunRasdialAsync(cancellationToken).ConfigureAwait(false);
                if (!result.Success && string.IsNullOrWhiteSpace(result.Message))
                    return ZinkVpnConnectionState.Unknown;

                var output = result.Message;
                if (output.IndexOf(profileName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    output.IndexOf("No connections", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return ZinkVpnConnectionState.Connected;
                }

                return ZinkVpnConnectionState.Disconnected;
            }, cancellationToken);
        }

        public async Task<ZinkVpnCommandResult> ConnectAsync(
            string profileName = DefaultProfileName,
            CancellationToken cancellationToken = default)
        {
            return await ConnectAsync(profileName, null, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ZinkVpnCommandResult> ConnectAsync(
            string profileName,
            string? username,
            string? password,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(profileName))
                return new ZinkVpnCommandResult(false, "VPN profile name is empty.");

            var state = await GetConnectionStateAsync(profileName, cancellationToken).ConfigureAwait(false);
            if (state == ZinkVpnConnectionState.Connected)
                return new ZinkVpnCommandResult(true, $"{profileName} is already connected.");

            var result = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password)
                ? await RunRasdialAsync(cancellationToken, profileName, username, password).ConfigureAwait(false)
                : await RunRasdialAsync(cancellationToken, profileName).ConfigureAwait(false);

            if (result.Success)
                return new ZinkVpnCommandResult(true, $"{profileName} is connected.", result.Message);

            return new ZinkVpnCommandResult(false, BuildFriendlyError(profileName, result.Message), result.Message);
        }

        public async Task<ZinkVpnCommandResult> DisconnectAsync(
            string profileName = DefaultProfileName,
            CancellationToken cancellationToken = default)
        {
            var result = await RunRasdialAsync(cancellationToken, profileName, "/disconnect").ConfigureAwait(false);
            if (result.Success)
                return new ZinkVpnCommandResult(true, $"{profileName} is disconnected.", result.Message);

            return new ZinkVpnCommandResult(false, BuildFriendlyError(profileName, result.Message), result.Message);
        }

        private static async Task<ZinkVpnCommandResult> RunRasdialAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "rasdial.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };

                foreach (var argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                var message = string.Join(Environment.NewLine, new[] { output, error }).Trim();

                return new ZinkVpnCommandResult(process.ExitCode == 0, message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ZinkVpnCommandResult(false, ex.Message);
            }
        }

        private static string BuildFriendlyError(string profileName, string rawMessage)
        {
            if (rawMessage.IndexOf("623", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawMessage.IndexOf("phone book entry", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"Windows does not have a VPN profile named {profileName}. Add one in Windows VPN settings, then come back and press Connect.";
            }

            if (rawMessage.IndexOf("691", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The VPN username or password was rejected by the VPN server.";

            if (rawMessage.IndexOf("868", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Windows could not reach the VPN server. Check the server address and internet connection.";

            return string.IsNullOrWhiteSpace(rawMessage)
                ? "Windows could not complete the VPN command."
                : rawMessage;
        }
    }
}
