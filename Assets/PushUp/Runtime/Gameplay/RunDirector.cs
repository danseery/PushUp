using System;
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Server;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Owns run state while LevelSpawnService owns all scene-authored placement.</summary>
    public sealed class RunDirector : MonoBehaviour
    {
        private static readonly Vector3 FallbackBoulderScale = Vector3.one * 2.35f;

        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private LevelLayout _levelLayout;
        [SerializeField] private LevelSpawnService _spawnService;

        private float _runStartedAt;
        private bool _complete;
        private bool _listeningForServer;
        private string _lastDirectorError = string.Empty;
        private SummitGoal _subscribedSummit;
        private NetworkRunState _networkRunState;

        public event Action<bool> RunStarted;
        public event Action RunEnded;
        public event Action RunCompleted;

        public BoulderController Boulder => _spawnService != null ? _spawnService.PrimaryBoulder : null;
        public bool IsComplete => _complete;
        public bool IsRunActive => _spawnService != null && _spawnService.IsStarted;
        public bool IsNetworkRun => IsRunActive && _spawnService.IsNetworked;
        public float ElapsedSeconds => _runStartedAt <= 0f ? 0f : Time.time - _runStartedAt;
        public Transform Summit => _spawnService != null && _spawnService.Summit != null
            ? _spawnService.Summit.transform
            : null;
        public string LastStartError => !string.IsNullOrWhiteSpace(_lastDirectorError)
            ? _lastDirectorError
            : _spawnService != null ? _spawnService.LastError : "Spawn service is unavailable.";

        private void Awake() => EnsureServices();

        private void OnEnable()
        {
            EnsureServices();
            if (_networkManager == null || _listeningForServer)
                return;
            _networkManager.ServerManager.OnServerConnectionState += OnServerConnectionState;
            _networkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
            _networkManager.ServerManager.OnAuthenticationResult += OnAuthenticationResult;
            _listeningForServer = true;
        }

        private void OnDisable()
        {
            if (!_listeningForServer || _networkManager == null)
                return;
            _networkManager.ServerManager.OnServerConnectionState -= OnServerConnectionState;
            _networkManager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            _networkManager.ServerManager.OnAuthenticationResult -= OnAuthenticationResult;
            _listeningForServer = false;
        }

        public void BeginOfflineRun()
        {
            EnsureServices();
            _lastDirectorError = string.Empty;
            if ((_networkManager != null && (_networkManager.ServerManager.Started || _networkManager.ClientManager.Started)) ||
                IsRunActive || _spawnService == null)
                return;
            if (!_spawnService.BeginOfflineRun())
            {
                Debug.LogError("Cannot start offline run:\n" + LastStartError);
                return;
            }
            BeginTimer(false);
        }

        public bool BeginNetworkRun()
        {
            EnsureServices();
            _lastDirectorError = string.Empty;
            bool serverStarted = _networkManager != null && _networkManager.ServerManager.Started;
            if (!CanStartNetworkRun(serverStarted, IsRunActive))
                return IsRunActive;

            // Steam hosts run server-only and need an explicit local player. Local-dev
            // host mode has a real client connection and receives an owned player below.
            bool needsServerLocalPlayer = !_networkManager.ClientManager.Started;
            if (!_spawnService.BeginNetworkRun(needsServerLocalPlayer))
            {
                Debug.LogError("Cannot start network run:\n" + LastStartError);
                return false;
            }
            foreach (NetworkConnection connection in _networkManager.ServerManager.Clients.Values)
            {
                if (!connection.IsAuthenticated)
                    continue;
                NetworkPlayerSpawnStatus status = _spawnService.EnsureNetworkPlayer(connection, out _);
                if (status != NetworkPlayerSpawnStatus.Rejected)
                    continue;
                _lastDirectorError = string.IsNullOrWhiteSpace(_spawnService.LastError)
                    ? $"Could not allocate a player spawn for connection {connection.ClientId}."
                    : _spawnService.LastError;
                EndRun();
                return false;
            }
            BeginTimer(true);
            return true;
        }

        public static bool CanStartNetworkRun(bool serverStarted, bool alreadyStarted) =>
            serverStarted && !alreadyStarted;

        public bool ActivateGroup(string groupId) => _spawnService != null && _spawnService.ActivateGroup(groupId);

        public void ResetBoulder()
        {
            if (IsNetworkRun && (_networkManager == null || !_networkManager.ServerManager.Started))
                return;
            Boulder?.ResetToSpawn();
            _complete = false;
            _runStartedAt = Time.time;
        }

        public void EndRun()
        {
            bool wasActive = IsRunActive;
            _networkRunState?.EndRun();
            UnsubscribeSummit();
            _spawnService?.Clear();
            _networkRunState = null;
            _complete = false;
            _runStartedAt = 0f;
            if (wasActive)
                RunEnded?.Invoke();
        }

        private void BeginTimer(bool networked)
        {
            _complete = false;
            _runStartedAt = Time.time;
            SubscribeSummit();
            _networkRunState = networked && Boulder != null ? Boulder.GetComponent<NetworkRunState>() : null;
            if (_networkRunState != null)
                _networkRunState.BeginRun(_networkManager != null && _networkManager.TimeManager != null
                    ? _networkManager.TimeManager.Tick
                    : 0u);
            RunStarted?.Invoke(networked);
        }

        private void OnServerConnectionState(ServerConnectionStateArgs args)
        {
            if (args.ConnectionState != LocalConnectionState.Stopped)
                return;
            EndRun();
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (_spawnService == null)
                return;
            if (args.ConnectionState == RemoteConnectionState.Stopped)
                _spawnService.ReleaseNetworkPlayer(connection);
        }

        private void OnAuthenticationResult(NetworkConnection connection, bool authenticated)
        {
            if (!authenticated || !IsRunActive || _spawnService == null)
                return;
            if (_spawnService.EnsureNetworkPlayer(connection, out _) == NetworkPlayerSpawnStatus.Rejected)
                connection.Kick(KickReason.UnexpectedProblem, log: "No player spawn slot was available after authentication.");
        }

        private void SubscribeSummit()
        {
            UnsubscribeSummit();
            _subscribedSummit = _spawnService != null ? _spawnService.Summit : null;
            if (_subscribedSummit != null)
                _subscribedSummit.BoulderEntered += OnBoulderEnteredSummit;
        }

        private void UnsubscribeSummit()
        {
            if (_subscribedSummit != null)
                _subscribedSummit.BoulderEntered -= OnBoulderEnteredSummit;
            _subscribedSummit = null;
        }

        private void OnBoulderEnteredSummit(BoulderController boulder)
        {
            if (_complete || !IsRunActive || boulder == null || boulder != Boulder)
                return;
            if (IsNetworkRun && (_networkManager == null || !_networkManager.ServerManager.Started))
                return;
            _complete = true;
            _networkRunState?.CompleteRun(ElapsedSeconds);
            RunCompleted?.Invoke();
        }

        private void EnsureServices()
        {
            if (_networkManager == null)
                _networkManager = InstanceFinder.NetworkManager;
            if (_levelLayout == null)
                _levelLayout = FindFirstObjectByType<LevelLayout>(FindObjectsInactive.Include);
            if (_spawnService == null)
                _spawnService = GetComponent<LevelSpawnService>();
            if (_spawnService == null)
                _spawnService = gameObject.AddComponent<LevelSpawnService>();
            _spawnService.Configure(_levelLayout, _networkManager);
        }

        // Compatibility helpers retained for existing tuning tests and editor tooling.
        private static Material MaterialFromPrefab(NetworkObject prefab) =>
            prefab != null && prefab.GetComponentInChildren<Renderer>(true) is { } renderer
                ? renderer.sharedMaterial
                : null;

        private static Vector3 ScaleFromPrefab(NetworkObject prefab) =>
            prefab != null ? prefab.transform.localScale : FallbackBoulderScale;

        private static void SpawnStandalonePlayer(Vector3 position, NetworkObject prefab) =>
            LevelSpawnService.CreateStandalonePlayer(position, Quaternion.identity, prefab);
    }
}
