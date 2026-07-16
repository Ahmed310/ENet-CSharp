using System;
using Xunit;

namespace ENet.Tests {
	[Collection("ENet")]
	public class GuardTests {
		[Fact]
		public void Create_OffsetGreaterThanLength_Throws() {
			Packet packet = new Packet();

			Assert.Throws<ArgumentOutOfRangeException>(() => packet.Create(new byte[10], 8, 4, PacketFlags.None));
		}

		[Fact]
		public void CopyTo_TooSmallDestination_Throws() {
			Packet packet = new Packet();
			packet.Create(PacketTests.MakePayload(100, 8));

			try {
				Assert.Throws<ArgumentOutOfRangeException>(() => packet.CopyTo(new byte[50]));
			} finally {
				packet.Dispose();
			}
		}

		[Fact]
		public void Create_NoAllocateWithManagedArray_Throws() {
			Packet packet = new Packet();

			Assert.Throws<ArgumentException>(() => packet.Create(new byte[16], 16, PacketFlags.NoAllocate));
			Assert.Throws<ArgumentException>(() => packet.Create(new byte[16], 0, 16, PacketFlags.NoAllocate));
		}

		[Fact]
		public void Version_IsLockstepped() {
			// 2.6.0 on both sides; the fixture's successful Initialize() already proved the
			// native library agrees (a mismatch throws "Incompatible version")
			Assert.Equal((2u << 16) | (6u << 8) | 0u, Library.version);
		}

		[Fact]
		public void PoolStatistics_And_Drain_BehaveSanely() {
			Packet packet = new Packet();
			packet.Create(PacketTests.MakePayload(64, 9));
			packet.Dispose();

			PoolStatistics statistics = Library.GetPoolStatistics();

			Assert.True(statistics.Hits + statistics.Misses > 0, "Expected at least one pool acquisition by this point in the run");
			Assert.True(statistics.Retained <= 576);

			Library.DrainPool();

			Assert.Equal(0u, Library.GetPoolStatistics().Retained);
		}
	}
}
