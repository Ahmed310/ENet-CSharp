using Xunit;

namespace ENet.Tests {
	[Collection("ENet")]
	public class ConnectionTests {
		[Fact]
		public void Connect_Then_Disconnect_RaisesEvents() {
			using LoopbackPair pair = new LoopbackPair();

			Assert.True(pair.ClientToServer.IsSet);
			Assert.True(pair.ServerToClient.IsSet);
			Assert.Equal(PeerState.Connected, pair.ClientToServer.State);
			Assert.Equal(PeerState.Connected, pair.ServerToClient.State);

			bool serverSawDisconnect = false;
			bool clientSawDisconnect = false;

			pair.ClientToServer.Disconnect(0);

			bool done = pair.PumpUntil(() => serverSawDisconnect && clientSawDisconnect, (host, e) => {
				if (e.Type != EventType.Disconnect)
					return;

				if (host == pair.Server)
					serverSawDisconnect = true;
				else
					clientSawDisconnect = true;
			});

			Assert.True(done, "Timed out waiting for disconnect events on both sides");
		}
	}
}
