using System;
using TCPTunnel.Cryptography;

namespace TCPTunnel
{
    public partial class UserInterface
    {
        private static SecureInvitation expectedInvitation;
        private static bool VerifySecureHubTrust(SecureBoot boot, string[] endpoints)
        {
            var pins = new EndpointTrustStore(System.IO.Path.Combine(ApplicationSettings.DataDirectory, "trusted-endpoints"));
            string fingerprint = NativeMlsSession.FingerprintOf(boot.OwnerKey);
            foreach (string endpoint in endpoints) pins.Check(endpoint, fingerprint);
            bool trusted = expectedInvitation == null ? SecureClientBridge.VerifyTrust(boot, endpoints[0]) : SecureClientBridge.TrustVerifiedInvitation(boot, expectedInvitation, endpoints[0]);
            if (trusted) foreach (string endpoint in endpoints) pins.Save(endpoint, fingerprint);
            return trusted;
        }
        internal static bool ConnectInvitation(string text)
        {
            try
            {
                var invitation = SecureInvitation.Parse(text);
                expectedInvitation = invitation;
                if (invitation.Port > 0) return DoConnect(invitation.Host, invitation.Port);
                if (!EnsureNickname()) return false;
                var connection = BluetoothConnector.Connect(invitation.BluetoothAddress, out string error);
                if (connection == null) throw new System.IO.IOException(error);
                return RunPreparedConnection(connection, "Bluetooth " + BluetoothSupport.FormatAddress(invitation.BluetoothAddress), false);
            }
            catch (Exception error) when (error is System.IO.IOException or FormatException or ArgumentException or System.Text.Json.JsonException)
            { ConsoleGraphic.WriteContentLine(error.Message); return false; }
            finally { expectedInvitation = null; }
        }
        private static bool HandleEncryptionCommand(string input)
        {
            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            string command = parts[0].ToLowerInvariant();
            if (command is not ("/invite" or "/pending" or "/approve" or "/identity")) return false;
            bool validArguments = command == "/approve" ? parts.Length is 2 or 3 : command == "/pending" ? parts.Length is 1 or 2 : command == "/invite" ? parts.Length == 1 || parts.Length == 2 && parts[1] is "once" or "revoke" : parts.Length == 1;
            if (!validArguments)
            {
                CommandHelp.Show(command, (text, _) => WriteSystemChatLine(text));
                return true;
            }
            try
            {
                if (command == "/identity" && parts.Length == 1)
                { using var identity = ApplicationSettings.OpenIdentity(); WriteSystemChatLine(identity.Fingerprint); CopyPublicText(identity.Fingerprint, TextId.PublicFingerprintCopied); return true; }
                SecureHubService hub = ServerInterface.SecureHub;
                if (!isLocalHubSession || hub == null) throw new InvalidOperationException(Lang.Current == AppLanguage.Russian ? "Команда доступна владельцу своего E2E Hub." : "This command requires your own E2E Hub.");
                if (command == "/invite")
                {
                    if (parts.Length == 2 && parts[1] == "revoke")
                    {
                        hub.RevokeInvitations();
                        WriteSystemChatLine(Lang.Current == AppLanguage.Russian ? "Одноразовые приглашения отозваны." : "One-time invitations revoked.");
                        return true;
                    }
                    string invitation = parts.Length == 2 ? hub.CreateOneTimeInvitation() : hub.Invitation;
                    WriteSystemChatLine(Lang.Get(TextId.InvitationValue));
                    WriteSystemChatLine(invitation);
                    CopyPublicText(invitation, TextId.InvitationCopied);
                    WriteSystemChatLine(Lang.Get(TextId.InvitationFingerprintLabel, hub.OwnerFingerprint));
                    if (parts.Length == 2) WriteSystemChatLine(Lang.Current == AppLanguage.Russian ? "Одно использование, 10 минут. Любой владелец ссылки сможет войти. Не публикуйте её; /invite revoke отзывает все такие ссылки." : "One use, ten minutes. Anyone holding this link can join. Keep it private; /invite revoke revokes all such links.");
                }
                else if (command == "/pending")
                {
                    if (parts.Length == 2)
                    {
                        if (!TryApprovalNumber(parts[1], out long number)) { CommandHelp.Show(command, (text, _) => WriteSystemChatLine(text)); return true; }
                        var request = hub.GetPendingRequest(number);
                        WritePendingRequest(request); CopyPublicText(request.Fingerprint, TextId.PublicFingerprintCopied);
                    }
                    else
                    {
                        var requests = hub.PendingRequestsSnapshot();
                        if (requests.Length == 0) WriteSystemChatLine(Lang.Get(TextId.ApprovalNoPending));
                        foreach (var request in requests) WritePendingRequest(request);
                        if (requests.Length > 0) WriteSystemChatLine(Lang.Get(TextId.ApprovalVerifyHint));
                    }
                }
                else if (command == "/approve" && parts.Length == 2)
                {
                    if (!TryApprovalNumber(parts[1], out long number)) { CommandHelp.Show(command, (text, _) => WriteSystemChatLine(text)); return true; }
                    var approved = hub.Approve(number); WriteSystemChatLine(Lang.Get(TextId.ApprovalSucceeded, approved.Nickname));
                }
                else if (command == "/approve" && parts.Length == 3)
                { hub.Approve(parts[1].TrimStart('@'), parts[2]); WriteSystemChatLine(Lang.Get(TextId.ApprovalSucceeded, parts[1].TrimStart('@'))); }
                else CommandHelp.Show(command, (text, _) => WriteSystemChatLine(text));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.IO.IOException)
            { WriteSystemChatLine(error.Message); }
            return true;
        }
        private static bool TryApprovalNumber(string value, out long number) => Int64.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number) && number > 0;
        private static void WritePendingRequest(PendingRoomRequest request)
        {
            foreach (string line in request.DisplayLines()) WriteSystemChatLine(line);
        }
        private static bool CopyPublicText(string text, TextId success)
        {
            if (WindowsClipboard.TryCopy(text, out string error)) { WriteSystemChatLine(Lang.Get(success)); return true; }
            WriteSystemChatLine(Lang.Get(TextId.ClipboardCopyFailed, error)); return false;
        }
    }
}
