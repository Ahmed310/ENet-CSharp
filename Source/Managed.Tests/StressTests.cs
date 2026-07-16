using System;
using System.Collections.Generic;
using Xunit;

namespace ENet.Tests {
	[Collection("ENet")]
	public class StressTests {
		[Fact]
		public void RapidLoop_10000Packets_AllReceivedInOrder() {
			using LoopbackPair pair = new LoopbackPair();

			const int total = 10000;
			const int batchSize = 100;

			Random rng = new Random(12345);
			int[] sizes = new int[total];

			for (int i = 0; i < total; i++)
				sizes[i] = rng.Next(5, 985);

			PoolStatistics before = Library.GetPoolStatistics();

			int sent = 0;
			int received = 0;
			bool intact = true;

			Action<Host, Event> onEvent = (host, e) => {
				if (host != pair.Server || e.Type != EventType.Receive)
					return;

				Packet incoming = e.Packet;
				byte[] buffer = new byte[incoming.Length];

				incoming.CopyTo(buffer);
				incoming.Dispose();

				int index = BitConverter.ToInt32(buffer, 0);

				if (index != received || buffer.Length != sizes[index]) {
					intact = false;
				} else {
					for (int i = 4; i < buffer.Length; i++) {
						if (buffer[i] != (byte)(index + i)) {
							intact = false;

							break;
						}
					}
				}

				received++;
			};

			while (sent < total) {
				int batch = Math.Min(batchSize, total - sent);

				for (int i = 0; i < batch; i++) {
					byte[] payload = new byte[sizes[sent]];

					BitConverter.TryWriteBytes(payload, sent);

					for (int j = 4; j < payload.Length; j++)
						payload[j] = (byte)(sent + j);

					Packet packet = new Packet();
					packet.Create(payload, payload.Length, PacketFlags.Reliable);

					Assert.True(pair.ClientToServer.Send(0, ref packet));

					sent++;
				}

				Assert.True(pair.PumpUntil(() => received >= sent, onEvent, 30000), $"Timed out at {received}/{sent} packets");
			}

			Assert.Equal(total, received);
			Assert.True(intact, "At least one packet arrived corrupted or out of order");

			PoolStatistics after = Library.GetPoolStatistics();

			Assert.True(after.Hits > before.Hits, "Expected the pool to recycle blocks during the rapid loop");
			Assert.True(after.Retained <= 576);
		}

		[Fact]
		public void BurstSends_ManyPacketsPerTick_MultiplePeers() {
			const int clientCount = 4;
			const int ticks = 10;
			const int packetsPerTick = 100;
			const int payloadSize = 300;

			ushort port = (ushort)27090;

			using Host server = new Host();

			Address listenAddress = new Address { Port = port };
			server.Create(listenAddress, clientCount * 2, 2);

			Host[] clients = new Host[clientCount];
			Peer[] peers = new Peer[clientCount];

			try {
				Address connectAddress = new Address { Port = port };
				Assert.True(connectAddress.SetIP("127.0.0.1"));

				for (int i = 0; i < clientCount; i++) {
					clients[i] = new Host();
					clients[i].Create(1, 2);
					peers[i] = clients[i].Connect(connectAddress, 2);
				}

				int connected = 0;
				Action<Host, Event> onConnect = (host, e) => {
					if (host == server && e.Type == EventType.Connect)
						connected++;
				};

				Assert.True(PumpAll(server, clients, () => connected == clientCount, onConnect), "Timed out connecting clients");

				int received = 0;
				bool intact = true;

				Action<Host, Event> onReceive = (host, e) => {
					if (host != server || e.Type != EventType.Receive)
						return;

					Packet incoming = e.Packet;

					if (incoming.Length != payloadSize) {
						intact = false;
					} else {
						byte[] buffer = new byte[payloadSize];
						incoming.CopyTo(buffer);

						for (int i = 0; i < payloadSize; i++) {
							if (buffer[i] != (byte)(i * 7)) {
								intact = false;

								break;
							}
						}
					}

					incoming.Dispose();
					received++;
				};

				byte[] payload = new byte[payloadSize];

				for (int i = 0; i < payloadSize; i++)
					payload[i] = (byte)(i * 7);

				int expected = 0;

				for (int tick = 0; tick < ticks; tick++) {
					for (int c = 0; c < clientCount; c++) {
						for (int p = 0; p < packetsPerTick; p++) {
							Packet packet = new Packet();
							packet.Create(payload, payload.Length, PacketFlags.Reliable);

							Assert.True(peers[c].Send(0, ref packet));
						}
					}

					expected += clientCount * packetsPerTick;

					Assert.True(PumpAll(server, clients, () => received >= expected, onReceive), $"Timed out at {received}/{expected} packets");
				}

				Assert.Equal(ticks * clientCount * packetsPerTick, received);
				Assert.True(intact, "At least one packet arrived corrupted");
			} finally {
				for (int i = 0; i < clientCount; i++) {
					if (clients[i] != null)
						clients[i].Dispose();
				}
			}
		}

		private static bool PumpAll(Host server, Host[] clients, Func<bool> condition, Action<Host, Event> onEvent, int timeoutMilliseconds = 30000) {
			System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

			while (!condition()) {
				if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
					return false;

				LoopbackPair.DrainHost(server, onEvent);

				foreach (Host client in clients) {
					if (client != null && client.IsSet)
						LoopbackPair.DrainHost(client, onEvent);
				}
			}

			return true;
		}
	}
}
