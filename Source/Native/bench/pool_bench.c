/*
 *  pool_bench: native create/destroy throughput of enet packets, without P/Invoke in the way.
 *
 *  Loads the ENet shared library given on the command line at run time, so one binary measures any
 *  build (2.6.1 as shipped, the per-thread pool, ENET_NO_POOL). Each thread holds a burst of packets,
 *  then destroys them, as in bragvr-gdd#351.
 *
 *    pool_bench <library> <threads> <size | min-max> <seconds> [held=8] [cold]
 *
 *  "cold" drains the thread's pool cache after every burst, so every create is a first creation.
 *
 *  Prints one JSON object: total and per-thread pairs per second, and burst latency percentiles.
 */

#define _CRT_SECURE_NO_WARNINGS

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
	#define WIN32_LEAN_AND_MEAN
	#include <windows.h>

	typedef HMODULE LibraryHandle;
	#define LOAD_LIBRARY(path) LoadLibraryA(path)
	#define LOAD_SYMBOL(handle, name) ((void*)GetProcAddress(handle, name))
	#define CALLBACK_CC __cdecl
#else
	#include <dlfcn.h>
	#include <pthread.h>
	#include <time.h>
	#include <unistd.h>

	typedef void* LibraryHandle;
	#define LOAD_LIBRARY(path) dlopen(path, RTLD_NOW)
	#define LOAD_SYMBOL(handle, name) dlsym(handle, name)
	#define CALLBACK_CC
#endif

typedef int (CALLBACK_CC *InitializeFunction)(void);
typedef void* (CALLBACK_CC *CreateFunction)(const void*, size_t, uint32_t);
typedef void (CALLBACK_CC *DestroyFunction)(void*);
typedef void (CALLBACK_CC *DrainFunction)(void);

static CreateFunction packet_create;
static DestroyFunction packet_destroy;
static DrainFunction pool_drain;
static int cold;

static volatile int phase; /* 0 warmup, 1 measure, 2 stop */

typedef struct {
	int index;
	int held;
	int sizeMin;
	int sizeMax;
	uint64_t measured;
	uint64_t* samples;
	size_t sampleCount;
	size_t sampleCapacity;
} Worker;

