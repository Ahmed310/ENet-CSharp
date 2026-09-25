using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ENet.Benchmarks {
	/// <summary>
	/// enet-bench: allocator benchmarks, the bragvr-gdd#351 crash repro, and a multi-process network
	/// stress test with per-second telemetry. Every mode takes --lib to pick the native build under test.
	///
	///   churn          threads create a burst of packets, then destroy them (--threads --size | --size-min/--size-max --held);
	///                  --cold drains the thread's pool cache after every burst, so every create is a first creation
	///   handoff        producer threads create, consumer threads destroy (--threads = producers + consumers)
	///   stress-server  two listeners (state + voice), each serviced on its own thread, relaying to all peers
	///   stress-client  one game client: a state socket and a voice socket, each on its own network thread
	/// </summary>
	internal static class Program {
		private static int Main(string[] args) {
			Options options;

			try {
				options = Options.Parse(args);
			} catch (ArgumentException exception) {
				Console.Error.WriteLine(exception.Message);
				Console.Error.WriteLine("usage: enet-bench --mode churn|handoff|stress-server|stress-client --lib <native library> [options]");

				return 2;
			}

			NativeLibraryLoader.Load(options.Library);

			try {
				Dictionary<string, object> result;

				switch (options.Mode) {
					case "churn":
					case "handoff":
						result = ChurnBenchmark.Run(options);
						break;

					case "stress-server":
						result = StressServer.Run(options);
						break;

					case "stress-client":
						result = StressClient.Run(options);
						break;

					default:
						throw new ArgumentException("Unknown mode " + options.Mode);
				}

				string json = JsonSerializer.Serialize(result);

				Console.WriteLine(json);

				if (!String.IsNullOrEmpty(options.JsonPath))
					File.AppendAllText(options.JsonPath, json + Environment.NewLine);

				return 0;
			} finally {
				NativeLibraryLoader.Unload();
			}
		}
	}

	internal sealed class Options {
		public string Mode = "churn";
		public string Library;
		public string Label = "";
		public int Threads = 1;
		public int Size = 181;
		public int SizeMin;
		public int SizeMax;
		public int Held = 8;
		public double Seconds = 3;
		public double WarmupSeconds = 1;
		public bool Verify;
		public bool Heartbeat;
		public bool Cold;
		public PacketFlags Flags = PacketFlags.Unsequenced;
		public string JsonPath;

		// Stress
		public string Server = "127.0.0.1";
		public int Port = 27500;
		public int Instance;
		public double Scale = 1;
		public double ReconnectSeconds;
		public string TelemetryPath;

		public static Options Parse(string[] args) {
			Options options = new Options();

			for (int i = 0; i < args.Length; i++) {
				string name = args[i];
				string value = i + 1 < args.Length ? args[i + 1] : null;

				switch (name) {
					case "--mode": options.Mode = value; i++; break;
					case "--lib": options.Library = value; i++; break;
					case "--label": options.Label = value; i++; break;
					case "--threads": options.Threads = Int(value); i++; break;
					case "--size": options.Size = Int(value); i++; break;
					case "--size-min": options.SizeMin = Int(value); i++; break;
					case "--size-max": options.SizeMax = Int(value); i++; break;
					case "--held": options.Held = Int(value); i++; break;
					case "--seconds": options.Seconds = Double(value); i++; break;
					case "--warmup": options.WarmupSeconds = Double(value); i++; break;
					case "--verify": options.Verify = true; break;
					case "--heartbeat": options.Heartbeat = true; break;
					case "--cold": options.Cold = true; break;
					case "--reliable": options.Flags = PacketFlags.Reliable; break;
					case "--json": options.JsonPath = value; i++; break;
					case "--server": options.Server = value; i++; break;
					case "--port": options.Port = Int(value); i++; break;
					case "--instance": options.Instance = Int(value); i++; break;
					case "--scale": options.Scale = Double(value); i++; break;
					case "--reconnect-every": options.ReconnectSeconds = Double(value); i++; break;
					case "--telemetry": options.TelemetryPath = value; i++; break;
					default: throw new ArgumentException("Unknown option " + name);
				}
			}

			if (options.SizeMax > 0 && options.SizeMin <= 0)
				options.SizeMin = 1;

			if (options.Threads < 1 || options.Held < 1 || options.Size < 1)
				throw new ArgumentException("--threads, --held and --size must be positive");

			return options;
		}

		private static int Int(string value) {
			if (value == null)
				throw new ArgumentException("Missing value");

			return Int32.Parse(value, CultureInfo.InvariantCulture);
		}

		private static double Double(string value) {
			if (value == null)
				throw new ArgumentException("Missing value");

			return System.Double.Parse(value, CultureInfo.InvariantCulture);
		}
	}

	internal static class Result {
		public static Dictionary<string, object> Common(Options options, TimeSpan elapsed) {
			return new Dictionary<string, object> {
				["mode"] = options.Mode,
				["label"] = options.Label,
				["lib"] = NativeLibraryLoader.LibraryPath,
				["version"] = NativeLibraryLoader.VersionString,
				["poolBlockSize"] = PoolBlockSizeOrUnknown(),
				["threads"] = options.Threads,
				["size"] = options.SizeMax > 0 ? $"{options.SizeMin}-{options.SizeMax}" : options.Size.ToString(CultureInfo.InvariantCulture),
				["held"] = options.Held,
				["cold"] = options.Cold,
				["seconds"] = elapsed.TotalSeconds,
				["os"] = Environment.OSVersion.ToString(),
				["processors"] = Environment.ProcessorCount
			};
		}

		public static Dictionary<string, object> Percentiles(List<long> ticks, int operationsPerSample) {
			Dictionary<string, object> result = new Dictionary<string, object>();

			if (ticks.Count == 0)
				return result;

			ticks.Sort();

			double toNanoseconds = 1e9 / System.Diagnostics.Stopwatch.Frequency;

			result["samples"] = ticks.Count;
			result["operations"] = operationsPerSample;
			result["timerResolutionNs"] = toNanoseconds;
			result["p50Ns"] = ticks[Index(ticks.Count, 0.50)] * toNanoseconds;
			result["p99Ns"] = ticks[Index(ticks.Count, 0.99)] * toNanoseconds;
			result["p999Ns"] = ticks[Index(ticks.Count, 0.999)] * toNanoseconds;
			result["maxNs"] = ticks[ticks.Count - 1] * toNanoseconds;
			result["meanNs"] = ticks.Average() * toNanoseconds;

			return result;
		}

		public static Dictionary<string, object> PoolDelta(PoolSnapshot start, PoolSnapshot end) {
			ulong hits = end.Hits - start.Hits;
			ulong misses = end.Misses - start.Misses;

			return new Dictionary<string, object> {
				["hits"] = hits,
				["misses"] = misses,
				["oversized"] = end.Oversized - start.Oversized,
				["returned"] = end.Returned - start.Returned,
				["freed"] = end.Freed - start.Freed,
				["hitRate"] = hits + misses == 0 ? 0.0 : (double)hits / (hits + misses),
				["retainedEnd"] = end.Retained,
				["cachesEnd"] = end.Caches
			};
		}

		private static int Index(int count, double quantile) {
			return Math.Min(count - 1, (int)Math.Ceiling(quantile * count) - 1);
		}

		private static int PoolBlockSizeOrUnknown() {
			try {
				return Library.PoolBlockSize;
			} catch (EntryPointNotFoundException) {
				// 2.6.x predates the export; its pool block is 1280 bytes
				return -1;
			}
		}
	}
}
