using System;
using System.Diagnostics;

namespace ENet.Tests {
	/// <summary>
	/// A connected server/client host pair on 127.0.0.1. Everything runs on the calling thread,
	/// honoring ENet's single-thread contract. Received packets are handed to the event handler,
	/// which owns disposing them.
	/// </summary>
	internal sealed class LoopbackPair : IDisposable {
		private static int nextPort = 27100;

		public readonly Host Server = new Host();
		public readonly Host Client = new Host();
		public Peer ClientToServer;
		public Peer ServerToClient;

		public LoopbackPair(int channelLimit = 2) {
			ushort port = (ushort)nextPort++;

			Address listenAddress = new Address { Port = port };

			Server.Create(listenAddress, 8, channelLimit);
			Client.Create(1, channelLimit);

			Address connectAddress = new Address { Port = port };

			if (!connectAddress.SetIP("127.0.0.1"))
				throw new InvalidOperationException("Failed to set loopback IP");

			ClientToServer = Client.Connect(connectAddress, channelLimit);

			bool serverConnected = false;
			bool clientConnected = false;

			bool connected = PumpUntil(() => serverConnected && clientConnected, (host, e) => {
				if (e.Type != EventType.Connect)
					return;

				if (host == Server) {
					ServerToClient = e.Peer;
					serverConnected = true;
				} else {
					clientConnected = true;
				}
			});

			if (!connected)
				throw new TimeoutException("Loopback hosts failed to connect");
		}

		/// <summary>
		/// Services both hosts until the condition holds or the timeout elapses. Receive-event
		/// packets are owned by the handler; unhandled receive packets are disposed here.
		/// </summary>
		public bool PumpUntil(Func<bool> condition, Action<Host, Event> onEvent = null, int timeoutMilliseconds = 10000) {
			Stopwatch stopwatch = Stopwatch.StartNew();

			while (!condition()) {
				if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
					return false;

				DrainHost(Server, onEvent);
				DrainHost(Client, onEvent);
			}

			return true;
		}

		/// <summary>Services both hosts for the given duration regardless of events.</summary>
		public void PumpFor(int milliseconds, Action<Host, Event> onEvent = null) {
			Stopwatch stopwatch = Stopwatch.StartNew();

			while (stopwatch.ElapsedMilliseconds < milliseconds) {
				DrainHost(Server, onEvent);
				DrainHost(Client, onEvent);
			}
		}

		public static void DrainHost(Host host, Action<Host, Event> onEvent) {
			Event e;

			if (host.Service(1, out e) <= 0)
				return;

			do {
				if (onEvent != null)
					onEvent(host, e);
				else if (e.Type == EventType.Receive)
					e.Packet.Dispose();
			}
			while (host.CheckEvents(out e) > 0);
		}

		public void Dispose() {
			if (Client.IsSet)
				Client.Flush();

			if (Server.IsSet)
				Server.Flush();

			Client.Dispose();
			Server.Dispose();
		}
	}
}
