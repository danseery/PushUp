using System.Collections;
using FishNet.Managing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PushUp.Tests
{
    public sealed class ServerOnlyHostHarnessTests
    {
        [UnityTest]
        public IEnumerator MountainStartsAFishNetServerWithoutCreatingALocalClient()
        {
            AsyncOperation load = SceneManager.LoadSceneAsync("Assets/PushUp/Scenes/Mountain.unity",
                LoadSceneMode.Single);
            Assert.That(load, Is.Not.Null);
            while (!load.isDone)
                yield return null;

            NetworkManager manager = Object.FindFirstObjectByType<NetworkManager>();
            Assert.That(manager, Is.Not.Null);
            Assert.That(manager.IsServerStarted, Is.False);
            Assert.That(manager.IsClientStarted, Is.False);
            Assert.That(manager.ServerManager.StartConnection(), Is.True);

            float timeout = Time.realtimeSinceStartup + 3f;
            while (!manager.IsServerStarted && Time.realtimeSinceStartup < timeout)
                yield return null;

            Assert.That(manager.IsServerStarted, Is.True,
                "the Steam host topology must be a genuine server-only FishNet process");
            Assert.That(manager.IsClientStarted, Is.False,
                "starting a host must not silently create an in-process client that masks relay jitter");

            manager.ServerManager.StopConnection(true);
            yield return null;
            Object.Destroy(manager.gameObject);
        }
    }
}
