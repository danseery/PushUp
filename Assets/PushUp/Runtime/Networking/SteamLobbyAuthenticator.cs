using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using PushUp.Core;
using PushUp.Steam;
using Steamworks;
using UnityEngine;

namespace PushUp.Networking
{
    public struct PushUpAdmissionRequest : IBroadcast
    {
        public ulong SteamId;
        public ulong LobbyId;
        public string Protocol;
        public string Build;
    }

    public struct PushUpAdmissionResponse : IBroadcast
    {
        public bool Passed;
        public string Reason;
    }

    /// <summary>
    /// Binds FishNet admission to the Steam connection identity and active lobby.
    /// Steam authenticates identity; this layer validates PushUp compatibility and membership.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SteamLobbyAuthenticator : Authenticator
    {
        public readonly struct AuthenticatedPlayerIdentity
        {
            public readonly NetworkConnection Connection;
            public readonly ulong SteamId;
            public readonly string DisplayName;

            public AuthenticatedPlayerIdentity(NetworkConnection connection, ulong steamId, string displayName)
            {
                Connection = connection;
                SteamId = steamId;
                DisplayName = displayName;
            }
        }

        private SteamSessionService _session;
        private SteamSocketsTransport _steamTransport;
        private readonly Dictionary<int, AuthenticatedPlayerIdentity> _playerIdentities = new();

        public override event Action<NetworkConnection, bool> OnAuthenticationResult;
        public event Action<bool, string> ClientAuthenticationResult;

        public override void InitializeOnce(NetworkManager networkManager)
        {
            base.InitializeOnce(networkManager);
            _session = GetComponent<SteamSessionService>();
            _steamTransport = GetComponent<SteamSocketsTransport>();
            NetworkManager.ServerManager.RegisterBroadcast<PushUpAdmissionRequest>(OnAdmissionRequest, false);
            NetworkManager.ClientManager.RegisterBroadcast<PushUpAdmissionResponse>(OnAdmissionResponse);
            NetworkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
            NetworkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        }

        private void OnDestroy()
        {
            if (!Initialized || NetworkManager == null)
                return;
            NetworkManager.ServerManager.UnregisterBroadcast<PushUpAdmissionRequest>(OnAdmissionRequest);
            NetworkManager.ClientManager.UnregisterBroadcast<PushUpAdmissionResponse>(OnAdmissionResponse);
            NetworkManager.ClientManager.OnClientConnectionState -= OnClientConnectionState;
            NetworkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        }

        private void OnClientConnectionState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Started)
                return;

            bool steamRoute = NetworkManager.TransportManager.Transport == _steamTransport;
            ulong steamId = steamRoute && SteamBootstrap.IsAvailable ? SteamUser.GetSteamID().m_SteamID : 0UL;
            ulong lobbyId = steamRoute && _session != null ? _session.CurrentLobby.m_SteamID : 0UL;
            NetworkManager.ClientManager.Broadcast(new PushUpAdmissionRequest
            {
                SteamId = steamId,
                LobbyId = lobbyId,
                Protocol = PushUpConstants.ProtocolVersion,
                Build = Application.version
            });
        }

        private void OnAdmissionRequest(NetworkConnection connection, PushUpAdmissionRequest request, Channel channel)
        {
            if (connection == null || !connection.IsActive)
                return;

            // Admission packets are reliable, but a reconnecting client may retry while a previous
            // response is in flight. Never authenticate the same FishNet connection twice.
            if (connection.IsAuthenticated)
            {
                NetworkManager.ServerManager.Broadcast(connection, new PushUpAdmissionResponse
                {
                    Passed = true,
                    Reason = string.Empty
                }, false);
                return;
            }

            bool steamRoute = NetworkManager.TransportManager.Transport == _steamTransport;
            string failure = ValidateRequest(connection.ClientId, request, steamRoute);
            bool passed = string.IsNullOrEmpty(failure);
            if (passed)
            {
                ulong steamId = steamRoute ? request.SteamId : 0UL;
                _playerIdentities[connection.ClientId] = new AuthenticatedPlayerIdentity(connection,
                    steamId, ResolveDisplayName(steamId, $"Player {connection.ClientId + 1}"));
            }
            NetworkManager.ServerManager.Broadcast(connection, new PushUpAdmissionResponse
            {
                Passed = passed,
                Reason = passed ? string.Empty : failure
            }, false);
            OnAuthenticationResult?.Invoke(connection, passed);
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped || connection == null ||
                !_playerIdentities.TryGetValue(connection.ClientId, out AuthenticatedPlayerIdentity identity) ||
                !ReferenceEquals(identity.Connection, connection))
                return;
            _playerIdentities.Remove(connection.ClientId);
        }

        public bool TryGetPlayerIdentity(NetworkConnection connection, out AuthenticatedPlayerIdentity identity)
        {
            if (connection != null && _playerIdentities.TryGetValue(connection.ClientId, out identity) &&
                ReferenceEquals(identity.Connection, connection))
                return true;
            identity = default;
            return false;
        }

        public AuthenticatedPlayerIdentity GetServerLocalIdentity()
        {
            ulong steamId = SteamBootstrap.IsAvailable ? SteamUser.GetSteamID().m_SteamID : 0UL;
            string fallback = SteamBootstrap.IsAvailable ? SteamFriends.GetPersonaName() : "Host";
            return new AuthenticatedPlayerIdentity(null, steamId, ResolveDisplayName(steamId, fallback));
        }

        private string ResolveDisplayName(ulong steamId, string fallback)
        {
            if (steamId != 0UL && _session != null)
            {
                SteamLobbySnapshot snapshot = _session.GetCurrentLobbySnapshot();
                for (int index = 0; index < snapshot.MemberCount; index++)
                    if (snapshot.Members[index].SteamId.m_SteamID == steamId)
                        return snapshot.Members[index].Name;
            }
            return string.IsNullOrWhiteSpace(fallback) ? "Player" : fallback;
        }

        private string ValidateRequest(int connectionId, PushUpAdmissionRequest request, bool steamRoute)
        {
            if (!string.Equals(request.Protocol, PushUpConstants.ProtocolVersion, StringComparison.Ordinal))
                return "Network protocol does not match the host.";
            if (!string.Equals(request.Build, Application.version, StringComparison.Ordinal))
                return "Game build does not match the host.";
            if (!steamRoute)
                return string.Empty;
            if (_session == null || !_session.CurrentLobby.IsValid() || request.LobbyId != _session.CurrentLobby.m_SteamID)
                return "Client is not joining the host's active Steam lobby.";
            if (request.SteamId == 0UL || !_session.IsCurrentLobbyMember(request.SteamId))
                return "Steam user is not a current lobby member.";
            string remoteAddress = _steamTransport.GetConnectionAddress(connectionId);
            if (!ulong.TryParse(remoteAddress, out ulong transportSteamId) || transportSteamId != request.SteamId)
                return "Steam transport identity does not match the admission request.";
            return string.Empty;
        }

        private void OnAdmissionResponse(PushUpAdmissionResponse response, Channel channel)
        {
            ClientAuthenticationResult?.Invoke(response.Passed, response.Reason ?? string.Empty);
        }

        public static bool IsCompatible(string protocol, string build, string expectedBuild) =>
            string.Equals(protocol, PushUpConstants.ProtocolVersion, StringComparison.Ordinal) &&
            string.Equals(build, expectedBuild, StringComparison.Ordinal);
    }
}
