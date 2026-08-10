using NUnit.Framework;
using PushUp.Networking;

namespace PushUp.Tests
{
    public sealed class TransportSelectionTests
    {
        [Test]
        public void EditorAndOrdinaryDevelopmentBuildUseLocalTransport()
        {
            Assert.That(TransportSelector.ShouldUseSteamTransport(true, false, false), Is.False);
            Assert.That(TransportSelector.ShouldUseSteamTransport(false, true, false), Is.False);
        }

        [Test]
        public void ForcedOrReleaseBuildUsesSteamTransport()
        {
            Assert.That(TransportSelector.ShouldUseSteamTransport(true, true, true), Is.True);
            Assert.That(TransportSelector.ShouldUseSteamTransport(false, false, false), Is.True);
        }
    }
}
