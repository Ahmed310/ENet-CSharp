using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace ENet.Benchmarks {
	/// <summary>
	/// Wire format of every stress message: a 16-byte header, then filler derived from the header. The
	/// checksum covers the filler, so a block shared by two packets shows up as a corrupt message.
	/// </summary>
	internal static class Message {
		public const int HeaderSize = 16;
		public const byte KindState = 1;
		public const byte KindPose = 2;
		public const byte KindVoice = 3;
		public const byte KindSnapshot = 4;
		public const byte KindBlob = 5;

		public static void Write(byte[] buffer, int length, byte kind, int sender, uint sequence) {
			buffer[0] = 0xE7;
			buffer[1] = 0xE7;
			buffer[2] = kind;
			buffer[3] = 0;
			BitConverter.TryWriteBytes(buffer.AsSpan(4, 4), sender);
			BitConverter.TryWriteBytes(buffer.AsSpan(8, 4), sequence);

			uint seed = sequence * 2654435761u + (uint)sender;

			for (int i = HeaderSize; i < length; i++)
				buffer[i] = (byte)((seed >> ((i & 3) * 8)) + i);

			BitConverter.TryWriteBytes(buffer.AsSpan(12, 4), Checksum(buffer, length));
		}

		public static bool IsValid(byte[] buffer, int length) {
			if (length < HeaderSize || buffer[0] != 0xE7 || buffer[1] != 0xE7)
				return false;

			return BitConverter.ToUInt32(buffer, 12) == Checksum(buffer, length);
		}

		private static uint Checksum(byte[] buffer, int length) {
			uint hash = 2166136261u;

			for (int i = HeaderSize; i < length; i++)
				hash = (hash ^ buffer[i]) * 16777619u;

			return hash ^ (uint)length;
		}
	}

	/// <summary>Counters owned by one socket thread and read by the telemetry sampler.</summary>
	internal sealed class SocketCounters {
		public long Sent, Received, BytesSent, BytesReceived, Corrupt;
		public long Connects, Disconnects, Timeouts, Peers;

		public void CountSent(int bytes) {
			Interlocked.Increment(ref Sent);
			Interlocked.Add(ref BytesSent, bytes);
		}

		public void CountReceived(int bytes) {
			Interlocked.Increment(ref Received);
			Interlocked.Add(ref BytesReceived, bytes);
		}
	}

	/// <summary>One CSV row per second, flushed immediately so a crash keeps everything up to its last second.</summary>
	internal sealed class Telemetry : IDisposable {
		private readonly StreamWriter writer;
		private readonly Options options;
		private readonly string role;
		private readonly Stopwatch stopwatch = Stopwatch.StartNew();
		private readonly Process process = Process.GetCurrentProcess();
		private TimeSpan lastCpu;
		private double lastElapsed;

		public Telemetry(Options options, string role) {
			this.options = options;
			this.role = role;

			if (String.IsNullOrEmpty(options.TelemetryPath))
				return;

			writer = new StreamWriter(new FileStream(options.TelemetryPath, FileMode.Create, FileAccess.Write, FileShare.Read));
			writer.WriteLine("time_utc,elapsed_s,role,instance,pid,label,version,cpu_pct,working_set_mb,private_mb,gc_heap_mb,os_threads," +
				"pool_hits,pool_misses,pool_oversized,pool_returned,pool_freed,pool_drained,pool_retained,pool_orphaned,pool_caches," +
				"state_sent,state_received,state_bytes_sent,state_bytes_received,state_corrupt,state_peers," +
				"voice_sent,voice_received,voice_bytes_sent,voice_bytes_received,voice_corrupt,voice_peers," +
				"connects,disconnects,timeouts,reconnects");
			writer.Flush();
			lastCpu = process.TotalProcessorTime;
		}

		public void Sample(SocketCounters state, SocketCounters voice, long reconnects) {
			if (writer == null)
				return;

			process.Refresh();

			double elapsed = stopwatch.Elapsed.TotalSeconds;
			TimeSpan cpu = process.TotalProcessorTime;
			double cpuPercent = elapsed > lastElapsed ? (cpu - lastCpu).TotalSeconds / (elapsed - lastElapsed) * 100.0 : 0;
			PoolSnapshot pool = NativeLibraryLoader.ReadPool();

			lastCpu = cpu;
			lastElapsed = elapsed;

			string row = String.Join(",",
				DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
				F(elapsed), role, options.Instance, Environment.ProcessId, options.Label, NativeLibraryLoader.VersionString,
				F(cpuPercent), F(process.WorkingSet64 / 1048576.0), F(process.PrivateMemorySize64 / 1048576.0), F(GC.GetTotalMemory(false) / 1048576.0), process.Threads.Count,
				pool.Hits, pool.Misses, pool.Oversized, pool.Returned, pool.Freed, pool.Drained, pool.Retained, pool.Orphaned, pool.Caches,
				Read(ref state.Sent), Read(ref state.Received), Read(ref state.BytesSent), Read(ref state.BytesReceived), Read(ref state.Corrupt), Read(ref state.Peers),
				Read(ref voice.Sent), Read(ref voice.Received), Read(ref voice.BytesSent), Read(ref voice.BytesReceived), Read(ref voice.Corrupt), Read(ref voice.Peers),
				Read(ref state.Connects) + Read(ref voice.Connects), Read(ref state.Disconnects) + Read(ref voice.Disconnects),
				Read(ref state.Timeouts) + Read(ref voice.Timeouts), reconnects);

			writer.WriteLine(row);
			writer.Flush();
		}

		public void Dispose() {
			writer?.Dispose();
		}

		private static long Read(ref long value) {
			return Interlocked.Read(ref value);
		}

		private static string F(double value) {
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}
	}

	/// <summary>
	/// The relay server: a state listener and a voice listener, each serviced on its own thread, like
	/// FigNet's threaded server sockets. State is relayed inline on its network thread. Voice goes
	/// through a separate mixer thread that validates, destroys the received packet and creates the
	/// relay packet, which the voice network thread then broadcasts and ENet destroys, so voice blocks
	/// cross threads in both directions.
	/// </summary>
	internal static class StressServer {
		private static volatile bool stop;

		public static Dictionary<string, object> Run(Options options) {
			SocketCounters state = new SocketCounters();
			SocketCounters voice = new SocketCounters();
			ConcurrentQueue<(uint from, Packet packet)> toMixer = new ConcurrentQueue<(uint, Packet)>();
			ConcurrentQueue<(uint from, Packet packet)> fromMixer = new ConcurrentQueue<(uint, Packet)>();
			Stopwatch stopwatch = Stopwatch.StartNew();

			Thread stateThread = Start("state-listener", () => Listen(options.Port, state, null, null));
			Thread voiceThread = Start("voice-listener", () => Listen(options.Port + 1, voice, toMixer, fromMixer));
			Thread mixerThread = Start("voice-mixer", () => Mix(toMixer, fromMixer, voice));

			using (Telemetry telemetry = new Telemetry(options, "server")) {
				while (stopwatch.Elapsed.TotalSeconds < options.Seconds) {
					Thread.Sleep(1000);
					telemetry.Sample(state, voice, 0);
				}
			}

			stop = true;
			stateThread.Join();
			voiceThread.Join();
			mixerThread.Join();

			return Summary(options, "server", stopwatch.Elapsed, state, voice, 0);
		}

		private static void Listen(int port, SocketCounters counters, ConcurrentQueue<(uint, Packet)> toMixer, ConcurrentQueue<(uint, Packet)> fromMixer) {
			using Host host = new Host();

			Address address = new Address { Port = (ushort)port };
			host.Create(address, 256, 4);

			Dictionary<uint, Peer> peers = new Dictionary<uint, Peer>();
			byte[] buffer = new byte[64 * 1024];

			while (!stop) {
				if (fromMixer != null) {
					(uint, Packet) relay;

					while (fromMixer.TryDequeue(out relay)) {
						Packet packet = relay.Item2;
						int length = packet.Length;
						Peer excluded;

						// Broadcast hands the packet to ENet, which destroys it on this thread once sent
						if (peers.TryGetValue(relay.Item1, out excluded))
							host.Broadcast(0, ref packet, excluded);
						else
							host.Broadcast(0, ref packet);

						counters.CountSent(length);
					}
				}

				Event e;

				if (host.Service(1, out e) <= 0)
					continue;

				do {
					switch (e.Type) {
						case EventType.Connect:
							peers[e.Peer.ID] = e.Peer;
							Interlocked.Increment(ref counters.Connects);
							Interlocked.Exchange(ref counters.Peers, peers.Count);
							break;

						case EventType.Disconnect:
						case EventType.Timeout:
							peers.Remove(e.Peer.ID);
							CountDrop(counters, e.Type);
							Interlocked.Exchange(ref counters.Peers, peers.Count);
							break;

						case EventType.Receive: {
							Packet packet = e.Packet;
							int length = packet.Length;

							counters.CountReceived(length);

							if (toMixer != null) {
								// Handed to the mixer thread, which destroys it there
								toMixer.Enqueue((e.Peer.ID, packet));

								break;
							}

							packet.CopyTo(buffer);

							if (!Message.IsValid(buffer, length))
								Interlocked.Increment(ref counters.Corrupt);

							Packet relay = new Packet();
							relay.Create(packet.Data, length, buffer[2] == Message.KindPose ? PacketFlags.None : PacketFlags.Reliable);

							packet.Dispose();

							host.Broadcast(e.ChannelID, ref relay, e.Peer);
							counters.CountSent(length);
							break;
						}
					}
				} while (host.CheckEvents(out e) > 0);
			}

			host.Flush();
		}

		private static void Mix(ConcurrentQueue<(uint, Packet)> toMixer, ConcurrentQueue<(uint, Packet)> fromMixer, SocketCounters counters) {
			byte[] buffer = new byte[64 * 1024];

			while (!stop) {
				(uint, Packet) item;

				if (!toMixer.TryDequeue(out item)) {
					// Voice is paced in 20 ms frames; a 1 ms nap keeps the mixer from burning a core
					Thread.Sleep(1);

					continue;
				}

				Packet received = item.Item2;
				int length = received.Length;

				received.CopyTo(buffer);

				if (!Message.IsValid(buffer, length))
					Interlocked.Increment(ref counters.Corrupt);

				Packet relay = new Packet();
				relay.Create(buffer, length, PacketFlags.Unsequenced);

				received.Dispose();

				fromMixer.Enqueue((item.Item1, relay));
			}

			(uint, Packet) leftover;

			while (toMixer.TryDequeue(out leftover))
				leftover.Item2.Dispose();
		}

		internal static void CountDrop(SocketCounters counters, EventType type) {
			if (type == EventType.Timeout)
				Interlocked.Increment(ref counters.Timeouts);
			else
				Interlocked.Increment(ref counters.Disconnects);
		}

		internal static Thread Start(string name, ThreadStart body) {
			Thread thread = new Thread(body) { IsBackground = true, Name = name };

			thread.Start();

			return thread;
		}

		internal static Dictionary<string, object> Summary(Options options, string role, TimeSpan elapsed, SocketCounters state, SocketCounters voice, long reconnects) {
			PoolSnapshot pool = NativeLibraryLoader.ReadPool();

			return new Dictionary<string, object> {
				["mode"] = options.Mode,
				["role"] = role,
				["instance"] = options.Instance,
				["label"] = options.Label,
				["version"] = NativeLibraryLoader.VersionString,
				["seconds"] = elapsed.TotalSeconds,
				["stateSent"] = state.Sent,
				["stateReceived"] = state.Received,
				["voiceSent"] = voice.Sent,
				["voiceReceived"] = voice.Received,
				["corrupt"] = state.Corrupt + voice.Corrupt,
				["connects"] = state.Connects + voice.Connects,
				["disconnects"] = state.Disconnects + voice.Disconnects,
				["timeouts"] = state.Timeouts + voice.Timeouts,
				["reconnects"] = reconnects,
				["poolHits"] = pool.Hits,
				["poolMisses"] = pool.Misses,
				["poolRetained"] = pool.Retained,
				["poolCaches"] = pool.Caches,
				["peakWorkingSetMB"] = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0
			};
		}
	}

	/// <summary>
	/// One game client: a state socket (reliable state, unreliable pose, periodic snapshot and a
	/// fragmented blob) and a voice socket (unsequenced voice frames), each on its own network thread.
	/// With --reconnect-every the voice socket periodically disconnects and its thread exits; a new
	/// thread reconnects, the way FigNet starts a network thread per Connect.
	/// </summary>
	internal static class StressClient {
		private static volatile bool stop;

		public static Dictionary<string, object> Run(Options options) {
			SocketCounters state = new SocketCounters();
			SocketCounters voice = new SocketCounters();
			long reconnects = 0;
			Stopwatch stopwatch = Stopwatch.StartNew();

			Thread stateThread = StressServer.Start("state-socket", () => RunSocket(options, options.Port, state, false, 0));
			Thread voiceThread = StressServer.Start("voice-socket", () => RunSocket(options, options.Port + 1, voice, true, options.ReconnectSeconds));

			using (Telemetry telemetry = new Telemetry(options, "client")) {
				while (stopwatch.Elapsed.TotalSeconds < options.Seconds) {
					Thread.Sleep(1000);

					// A voice socket that finished its session exits; start the next one on a new thread
					if (!voiceThread.IsAlive && !stop) {
						reconnects++;
						voiceThread = StressServer.Start("voice-socket", () => RunSocket(options, options.Port + 1, voice, true, options.ReconnectSeconds));
					}

					telemetry.Sample(state, voice, reconnects);
				}
			}

			stop = true;
			stateThread.Join();
			voiceThread.Join();

			return StressServer.Summary(options, "client", stopwatch.Elapsed, state, voice, reconnects);
		}

		private struct Stream {
			public byte Kind;
			public int Size;
			public double Hertz;
			public PacketFlags Flags;
			public byte Channel;
			public double NextDue;
			public uint Sequence;
		}

		private static void RunSocket(Options options, int port, SocketCounters counters, bool isVoice, double sessionSeconds) {
			Stream[] streams = isVoice
				? new[] { new Stream { Kind = Message.KindVoice, Size = 53, Hertz = 50, Flags = PacketFlags.Unsequenced, Channel = 0 } }
				: new[] {
					new Stream { Kind = Message.KindState, Size = 181, Hertz = 20, Flags = PacketFlags.Reliable, Channel = 0 },
					new Stream { Kind = Message.KindPose, Size = 120, Hertz = 30, Flags = PacketFlags.None, Channel = 1 },
					new Stream { Kind = Message.KindSnapshot, Size = 1100, Hertz = 1, Flags = PacketFlags.Reliable, Channel = 2 },
					new Stream { Kind = Message.KindBlob, Size = 3000, Hertz = 0.2, Flags = PacketFlags.Reliable, Channel = 3 }
				};

			byte[] outgoing = new byte[4096];
			byte[] incoming = new byte[64 * 1024];
			Stopwatch session = Stopwatch.StartNew();

			while (!stop) {
				using Host host = new Host();
				host.Create(1, 4);

				Address address = new Address { Port = (ushort)port };
				address.SetHost(options.Server);

				Peer peer = host.Connect(address, 4);
				bool connected = false;
				Stopwatch clock = Stopwatch.StartNew();

				for (int i = 0; i < streams.Length; i++)
					streams[i].NextDue = 0;

				while (!stop) {
					double now = clock.Elapsed.TotalSeconds;

					if (connected) {
						for (int i = 0; i < streams.Length; i++) {
							double interval = 1.0 / (streams[i].Hertz * options.Scale);

							while (streams[i].NextDue <= now) {
								Message.Write(outgoing, streams[i].Size, streams[i].Kind, options.Instance, streams[i].Sequence++);

								Packet packet = new Packet();
								packet.Create(outgoing, streams[i].Size, streams[i].Flags);

								if (peer.Send(streams[i].Channel, ref packet))
									counters.CountSent(streams[i].Size);

								streams[i].NextDue += interval;
							}
						}

						if (sessionSeconds > 0 && session.Elapsed.TotalSeconds >= sessionSeconds) {
							peer.Disconnect(0);
							PumpUntilDisconnected(host, counters);

							return;
						}
					}

					Event e;

					if (host.Service(1, out e) <= 0)
						continue;

					bool dropped = false;

					do {
						switch (e.Type) {
							case EventType.Connect:
								connected = true;
								Interlocked.Increment(ref counters.Connects);
								Interlocked.Exchange(ref counters.Peers, 1);

								for (int i = 0; i < streams.Length; i++)
									streams[i].NextDue = clock.Elapsed.TotalSeconds;
								break;

							case EventType.Disconnect:
							case EventType.Timeout:
								StressServer.CountDrop(counters, e.Type);
								Interlocked.Exchange(ref counters.Peers, 0);
								dropped = true;
								break;

							case EventType.Receive: {
								Packet packet = e.Packet;
								int length = packet.Length;

								packet.CopyTo(incoming);

								if (!Message.IsValid(incoming, length))
									Interlocked.Increment(ref counters.Corrupt);

								counters.CountReceived(length);
								packet.Dispose();
								break;
							}
						}
					} while (host.CheckEvents(out e) > 0);

					// Server gone or timed out: back off and reconnect on a fresh host
					if (dropped) {
						Thread.Sleep(1000);

						break;
					}
				}

				if (stop && connected) {
					peer.Disconnect(0);
					PumpUntilDisconnected(host, counters);
				}
			}
		}

		private static void PumpUntilDisconnected(Host host, SocketCounters counters) {
			Stopwatch wait = Stopwatch.StartNew();

			while (wait.ElapsedMilliseconds < 1000) {
				Event e;

				if (host.Service(10, out e) <= 0)
					continue;

				do {
					if (e.Type == EventType.Receive)
						e.Packet.Dispose();

					if (e.Type == EventType.Disconnect) {
						Interlocked.Increment(ref counters.Disconnects);
						Interlocked.Exchange(ref counters.Peers, 0);

						return;
					}
				} while (host.CheckEvents(out e) > 0);
			}
		}
	}
}
