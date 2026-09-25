/*
 *  pool_stress: multi-threaded correctness stress for the packet buffer pool, meant to run under
 *  ThreadSanitizer and AddressSanitizer (POSIX). Loads the library under test at run time.
 *
 *    pool_stress <library> <seconds>
 *
 *  Phases, each for <seconds>:
 *    churn     4 threads hold 8 packets of 1-1400 bytes, then destroy them (the bragvr-gdd#351 loop)
 *    handoff   2 producers create, 2 consumers destroy (blocks migrate between thread caches)
 *    exits     short-lived threads use the pool and exit (thread-exit hook, orphan adoption)
 *    readers   2 churn threads while another thread reads statistics and drains
 *  Payloads are verified on every destroy. If the library has enet_pool_get_statistics_ex, the pool's
 *  counter identities are checked once every thread has exited. Exit code 0 means no failure.
 */

#include <dlfcn.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

typedef struct {
	uint32_t flags;
	uint32_t dataLength;
	uint8_t* data;
} PacketHead; /* leading fields of ENetPacket */

typedef struct {
	uint64_t hits, misses, oversized, returned, freed, drained, retained, threadRetained, orphaned, caches;
} PoolStatistics;

static int (*enet_initialize_fn)(void);
static PacketHead* (*packet_create)(const void*, size_t, uint32_t);
static void (*packet_destroy)(void*);
static void (*pool_statistics_ex)(PoolStatistics*);
static void (*pool_drain)(void);

static volatile int stop;
static volatile int failures;

static double now_seconds(void) {
	struct timespec ts;

	clock_gettime(CLOCK_MONOTONIC, &ts);

	return (double)ts.tv_sec + ts.tv_nsec / 1e9;
}

static uint32_t next_random(uint32_t* state) {
	*state ^= *state << 13;
	*state ^= *state >> 17;
	*state ^= *state << 5;

	return *state;
}

static PacketHead* create_checked(uint32_t seed, size_t length) {
	uint8_t data[1400];
	size_t i;

	for (i = 0; i < length; i++)
		data[i] = (uint8_t)(seed + i * 7);

	return packet_create(data, length, 2);
}

static void destroy_checked(PacketHead* packet, uint32_t seed) {
	size_t i;

	for (i = 0; i < packet->dataLength; i++) {
		if (packet->data[i] != (uint8_t)(seed + i * 7)) {
			__atomic_add_fetch(&failures, 1, __ATOMIC_RELAXED);

			break;
		}
	}

	packet_destroy(packet);
}

static void* churn_main(void* argument) {
	uint32_t random = 12345u + (uint32_t)(uintptr_t)argument * 7919u;
	PacketHead* held[8];
	uint32_t seeds[8];
	int i;

	while (!__atomic_load_n(&stop, __ATOMIC_RELAXED)) {
		for (i = 0; i < 8; i++) {
			seeds[i] = next_random(&random);
			held[i] = create_checked(seeds[i], 1 + next_random(&random) % 1400);
		}

		for (i = 0; i < 8; i++)
			destroy_checked(held[i], seeds[i]);
	}

	return NULL;
}

/* Handoff queue: a mutex-guarded ring, which also gives producer -> consumer happens-before as any real queue would */
#define RING 1024

static struct {
	pthread_mutex_t lock;
	PacketHead* packets[RING];
	uint32_t seeds[RING];
	int head, count, producersLeft;
} ring = { PTHREAD_MUTEX_INITIALIZER };

static void* producer_main(void* argument) {
	uint32_t random = 777u + (uint32_t)(uintptr_t)argument;

	while (!__atomic_load_n(&stop, __ATOMIC_RELAXED)) {
		uint32_t seed = next_random(&random);
		PacketHead* packet = create_checked(seed, 1 + next_random(&random) % 1400);

		for (;;) {
			pthread_mutex_lock(&ring.lock);

			if (ring.count < RING) {
				int slot = (ring.head + ring.count) % RING;

				ring.packets[slot] = packet;
				ring.seeds[slot] = seed;
				ring.count++;

				pthread_mutex_unlock(&ring.lock);

				break;
			}

			pthread_mutex_unlock(&ring.lock);
			sched_yield();
		}
	}

	pthread_mutex_lock(&ring.lock);
	ring.producersLeft--;
	pthread_mutex_unlock(&ring.lock);

	return NULL;
}

static void* consumer_main(void* argument) {
	(void)argument;

	for (;;) {
		PacketHead* packet = NULL;
		uint32_t seed = 0;
		int done;

		pthread_mutex_lock(&ring.lock);

		if (ring.count > 0) {
			packet = ring.packets[ring.head];
			seed = ring.seeds[ring.head];
			ring.head = (ring.head + 1) % RING;
			ring.count--;
		}

		done = ring.count == 0 && ring.producersLeft == 0;

		pthread_mutex_unlock(&ring.lock);

		if (packet != NULL)
			destroy_checked(packet, seed);
		else if (done)
			return NULL;
		else
			sched_yield();
	}
}

static void* short_lived_main(void* argument) {
	PacketHead* held[32];
	uint32_t seed = (uint32_t)(uintptr_t)argument;
	int i;

	for (i = 0; i < 32; i++)
		held[i] = create_checked(seed + i, 64 + i * 16);

	for (i = 0; i < 32; i++)
		destroy_checked(held[i], seed + i);

	return NULL;
}

