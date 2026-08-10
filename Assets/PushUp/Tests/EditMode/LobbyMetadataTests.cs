using NUnit.Framework;
using PushUp.Core;
using PushUp.Steam;

namespace PushUp.Tests
{
    public sealed class LobbyMetadataTests
    {
        [Test]
        public void MetadataAcceptsMatchingProtocolAndBuild()
        {
            LobbyMetadata metadata = new LobbyMetadata(PushUpConstants.ProtocolVersion, "0.1.0", 76561198000000000UL,
                PushUpConstants.LobbyStateWaiting);
            Assert.That(metadata.IsCompatible("0.1.0"), Is.True);
        }

        [Test]
        public void MetadataRejectsWrongBuildOrProtocol()
        {
            LobbyMetadata metadata = new LobbyMetadata("old", "0.1.0", 76561198000000000UL,
                PushUpConstants.LobbyStateWaiting);
            Assert.That(metadata.IsCompatible("0.1.0"), Is.False);

            LobbyMetadata unknownState = new LobbyMetadata(PushUpConstants.ProtocolVersion, "0.1.0",
                76561198000000000UL, "lobby");
            Assert.That(unknownState.IsCompatible("0.1.0"), Is.False);
        }

        [Test]
        public void MetadataRequiresAValidHostSteamId()
        {
            Assert.That(LobbyMetadata.TryParse(PushUpConstants.ProtocolVersion, "0.1.0", "not-a-steamid",
                PushUpConstants.LobbyStateWaiting, out _), Is.False);
            Assert.That(LobbyMetadata.TryParse(PushUpConstants.ProtocolVersion, "0.1.0", "76561198000000000",
                PushUpConstants.LobbyStateWaiting, out LobbyMetadata result), Is.True);
            Assert.That(result.HostSteamId, Is.EqualTo(76561198000000000UL));
        }
    }
}