static uint64_t now_ticks(void) {
	#ifdef _WIN32
		LARGE_INTEGER counter;

		QueryPerformanceCounter(&counter);

		return (uint64_t)counter.QuadPart;
	#else
		struct timespec ts;

		clock_gettime(CLOCK_MONOTONIC, &ts);

		return (uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec;
	#endif
}

static double ticks_per_second(void) {
	#ifdef _WIN32
		LARGE_INTEGER frequency;

		QueryPerformanceFrequency(&frequency);

		return (double)frequency.QuadPart;
	#else
		return 1e9;
	#endif
}

static void sleep_ms(int milliseconds) {
	#ifdef _WIN32
		Sleep(milliseconds);
	#else
		usleep((useconds_t)milliseconds * 1000);
	#endif
}

static uint32_t next_random(uint32_t* state) {
	*state ^= *state << 13;
	*state ^= *state >> 17;
	*state ^= *state << 5;

	return *state;
}

#ifdef _WIN32
static DWORD WINAPI worker_main(LPVOID argument) {
#else
static void* worker_main(void* argument) {
#endif
	Worker* worker = (Worker*)argument;
	void** held = (void**)calloc((size_t)worker->held, sizeof(void*));
	uint8_t data[4096];
	uint32_t random = 2463534242u + (uint32_t)worker->index * 7919u;
	uint64_t created = 0, start = 0;
	int last = 0;
	uint64_t iteration = 0;

	memset(data, 0x5A, sizeof(data));

	for (;;) {
		int current = phase;
		int i;

		if (current != last) {
			if (current == 1)
				start = created;

			if (current == 2)
				break;

			last = current;
		}

		int sample = current == 1 && (iteration & 15) == 0 && worker->sampleCount < worker->sampleCapacity;
		uint64_t begin = sample ? now_ticks() : 0;

		for (i = 0; i < worker->held; i++) {
			size_t length = (size_t)worker->sizeMin;

			if (worker->sizeMax > worker->sizeMin)
				length += next_random(&random) % (uint32_t)(worker->sizeMax - worker->sizeMin + 1);

			/* ENET_PACKET_FLAG_UNSEQUENCED */
			held[i] = packet_create(data, length, 2);
		}

		for (i = 0; i < worker->held; i++)
			packet_destroy(held[i]);

		if (cold)
			pool_drain();

		if (sample)
			worker->samples[worker->sampleCount++] = now_ticks() - begin;

		created += (uint64_t)worker->held;
		iteration++;
	}

	worker->measured = created - start;

	free(held);

	return 0;
}

static int compare_u64(const void* a, const void* b) {
	uint64_t x = *(const uint64_t*)a, y = *(const uint64_t*)b;

	return x < y ? -1 : x > y;
}

int main(int argc, char** argv) {
	if (argc < 5) {
		fprintf(stderr, "usage: pool_bench <library> <threads> <size | min-max> <seconds> [held=8]\n");

		return 2;
	}

	const char* path = argv[1];
	int threads = atoi(argv[2]);
	int sizeMin = 0, sizeMax = 0;
	double seconds = atof(argv[4]);
	int held = argc > 5 ? atoi(argv[5]) : 8;
	cold = argc > 6 && strcmp(argv[6], "cold") == 0;
	int i;

	if (sscanf(argv[3], "%d-%d", &sizeMin, &sizeMax) < 2)
		sizeMax = sizeMin;

	LibraryHandle library = LOAD_LIBRARY(path);

	if (library == NULL) {
		fprintf(stderr, "cannot load %s\n", path);

		return 1;
	}

	InitializeFunction initialize = (InitializeFunction)LOAD_SYMBOL(library, "enet_initialize");
	packet_create = (CreateFunction)LOAD_SYMBOL(library, "enet_packet_create");
	packet_destroy = (DestroyFunction)LOAD_SYMBOL(library, "enet_packet_destroy");
	pool_drain = (DrainFunction)LOAD_SYMBOL(library, "enet_pool_drain");

	if (initialize == NULL || packet_create == NULL || packet_destroy == NULL || (cold && pool_drain == NULL) || initialize() != 0) {
		fprintf(stderr, "cannot initialize %s\n", path);

		return 1;
	}

	Worker* workers = (Worker*)calloc((size_t)threads, sizeof(Worker));

	#ifdef _WIN32
		HANDLE* handles = (HANDLE*)calloc((size_t)threads, sizeof(HANDLE));
	#else
		pthread_t* handles = (pthread_t*)calloc((size_t)threads, sizeof(pthread_t));
	#endif

	for (i = 0; i < threads; i++) {
		workers[i].index = i;
		workers[i].held = held;
		workers[i].sizeMin = sizeMin;
		workers[i].sizeMax = sizeMax;
		workers[i].sampleCapacity = 1 << 20;
		workers[i].samples = (uint64_t*)malloc(workers[i].sampleCapacity * sizeof(uint64_t));

		#ifdef _WIN32
			handles[i] = CreateThread(NULL, 0, worker_main, &workers[i], 0, NULL);
		#else
			pthread_create(&handles[i], NULL, worker_main, &workers[i]);
		#endif
	}

	sleep_ms(1000);

	uint64_t begin = now_ticks();

	phase = 1;
	sleep_ms((int)(seconds * 1000));
	phase = 2;

	double elapsed = (double)(now_ticks() - begin) / ticks_per_second();

	for (i = 0; i < threads; i++) {
		#ifdef _WIN32
			WaitForSingleObject(handles[i], INFINITE);
		#else
			pthread_join(handles[i], NULL);
		#endif
	}

	uint64_t total = 0;
	size_t sampleTotal = 0;

	for (i = 0; i < threads; i++) {
		total += workers[i].measured;
		sampleTotal += workers[i].sampleCount;
	}

	uint64_t* samples = (uint64_t*)malloc((sampleTotal + 1) * sizeof(uint64_t));
	size_t offset = 0;

	for (i = 0; i < threads; i++) {
		memcpy(samples + offset, workers[i].samples, workers[i].sampleCount * sizeof(uint64_t));
		offset += workers[i].sampleCount;
	}

	qsort(samples, sampleTotal, sizeof(uint64_t), compare_u64);

	double toNanoseconds = 1e9 / ticks_per_second();

	#define PERCENTILE(q) (sampleTotal ? (double)samples[(size_t)((q) * (double)(sampleTotal - 1))] * toNanoseconds : 0.0)

	printf("{\"mode\":\"native-churn\",\"lib\":\"");

	for (const char* c = path; *c; c++)
		printf(*c == '\\' ? "\\\\" : "%c", *c);

	printf("\",\"cold\":%s,\"threads\":%d,\"size\":\"%s\",\"held\":%d,\"seconds\":%.3f,\"pairs\":%llu,\"pairsPerSecond\":%.1f,\"nsPerPair\":%.3f,\"pairsPerSecondPerThread\":[",
		cold ? "true" : "false", threads, argv[3], held, elapsed, (unsigned long long)total, (double)total / elapsed, elapsed * 1e9 * threads / (double)(total ? total : 1));

	for (i = 0; i < threads; i++)
		printf("%s%.1f", i ? "," : "", (double)workers[i].measured / elapsed);

	printf("],\"burst\":{\"samples\":%zu,\"operations\":%d,\"timerResolutionNs\":%.1f,\"p50Ns\":%.1f,\"p99Ns\":%.1f,\"p999Ns\":%.1f,\"maxNs\":%.1f}}\n",
		sampleTotal, 2 * held, toNanoseconds, PERCENTILE(0.50), PERCENTILE(0.99), PERCENTILE(0.999), sampleTotal ? (double)samples[sampleTotal - 1] * toNanoseconds : 0.0);

	return 0;
}
