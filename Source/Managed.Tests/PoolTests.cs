using System;
using System.Collections.Generic;
using Xunit;

namespace ENet.Tests {
	[Collection("ENet")]
	public class PoolTests {
		[Fact]
		public void PoolExhaustion_Hold700Undisposed_ThenDisposeAll() {
			using LoopbackPair pair = new LoopbackPair();

			const int total = 700;
			const int batchSize = 50;
			const int payloadSize = 512;

			byte[] payload = PacketTests.MakePayload(payloadSize, 6);
			List<Packet> held = new List<Packet>();

			Action<Host, Event> holdReceived = (host, e) => {
				if (host == pair.Server && e.Type == EventType.Receive)
					held.Add(e.Packet);
			};

			PoolStatistics start = Library.GetPoolStatistics();

			int sent = 0;

			while (sent < total) {
				int batch = Math.Min(batchSize, total - sent);

				for (int i = 0; i < batch; i++) {
					Packet packet = new Packet();
					packet.Create(payload, payload.Length, PacketFlags.Reliable);

					Assert.True(pair.ClientToServer.Send(0, ref packet));

					sent++;
				}

				Assert.True(pair.PumpUntil(() => held.Count >= sent, holdReceived, 30000), $"Timed out at {held.Count}/{sent} held packets");
			}

			Assert.Equal(total, held.Count);

			// Far more blocks are live than the pool can ever retain, so creates had to fall back to malloc
			PoolStatistics whileHolding = Library.GetPoolStatistics();

			Assert.True(whileHolding.Misses > start.Misses, "Expected pool misses while holding more packets than the retention cap");

			// Payloads must stay intact while the packets are held
			byte[] buffer = new byte[payloadSize];

			for (int i = 0; i < held.Count; i++) {
				Packet packet = held[i];

				Assert.Equal(payloadSize, packet.Length);

				packet.CopyTo(buffer);

				Assert.Equal(payload, buffer);
			}

			// Empty the pool so the release accounting below is deterministic: disposing 700
			// pooled blocks into an empty pool must retain exactly the 576-block cap
			Library.DrainPool();

			Assert.Equal(0u, Library.GetPoolStatistics().Retained);

			PoolStatistics beforeDispose = Library.GetPoolStatistics();

			for (int i = 0; i < held.Count; i++) {
				Packet packet = held[i];
				packet.Dispose();
			}

			PoolStatistics afterDispose = Library.GetPoolStatistics();

			Assert.Equal(576u, afterDispose.Returned - beforeDispose.Returned);
			Assert.Equal(576u, afterDispose.Retained);

			Library.DrainPool();

			Assert.Equal(0u, Library.GetPoolStatistics().Retained);

			// Traffic still works after exhaustion, release, and drain
			Packet final = new Packet();
			final.Create(payload, payload.Length, PacketFlags.Reliable);

			Assert.True(pair.ClientToServer.Send(0, ref final));

			bool delivered = false;

			Assert.True(pair.PumpUntil(() => delivered, (host, e) => {
				if (host == pair.Server && e.Type == EventType.Receive) {
					Packet incoming = e.Packet;

					Assert.Equal(payloadSize, incoming.Length);

					incoming.Dispose();
					delivered = true;
				}
			}), "Timed out on the post-exhaustion packet");
		}

		[Fact]
		public void GracefulShutdown_HeldPacketsDisposedAfterDeinitialize() {
			List<Packet> held = new List<Packet>();
			byte[] payload = PacketTests.MakePayload(256, 7);

			LoopbackPair pair = new LoopbackPair();

			try {
				const int total = 100;
				int sent = 0;

				Action<Host, Event> holdReceived = (host, e) => {
					if (host == pair.Server && e.Type == EventType.Receive)
						held.Add(e.Packet);
				};

				for (int i = 0; i < total; i++) {
					Packet packet = new Packet();
					packet.Create(payload, payload.Length, PacketFlags.Reliable);

					Assert.True(pair.ClientToServer.Send(0, ref packet));

					sent++;
				}

				Assert.True(pair.PumpUntil(() => held.Count >= sent, holdReceived, 30000), "Timed out receiving packets to hold");
			} finally {
				pair.Dispose();
			}

			// Hosts are gone and the library is shut down, but held packets remain valid and
			// disposing them must be safe (their blocks bypass the now-disabled pool)
			Library.Deinitialize();

			try {
				byte[] buffer = new byte[payload.Length];

				for (int i = 0; i < held.Count; i++) {
					Packet packet = held[i];

					Assert.Equal(payload.Length, packet.Length);

					packet.CopyTo(buffer);

					Assert.Equal(payload, buffer);

					packet.Dispose();
				}
			} finally {
				Assert.True(Library.Initialize(), "ENet failed to re-initialize after shutdown");
			}

			// The pool works again after re-initialization
			using LoopbackPair fresh = new LoopbackPair();

			Packet probe = new Packet();
			probe.Create(payload, payload.Length, PacketFlags.Reliable);

			Assert.True(fresh.ClientToServer.Send(0, ref probe));

			bool delivered = false;

			Assert.True(fresh.PumpUntil(() => delivered, (host, e) => {
				if (host == fresh.Server && e.Type == EventType.Receive) {
					e.Packet.Dispose();
					delivered = true;
				}
			}), "Timed out after re-initialization");
		}
	}
}
