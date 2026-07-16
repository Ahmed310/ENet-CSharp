using System;
using System.Security.Cryptography;
using Xunit;

namespace ENet.Tests {
	[Collection("ENet")]
	public class PacketTests {
		internal static byte[] MakePayload(int size, int seed) {
			byte[] payload = new byte[size];

			for (int i = 0; i < size; i++)
				payload[i] = (byte)(seed + i * 31 + (i >> 8));

			return payload;
		}

		private static byte[] RoundTrip(byte[] payload) {
			using LoopbackPair pair = new LoopbackPair();

			Packet packet = new Packet();
			packet.Create(payload, payload.Length, PacketFlags.Reliable);

			Assert.True(pair.ClientToServer.Send(0, ref packet));

			byte[] received = null;

			bool delivered = pair.PumpUntil(() => received != null, (host, e) => {
				if (host == pair.Server && e.Type == EventType.Receive) {
					Packet incoming = e.Packet;

					received = new byte[incoming.Length];
					incoming.CopyTo(received);
					incoming.Dispose();
				}
			}, 30000);

			Assert.True(delivered, "Timed out waiting for the packet");

			// Let acknowledgements flow so the outgoing packet is destroyed before stats are read
			pair.PumpFor(50);

			return received;
		}

		[Fact]
		public void Send_256Bytes_RoundTripsIntact() {
			byte[] payload = MakePayload(256, 1);

			Assert.Equal(payload, RoundTrip(payload));
		}

		[Fact]
		public void Send_984Bytes_UsesPool() {
			// 984 + 40-byte ENetPacket header = exactly one 1024-byte pool block
			PoolStatistics before = Library.GetPoolStatistics();
			byte[] payload = MakePayload(984, 2);

			Assert.Equal(payload, RoundTrip(payload));

			PoolStatistics after = Library.GetPoolStatistics();

			// Send-side create + receive-side create both fit the pool
			Assert.True(after.Hits + after.Misses - before.Hits - before.Misses >= 2, "Expected at least two pool acquisitions");
			Assert.True(after.Returned > before.Returned, "Expected at least one block returned to the pool");
			Assert.Equal(before.Oversized, after.Oversized);
			Assert.True(after.Retained <= 576);
		}

		[Fact]
		public void Send_985Bytes_FallsBack() {
			// 985 + 40 = 1025 > 1024: one byte over the pool block, malloc fallback on both sides
			PoolStatistics before = Library.GetPoolStatistics();
			byte[] payload = MakePayload(985, 3);

			Assert.Equal(payload, RoundTrip(payload));

			PoolStatistics after = Library.GetPoolStatistics();

			Assert.True(after.Oversized - before.Oversized >= 2, "Expected oversized fallback on send and receive side");
		}

		[Fact]
		public void Send_Exactly1024Bytes_RoundTripsIntact_ViaFallback() {
			PoolStatistics before = Library.GetPoolStatistics();
			byte[] payload = MakePayload(1024, 4);

			Assert.Equal(payload, RoundTrip(payload));

			PoolStatistics after = Library.GetPoolStatistics();

			Assert.True(after.Oversized - before.Oversized >= 2, "1024-byte payloads exceed the pool block (header overhead) and must fall back");
		}

		[Fact]
		public void Send_100KB_Reliable_FragmentsAndReassembles() {
			byte[] payload = MakePayload(100 * 1024, 5);
			byte[] received = RoundTrip(payload);

			Assert.NotNull(received);
			Assert.Equal(payload.Length, received.Length);
			Assert.Equal(SHA256.HashData(payload), SHA256.HashData(received));
		}
	}
}
