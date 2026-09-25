using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ENet.Benchmarks {
	/// <summary>
	/// Packet create/destroy throughput through the managed API, the path FigNet takes. "churn": every
	/// thread holds a burst of packets, then destroys them (the bragvr-gdd#351 loop). "handoff": producer
	/// threads create packets and consumer threads destroy them, so blocks migrate between threads.
	/// </summary>
	internal static class ChurnBenchmark {
		private const int PhaseWarmup = 0;
		private const int PhaseMeasure = 1;
		private const int PhaseStop = 2;

		private static int phase;
		private static volatile bool producersDone;

		public static Dictionary<string, object> Run(Options options) {
			if (options.Mode == "handoff")
				return RunHandoff(options);

			int threadCount = options.Threads;
			ChurnWorker[] workers = new ChurnWorker[threadCount];
			Thread[] threads = new Thread[threadCount];
			CountdownEvent ready = new CountdownEvent(threadCount);
			ManualResetEventSlim go = new ManualResetEventSlim();

			phase = PhaseWarmup;

			for (int i = 0; i < threadCount; i++) {
				ChurnWorker worker = new ChurnWorker(i, options);

				workers[i] = worker;
				threads[i] = new Thread(() => {
					ready.Signal();
					go.Wait();
					worker.Run();
				});
				threads[i].IsBackground = true;
				threads[i].Start();
			}

			ready.Wait();

			Heartbeat heartbeat = options.Heartbeat ? Heartbeat.Start() : null;

			go.Set();
			Thread.Sleep(TimeSpan.FromSeconds(options.WarmupSeconds));

			Process process = Process.GetCurrentProcess();
			PoolSnapshot poolStart = NativeLibraryLoader.ReadPool();
			TimeSpan cpuStart = process.TotalProcessorTime;
			Stopwatch stopwatch = Stopwatch.StartNew();

			Volatile.Write(ref phase, PhaseMeasure);
			Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
			Volatile.Write(ref phase, PhaseStop);

			TimeSpan elapsed = stopwatch.Elapsed;

			foreach (Thread thread in threads)
				thread.Join();

			heartbeat?.Stop();
			process.Refresh();

			TimeSpan cpu = process.TotalProcessorTime - cpuStart;
			PoolSnapshot poolEnd = NativeLibraryLoader.ReadPool();

			long pairs = 0;
			List<long> samples = new List<long>();
			List<double> perThread = new List<double>();

			foreach (ChurnWorker worker in workers) {
				pairs += worker.MeasuredPairs;
				perThread.Add(worker.MeasuredPairs / elapsed.TotalSeconds);

				for (int i = 0; i < worker.SampleCount; i++)
					samples.Add(worker.Samples[i]);
			}

			if (workers[0].Corrupted || Array.Exists(workers, w => w.Corrupted))
				throw new InvalidOperationException("Payload corruption detected");

			Dictionary<string, object> result = Result.Common(options, elapsed);

			result["pairs"] = pairs;
			result["pairsPerSecond"] = pairs / elapsed.TotalSeconds;
			result["pairsPerSecondPerThread"] = perThread;
			result["nsPerPair"] = elapsed.TotalMilliseconds * 1e6 * threadCount / Math.Max(1, pairs);
			result["burst"] = Result.Percentiles(samples, 2 * options.Held);
			result["cpuSeconds"] = cpu.TotalSeconds;
			result["peakWorkingSetMB"] = process.PeakWorkingSet64 / (1024.0 * 1024.0);
			result["workingSetMB"] = process.WorkingSet64 / (1024.0 * 1024.0);
			result["pool"] = Result.PoolDelta(poolStart, poolEnd);

			return result;
		}

		private sealed class ChurnWorker {
			private readonly int index;
			private readonly Options options;

			public long MeasuredPairs;
			public long[] Samples = new long[1 << 20];
			public int SampleCount;
			public bool Corrupted;

			public ChurnWorker(int index, Options options) {
				this.index = index;
				this.options = options;
			}

			public void Run() {
				Random random = new Random(7919 * (index + 1));
				int maximum = Math.Max(options.Size, options.SizeMax);
				byte[] data = new byte[maximum];
				byte[] check = new byte[maximum];
				Packet[] held = new Packet[options.Held];
				int[] lengths = new int[options.Held];
				bool randomSizes = options.SizeMax > 0;
				long created = 0, measureStart = 0;
				int lastPhase = PhaseWarmup;
				int iteration = 0;

				for (int i = 0; i < data.Length; i++)
					data[i] = (byte)(i * 7 + index);

				for (int i = 0; i < lengths.Length; i++)
					lengths[i] = options.Size;

				while (true) {
					int current = Volatile.Read(ref phase);

					if (current != lastPhase) {
						if (current == PhaseMeasure)
							measureStart = created;

						if (current == PhaseStop) {
							MeasuredPairs = created - measureStart;

							return;
						}

						lastPhase = current;
					}

					if (randomSizes) {
						for (int i = 0; i < lengths.Length; i++)
							lengths[i] = options.SizeMin + random.Next(options.SizeMax - options.SizeMin + 1);
					}

					bool sample = current == PhaseMeasure && (iteration & 15) == 0 && SampleCount < Samples.Length;
					long started = sample ? Stopwatch.GetTimestamp() : 0;

					for (int i = 0; i < held.Length; i++)
						held[i].Create(data, lengths[i], options.Flags);

					if (options.Verify) {
						for (int i = 0; i < held.Length; i++) {
							held[i].CopyTo(check);

							if (check[0] != data[0] || check[lengths[i] - 1] != data[lengths[i] - 1])
								Corrupted = true;
						}
					}

					for (int i = 0; i < held.Length; i++)
						held[i].Dispose();

					// Cold: empty this thread's cache so the next burst allocates every block afresh
					if (options.Cold)
						Library.DrainPool();

					if (sample)
						Samples[SampleCount++] = Stopwatch.GetTimestamp() - started;

					created += held.Length;
					iteration++;
				}
			}
		}

		private static Dictionary<string, object> RunHandoff(Options options) {
			// Producers batch packets into arrays so queue overhead stays small next to the allocator
			const int batch = 64;
			int producers = Math.Max(1, options.Threads / 2);
			ConcurrentQueue<Packet[]>[] queues = new ConcurrentQueue<Packet[]>[producers];
			ConcurrentBag<Packet[]>[] spares = new ConcurrentBag<Packet[]>[producers];
			long[] consumed = new long[producers];
			Thread[] threads = new Thread[producers * 2];

			phase = PhaseWarmup;
			producersDone = false;

			for (int p = 0; p < producers; p++) {
				int pair = p;

				queues[p] = new ConcurrentQueue<Packet[]>();
				spares[p] = new ConcurrentBag<Packet[]>();

				for (int i = 0; i < 64; i++)
					spares[p].Add(new Packet[batch]);

				threads[p * 2] = new Thread(() => {
					byte[] data = new byte[options.Size];
					Packet[] packets;

					while (Volatile.Read(ref phase) != PhaseStop) {
						if (!spares[pair].TryTake(out packets)) {
							Thread.SpinWait(20);

							continue;
						}

						for (int i = 0; i < batch; i++)
							packets[i].Create(data, options.Size, options.Flags);

						queues[pair].Enqueue(packets);
					}
				});

				threads[p * 2 + 1] = new Thread(() => {
					long measured = 0;
					bool measuring = false;
					Packet[] packets;

					while (true) {
						int current = Volatile.Read(ref phase);

						if (current == PhaseMeasure)
							measuring = true;

						if (!queues[pair].TryDequeue(out packets)) {
							if (producersDone)
								break;

							Thread.SpinWait(20);

							continue;
						}

						for (int i = 0; i < batch; i++)
							packets[i].Dispose();

						spares[pair].Add(packets);

						if (measuring && current != PhaseStop)
							measured += batch;
					}

					consumed[pair] = measured;
				});
			}

			foreach (Thread thread in threads) {
				thread.IsBackground = true;
				thread.Start();
			}

			Thread.Sleep(TimeSpan.FromSeconds(options.WarmupSeconds));

			Process process = Process.GetCurrentProcess();
			PoolSnapshot poolStart = NativeLibraryLoader.ReadPool();
			TimeSpan cpuStart = process.TotalProcessorTime;
			Stopwatch stopwatch = Stopwatch.StartNew();

			Volatile.Write(ref phase, PhaseMeasure);
			Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
			Volatile.Write(ref phase, PhaseStop);

			TimeSpan elapsed = stopwatch.Elapsed;

			for (int p = 0; p < producers; p++)
				threads[p * 2].Join();

			producersDone = true;

			for (int p = 0; p < producers; p++)
				threads[p * 2 + 1].Join();

			process.Refresh();

			long total = 0;

			foreach (long count in consumed)
				total += count;

			Dictionary<string, object> result = Result.Common(options, elapsed);

			result["pairs"] = total;
			result["pairsPerSecond"] = total / elapsed.TotalSeconds;
			result["cpuSeconds"] = (process.TotalProcessorTime - cpuStart).TotalSeconds;
			result["peakWorkingSetMB"] = process.PeakWorkingSet64 / (1024.0 * 1024.0);
			result["pool"] = Result.PoolDelta(poolStart, NativeLibraryLoader.ReadPool());

			return result;
		}
	}

	/// <summary>Prints "alive" lines so a parent harness can time a crash to the nearest 100 ms.</summary>
	internal sealed class Heartbeat {
		private readonly Thread thread;
		private volatile bool running = true;

		private Heartbeat() {
			Stopwatch stopwatch = Stopwatch.StartNew();

			thread = new Thread(() => {
				while (running) {
					Console.Error.WriteLine($"alive {stopwatch.Elapsed.TotalSeconds:F1}");
					Console.Error.Flush();
					Thread.Sleep(100);
				}
			});
			thread.IsBackground = true;
		}

		public static Heartbeat Start() {
			Heartbeat heartbeat = new Heartbeat();

			heartbeat.thread.Start();

			return heartbeat;
		}

		public void Stop() {
			running = false;
		}
	}
}