static void* reader_main(void* argument) {
	(void)argument;

	while (!__atomic_load_n(&stop, __ATOMIC_RELAXED)) {
		if (pool_statistics_ex != NULL) {
			PoolStatistics statistics;

			pool_statistics_ex(&statistics);
		}

		pool_drain();
		usleep(100);
	}

	return NULL;
}

static void run_for(double seconds) {
	double end = now_seconds() + seconds;

	while (now_seconds() < end)
		usleep(10000);

	__atomic_store_n(&stop, 1, __ATOMIC_RELAXED);
}

static void join_all(pthread_t* threads, int count) {
	int i;

	for (i = 0; i < count; i++)
		pthread_join(threads[i], NULL);

	__atomic_store_n(&stop, 0, __ATOMIC_RELAXED);
}

int main(int argc, char** argv) {
	pthread_t threads[8];
	double seconds;
	int i, cycles = 0;

	if (argc < 3) {
		fprintf(stderr, "usage: pool_stress <library> <seconds>\n");

		return 2;
	}

	seconds = atof(argv[2]);

	void* library = dlopen(argv[1], RTLD_NOW);

	if (library == NULL) {
		fprintf(stderr, "dlopen: %s\n", dlerror());

		return 1;
	}

	enet_initialize_fn = (int (*)(void))dlsym(library, "enet_initialize");
	packet_create = (PacketHead* (*)(const void*, size_t, uint32_t))dlsym(library, "enet_packet_create");
	packet_destroy = (void (*)(void*))dlsym(library, "enet_packet_destroy");
	pool_statistics_ex = (void (*)(PoolStatistics*))dlsym(library, "enet_pool_get_statistics_ex");
	pool_drain = (void (*)(void))dlsym(library, "enet_pool_drain");

	if (enet_initialize_fn == NULL || packet_create == NULL || packet_destroy == NULL || pool_drain == NULL || enet_initialize_fn() != 0) {
		fprintf(stderr, "cannot initialize %s\n", argv[1]);

		return 1;
	}

	printf("churn: 4 threads\n");

	for (i = 0; i < 4; i++)
		pthread_create(&threads[i], NULL, churn_main, (void*)(uintptr_t)i);

	run_for(seconds);
	join_all(threads, 4);

	printf("handoff: 2 producers, 2 consumers\n");

	ring.producersLeft = 2;

	for (i = 0; i < 2; i++)
		pthread_create(&threads[i], NULL, producer_main, (void*)(uintptr_t)i);

	for (i = 2; i < 4; i++)
		pthread_create(&threads[i], NULL, consumer_main, NULL);

	run_for(seconds);
	join_all(threads, 4);

	printf("exits: short-lived threads\n");

	{
		double end = now_seconds() + seconds;

		while (now_seconds() < end) {
			for (i = 0; i < 4; i++)
				pthread_create(&threads[i], NULL, short_lived_main, (void*)(uintptr_t)(cycles * 4 + i));

			for (i = 0; i < 4; i++)
				pthread_join(threads[i], NULL);

			cycles++;
		}

		printf("  %d cycles of 4 threads\n", cycles);
	}

	printf("readers: statistics and drain racing 2 churn threads\n");

	for (i = 0; i < 2; i++)
		pthread_create(&threads[i], NULL, churn_main, (void*)(uintptr_t)(10 + i));

	pthread_create(&threads[2], NULL, reader_main, NULL);

	run_for(seconds);
	join_all(threads, 3);

	if (pool_statistics_ex != NULL) {
		PoolStatistics statistics;

		/* Every worker has exited (pthread_join returns after its key destructor ran): all counts are published */
		pool_statistics_ex(&statistics);

		printf("counters: hits %llu misses %llu oversized %llu returned %llu freed %llu drained %llu retained %llu orphaned %llu caches %llu\n",
			(unsigned long long)statistics.hits, (unsigned long long)statistics.misses, (unsigned long long)statistics.oversized,
			(unsigned long long)statistics.returned, (unsigned long long)statistics.freed, (unsigned long long)statistics.drained,
			(unsigned long long)statistics.retained, (unsigned long long)statistics.orphaned, (unsigned long long)statistics.caches);

		if (statistics.hits + statistics.misses != statistics.returned + statistics.freed) {
			printf("FAIL: acquisitions %llu != releases %llu\n", (unsigned long long)(statistics.hits + statistics.misses), (unsigned long long)(statistics.returned + statistics.freed));
			failures++;
		}

		if (statistics.retained != statistics.returned - statistics.hits - statistics.drained) {
			printf("FAIL: retained %llu != returned - hits - drained %llu\n", (unsigned long long)statistics.retained, (unsigned long long)(statistics.returned - statistics.hits - statistics.drained));
			failures++;
		}

		if (statistics.caches != 0 || statistics.retained != statistics.orphaned) {
			printf("FAIL: after every worker exited, expected no live cache and every retained block orphaned\n");
			failures++;
		}

		pool_drain();
		pool_statistics_ex(&statistics);

		if (statistics.retained != 0 || statistics.orphaned != 0) {
			printf("FAIL: drain left %llu retained, %llu orphaned\n", (unsigned long long)statistics.retained, (unsigned long long)statistics.orphaned);
			failures++;
		}
	}

	printf(failures ? "FAILED: %d\n" : "OK\n", failures);

	return failures ? 1 : 0;
}
