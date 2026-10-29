#include <cuda_runtime.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <iostream>
#include <mutex>
#include <random>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

#include "TripcodeTables.cuh"

__device__ __constant__ std::uint32_t dSPtrans[512];
__device__ __constant__ std::uint32_t dSKB[512];
__device__ __constant__ std::uint32_t dConSalt[128];
__device__ __constant__ std::uint32_t dShifts[16];
__device__ __constant__ std::uint32_t dCov[64];
__device__ __constant__ unsigned char dSaltMap[128];

namespace {

constexpr int kPasswordLength = 8;
constexpr int kTripLength = 10;
constexpr int kMaxTargets = 16;
constexpr int kThreadsPerBlock = 256;
constexpr int kMaxBatchResults = 1;
constexpr std::uint64_t kCpuChunkSize = 4096;
constexpr std::uint64_t kDefaultBatchSize = 1u << 22;
volatile LONG gStopRequested = 0;
std::atomic<unsigned int> gGpuBlocks{0};
std::atomic<unsigned int> gCpuWorkers{0};
unsigned int gMaxCpuWorkers = std::max(1u, std::thread::hardware_concurrency());
bool gGpuEnabled = false;
bool gCpuEnabled = false;

void applyRuntimeCommand(const std::string& line) {
    std::istringstream command(line);
    std::string name;
    if (!(command >> name)) return;
    if (name == "STOP") {
        InterlockedExchange(&gStopRequested, 1);
        std::cout << "STOPPING\n" << std::flush;
        return;
    }

    unsigned int value = 0;
    if (!(command >> value)) {
        std::cout << "ERROR\tInvalid live setting command.\n" << std::flush;
        return;
    }
    std::string extra;
    if (command >> extra) {
        std::cout << "ERROR\tInvalid live setting command.\n" << std::flush;
        return;
    }

    if (name == "GPU_BLOCKS" && gGpuEnabled && value >= kThreadsPerBlock &&
        value <= 65536u && value % kThreadsPerBlock == 0) {
        gGpuBlocks.store(value, std::memory_order_relaxed);
    } else if (name == "CPU_WORKERS" && gCpuEnabled && value >= 1 && value <= gMaxCpuWorkers) {
        gCpuWorkers.store(value, std::memory_order_relaxed);
    } else {
        std::ostringstream response;
        response << "ERROR\tCannot apply " << name << " setting in the current compute mode.\n";
        std::cout << response.str() << std::flush;
        return;
    }

    std::ostringstream response;
    response << "CONFIG\t" << gGpuBlocks.load(std::memory_order_relaxed)
             << '\t' << gCpuWorkers.load(std::memory_order_relaxed) << '\n';
    std::cout << response.str() << std::flush;
}

void processRuntimeCommands(std::string& pending) {
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    if (input == INVALID_HANDLE_VALUE || GetFileType(input) != FILE_TYPE_PIPE) return;

    for (;;) {
        DWORD available = 0;
        if (!PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr) || available == 0) return;

        char buffer[4096];
        DWORD bytesRead = 0;
        const DWORD requested = std::min<DWORD>(available, sizeof(buffer));
        if (!ReadFile(input, buffer, requested, &bytesRead, nullptr) || bytesRead == 0) return;
        pending.append(buffer, bytesRead);

        std::size_t newline = 0;
        while ((newline = pending.find('\n')) != std::string::npos) {
            auto line = pending.substr(0, newline);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            applyRuntimeCommand(line);
            pending.erase(0, newline + 1);
        }
    }
}

struct Match {
    char password[kPasswordLength + 1];
    char tripcode[kTripLength + 1];
    int targetIndex;
};

BOOL WINAPI handleConsoleControl(DWORD signal) {
    if (signal == CTRL_C_EVENT || signal == CTRL_BREAK_EVENT) {
        InterlockedExchange(&gStopRequested, 1);
        return TRUE;
    }
    return FALSE;
}

void checkCuda(cudaError_t error, const char* operation) {
    if (error != cudaSuccess) {
        std::cerr << operation << ": " << cudaGetErrorString(error) << '\n';
        std::exit(EXIT_FAILURE);
    }
}

std::string trim(std::string value) {
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) return {};
    const auto last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1);
}

unsigned char lowerAscii(unsigned char value) {
    return value >= 'A' && value <= 'Z'
        ? static_cast<unsigned char>(value + ('a' - 'A'))
        : value;
}

bool validTarget(const std::string& target) {
    if (target.empty() || target.size() > kTripLength) return false;
    for (unsigned char ch : target) {
        if (!((ch >= '0' && ch <= '9') ||
              (ch >= 'A' && ch <= 'Z') ||
              (ch >= 'a' && ch <= 'z') || ch == '.' || ch == '/')) {
            return false;
        }
    }
    return true;
}

std::vector<std::string> splitTargets(const std::string& text) {
    std::vector<std::string> targets;
    std::size_t start = 0;
    while (start <= text.size()) {
        const auto comma = text.find(',', start);
        auto target = trim(text.substr(start, comma == std::string::npos ? comma : comma - start));
        if (!target.empty()) targets.push_back(std::move(target));
        if (comma == std::string::npos) break;
        start = comma + 1;
    }
    return targets;
}

struct Options {
    std::string targetText;
    std::string mode = "any";
    std::string charset = "an";
    std::uint64_t batchSize = kDefaultBatchSize;
    bool ignoreCase = false;
    bool continueAfterHit = false;
    std::string compute = "gpu";
    unsigned int cpuWorkers = std::max(1u, std::thread::hardware_concurrency() > 1
        ? std::thread::hardware_concurrency() - 1 : 1u);
    bool interactive = true;
};

void printUsage() {
    std::cout
        << "TripcodeCuda - CUDA tripcode search for NVIDIA GPU\n\n"
        << "Usage:\n"
        << "  TripcodeCuda.exe [--target TEXT[,TEXT...]] [--mode any|start|end]\n"
        << "                   [--charset an|lower|sym] [--ignore-case] [--batch COUNT]\n"
        << "                   [--compute gpu|cpu|both] [--cpu-workers COUNT] [--continue]\n\n"
        << "Without --target, prompts for search options. Press Ctrl+C to stop.\n"
        << "The batch count is the number of password candidates per CUDA launch.\n"
        << "--continue keeps searching after reporting a hit; the default stops after the first hit.\n";
}

Options parseOptions(int argc, char** argv) {
    Options options;
    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--help" || arg == "-h") {
            printUsage();
            std::exit(EXIT_SUCCESS);
        } else if (arg == "--ignore-case") {
            options.ignoreCase = true;
        } else if (arg == "--continue") {
            options.continueAfterHit = true;
        } else if (arg == "--target" || arg == "--mode" || arg == "--charset" ||
                   arg == "--batch" || arg == "--compute" || arg == "--cpu-workers") {
            if (++i >= argc) {
                std::cerr << "Missing value for " << arg << ".\n";
                std::exit(EXIT_FAILURE);
            }
            const std::string value = argv[i];
            if (arg == "--target") options.targetText = value;
            else if (arg == "--mode") options.mode = value;
            else if (arg == "--charset") options.charset = value;
            else if (arg == "--compute") options.compute = value;
            else {
                char* end = nullptr;
                const auto parsed = std::strtoull(value.c_str(), &end, 10);
                const auto minimum = arg == "--batch" ? 65536u : 1u;
                const auto maximum = arg == "--batch" ? (1u << 24) : std::max(1u, std::thread::hardware_concurrency());
                if (!end || *end || parsed < minimum || parsed > maximum) {
                    std::cerr << (arg == "--batch"
                        ? "--batch must be between 65536 and 16777216.\n"
                        : "--cpu-workers must be between 1 and the number of logical processors.\n");
                    std::exit(EXIT_FAILURE);
                }
                if (arg == "--batch") options.batchSize = parsed;
                else options.cpuWorkers = static_cast<unsigned int>(parsed);
            }
        } else {
            std::cerr << "Unknown option: " << arg << '\n';
            printUsage();
            std::exit(EXIT_FAILURE);
        }
    }
    options.interactive = options.targetText.empty();
    return options;
}

void promptOptions(Options& options) {
    std::cout << "Search text (comma-separated targets): ";
    std::getline(std::cin, options.targetText);

    std::cout << "Match location [any/start/end] (default any): ";
    std::string value;
    std::getline(std::cin, value);
    if (!trim(value).empty()) options.mode = trim(value);

    std::cout << "Password charset [an/lower/sym] (default an): ";
    std::getline(std::cin, value);
    if (!trim(value).empty()) options.charset = trim(value);

    std::cout << "Ignore case? [y/N]: ";
    std::getline(std::cin, value);
    options.ignoreCase = !value.empty() && (value[0] == 'y' || value[0] == 'Y');
}

bool endsWithAllowedChar(const std::string& target, bool ignoreCase) {
    constexpr char allowed[] = ".26AEIMQUYcgkosw";
    const char last = target.back();
    for (char candidate : allowed) {
        if (!candidate) break;
        if (last == candidate ||
            (ignoreCase && lowerAscii(static_cast<unsigned char>(last)) ==
                           lowerAscii(static_cast<unsigned char>(candidate)))) {
            return true;
        }
    }
    return false;
}

#if defined(__CUDA_ARCH__)
#define SKB_TABLE dSKB
#define CONSALT_TABLE dConSalt
#define SHIFTS_TABLE dShifts
#define COV_TABLE dCov
#else
#define SKB_TABLE hSKB
#define CONSALT_TABLE hConSalt
#define SHIFTS_TABLE hShifts
#define COV_TABLE hCov
#endif

__host__ __device__ __forceinline__ std::uint32_t rotateRight28(std::uint32_t value, unsigned shift) {
    return (value >> shift) | (value << (28u - shift));
}

__host__ __device__ void makeTripcode(
    const unsigned char key[kPasswordLength],
    unsigned char output[kTripLength],
    const unsigned char* saltMap,
    const std::uint32_t* spTable) {
    std::uint32_t subkeys[32];
    const std::uint32_t e0 = CONSALT_TABLE[saltMap[key[1]]];
    const std::uint32_t e1 = CONSALT_TABLE[saltMap[key[2]]] << 4u;

    std::uint32_t c = (static_cast<std::uint32_t>(key[0]) << 1u) |
                      (static_cast<std::uint32_t>(key[1]) << 9u) |
                      (static_cast<std::uint32_t>(key[2]) << 17u) |
                      (static_cast<std::uint32_t>(key[3]) << 25u);
    std::uint32_t d = (static_cast<std::uint32_t>(key[4]) << 1u) |
                      (static_cast<std::uint32_t>(key[5]) << 9u) |
                      (static_cast<std::uint32_t>(key[6]) << 17u) |
                      (static_cast<std::uint32_t>(key[7]) << 25u);
    std::uint32_t t = ((d >> 4u) ^ c) & 0x0f0f0f0fu; c ^= t; d ^= t << 4u;
    t = ((c << 18u) ^ c) & 0xcccc0000u; c = c ^ t ^ (t >> 18u);
    t = ((d << 18u) ^ d) & 0xcccc0000u; d = d ^ t ^ (t >> 18u);
    t = ((d >> 1u) ^ c) & 0x55555555u; c ^= t; d ^= t << 1u;
    t = ((c >> 8u) ^ d) & 0x00ff00ffu; d ^= t; c ^= t << 8u;
    t = ((d >> 1u) ^ c) & 0x55555555u; c ^= t; d ^= t << 1u;
    d = ((d & 0xffu) << 16u) | (d & 0xff00u) |
        ((d & 0xff0000u) >> 16u) | ((c & 0xf0000000u) >> 4u);
    c &= 0x0fffffffu;

    unsigned int subkeyIndex = 0;
#pragma unroll
    for (unsigned int i = 0; i < 16; ++i) {
        if (SHIFTS_TABLE[i]) {
            c = rotateRight28(c, 2); d = rotateRight28(d, 2);
        } else {
            c = rotateRight28(c, 1); d = rotateRight28(d, 1);
        }
        c &= 0x0fffffffu; d &= 0x0fffffffu;
        std::uint32_t s = SKB_TABLE[c & 0x3fu] |
            SKB_TABLE[64u + (((c >> 6u) & 0x03u) | ((c >> 7u) & 0x3cu))] |
            SKB_TABLE[128u + (((c >> 13u) & 0x0fu) | ((c >> 14u) & 0x30u))] |
            SKB_TABLE[192u + (((c >> 20u) & 0x01u) | ((c >> 21u) & 0x06u) | ((c >> 22u) & 0x38u))];
        std::uint32_t u = SKB_TABLE[256u + (d & 0x3fu)] |
            SKB_TABLE[320u + (((d >> 7u) & 0x03u) | ((d >> 8u) & 0x3cu))] |
            SKB_TABLE[384u + ((d >> 15u) & 0x3fu)] |
            SKB_TABLE[448u + (((d >> 21u) & 0x0fu) | ((d >> 22u) & 0x30u))];
        subkeys[subkeyIndex++] = (u << 16u) | (s & 0xffffu);
        s = (s >> 16u) | (u & 0xffff0000u);
        subkeys[subkeyIndex++] = (s << 4u) | (s >> 28u);
    }

    std::uint32_t left = 0, right = 0;
#pragma unroll
    for (unsigned int round = 0; round < 25; ++round) {
#pragma unroll
        for (unsigned int i = 0; i < 32; i += 4) {
            std::uint32_t v = right ^ (right >> 16u);
            std::uint32_t x = v & e0; v &= e1;
            x = (x ^ (x << 16u)) ^ right ^ subkeys[i];
            std::uint32_t y = (v ^ (v << 16u)) ^ right ^ subkeys[i + 1];
            y = (y >> 4u) | (y << 28u);
            left ^= spTable[64u + (y & 0x3fu)] |
                spTable[192u + ((y >> 8u) & 0x3fu)] |
                spTable[320u + ((y >> 16u) & 0x3fu)] |
                spTable[448u + ((y >> 24u) & 0x3fu)] |
                spTable[x & 0x3fu] |
                spTable[128u + ((x >> 8u) & 0x3fu)] |
                spTable[256u + ((x >> 16u) & 0x3fu)] |
                spTable[384u + ((x >> 24u) & 0x3fu)];
            v = left ^ (left >> 16u);
            x = v & e0; v &= e1;
            x = (x ^ (x << 16u)) ^ left ^ subkeys[i + 2];
            y = (v ^ (v << 16u)) ^ left ^ subkeys[i + 3];
            y = (y >> 4u) | (y << 28u);
            right ^= spTable[64u + (y & 0x3fu)] |
                spTable[192u + ((y >> 8u) & 0x3fu)] |
                spTable[320u + ((y >> 16u) & 0x3fu)] |
                spTable[448u + ((y >> 24u) & 0x3fu)] |
                spTable[x & 0x3fu] |
                spTable[128u + ((x >> 8u) & 0x3fu)] |
                spTable[256u + ((x >> 16u) & 0x3fu)] |
                spTable[384u + ((x >> 24u) & 0x3fu)];
        }
        const std::uint32_t swap = left; left = right; right = swap;
    }

    t = right; right = (left >> 1u) | (left << 31u); left = (t >> 1u) | (t << 31u);
    t = ((right >> 1u) ^ left) & 0x55555555u; right ^= t << 1u; left ^= t;
    t = ((left >> 8u) ^ right) & 0x00ff00ffu; left ^= t << 8u; right ^= t;
    t = ((right >> 2u) ^ left) & 0x33333333u; right ^= t << 2u; left ^= t;
    t = ((left >> 16u) ^ right) & 0x0000ffffu; left ^= t << 16u; right ^= t;
    t = ((right >> 4u) ^ left) & 0x0f0f0f0fu; right ^= t << 4u; left ^= t;

    const std::uint32_t hi = ((left & 255u) << 24u) | ((left & 0xff00u) << 8u) |
                             ((left & 0xff0000u) >> 8u) | ((left & 0xff000000u) >> 24u);
    const std::uint32_t lo = ((right & 255u) << 24u) | ((right & 0xff00u) << 8u) |
                             ((right & 0xff0000u) >> 8u) | ((right & 0xff000000u) >> 24u);
    for (unsigned int group = 1; group < 11; ++group) {
        const unsigned int bit = group * 6;
        unsigned int value;
        if (bit + 6 <= 32) {
            value = (hi >> (26 - bit)) & 63u;
        } else if (bit >= 32) {
            const unsigned int p = bit - 32;
            value = p + 6 <= 32 ? (lo >> (26 - p)) & 63u : (lo << (p - 26)) & 63u;
        } else {
            const unsigned int k = 32 - bit;
            value = (((hi & ((1u << k) - 1u)) << (6 - k)) | (lo >> (32 - (6 - k)))) & 63u;
        }
        output[group - 1] = static_cast<unsigned char>(COV_TABLE[value]);
    }
}

template <std::uint32_t CharsetLength>
__global__ void searchKernel(
    std::uint64_t base,
    std::uint64_t searchSpace,
    std::uint32_t batchSize,
    const unsigned char* charset,
    const unsigned char* targets,
    const unsigned char* targetLengths,
    std::uint32_t targetCount,
    int mode,
    bool ignoreCase,
    bool stopAfterFirst,
    int* found,
    Match* result) {
    __shared__ std::uint32_t sharedSPtrans[512];
    for (std::uint32_t index = threadIdx.x; index < 512; index += blockDim.x) {
        sharedSPtrans[index] = dSPtrans[index];
    }
    __syncthreads();
    const auto* spTable = sharedSPtrans;
    const std::uint32_t id = blockIdx.x * blockDim.x + threadIdx.x;
    if (id >= batchSize) return;

    std::uint64_t candidate = base + id;
    if (candidate >= searchSpace) candidate -= searchSpace;
    unsigned char key[kPasswordLength];
    for (int i = kPasswordLength - 1; i >= 0; --i) {
        key[i] = charset[candidate % CharsetLength];
        candidate /= CharsetLength;
    }

    unsigned char tripcode[kTripLength];
    makeTripcode(key, tripcode, dSaltMap, spTable);
    for (std::uint32_t targetIndex = 0; targetIndex < targetCount; ++targetIndex) {
        const unsigned int length = targetLengths[targetIndex];
        const unsigned int first = mode == 1 ? 0 : mode == 2 ? kTripLength - length : 0;
        const unsigned int last = mode == 1 || mode == 2 ? first : kTripLength - length;
        for (unsigned int position = first; position <= last; ++position) {
            bool matched = true;
            for (unsigned int j = 0; j < length; ++j) {
                unsigned char actual = tripcode[position + j];
                unsigned char expected = targets[targetIndex * kTripLength + j];
                if (ignoreCase) {
                    actual = actual >= 'A' && actual <= 'Z' ? actual + ('a' - 'A') : actual;
                    expected = expected >= 'A' && expected <= 'Z' ? expected + ('a' - 'A') : expected;
                }
                if (actual != expected) { matched = false; break; }
            }
            if (matched) {
                int resultIndex;
                if (stopAfterFirst) {
                    if (atomicCAS(found, 0, 1) != 0) return;
                    resultIndex = 0;
                } else {
                    resultIndex = atomicAdd(found, 1);
                }
                if (resultIndex < kMaxBatchResults) {
                    Match* match = &result[resultIndex];
                    for (int i = 0; i < kPasswordLength; ++i) match->password[i] = static_cast<char>(key[i]);
                    match->password[kPasswordLength] = '\0';
                    for (int i = 0; i < kTripLength; ++i) match->tripcode[i] = static_cast<char>(tripcode[i]);
                    match->tripcode[kTripLength] = '\0';
                    match->targetIndex = static_cast<int>(targetIndex);
                }
                return;
            }
        }
    }
}

template <std::uint32_t CharsetLength>
void launchSearchBatch(
    std::uint64_t base,
    std::uint64_t searchSpace,
    std::uint32_t batchSize,
    const unsigned char* charset,
    const unsigned char* targets,
    const unsigned char* targetLengths,
    std::uint32_t targetCount,
    int mode,
    bool ignoreCase,
    bool stopAfterFirst,
    int* found,
    Match* result) {
    const auto blocks = (batchSize + kThreadsPerBlock - 1) / kThreadsPerBlock;
    searchKernel<CharsetLength><<<blocks, kThreadsPerBlock>>>(
        base, searchSpace, batchSize, charset, targets, targetLengths,
        targetCount, mode, ignoreCase, stopAfterFirst, found, result);
}

bool matchesTargets(
    const unsigned char tripcode[kTripLength],
    const std::vector<std::string>& targets,
    int mode,
    bool ignoreCase,
    int& targetIndex) {
    for (std::size_t index = 0; index < targets.size(); ++index) {
        const auto& target = targets[index];
        const unsigned int length = static_cast<unsigned int>(target.size());
        const unsigned int first = mode == 1 ? 0 : mode == 2 ? kTripLength - length : 0;
        const unsigned int last = mode == 1 || mode == 2 ? first : kTripLength - length;
        for (unsigned int position = first; position <= last; ++position) {
            bool matched = true;
            for (unsigned int character = 0; character < length; ++character) {
                unsigned char actual = tripcode[position + character];
                unsigned char expected = static_cast<unsigned char>(target[character]);
                if (ignoreCase) {
                    actual = lowerAscii(actual);
                    expected = lowerAscii(expected);
                }
                if (actual != expected) {
                    matched = false;
                    break;
                }
            }
            if (matched) {
                targetIndex = static_cast<int>(index);
                return true;
            }
        }
    }
    return false;
}

} // namespace

int main(int argc, char** argv) {
    Options options = parseOptions(argc, argv);
    if (options.interactive) promptOptions(options);

    auto targets = splitTargets(options.targetText);
    if (targets.empty() || targets.size() > kMaxTargets) {
        std::cerr << "Enter between 1 and " << kMaxTargets << " search targets.\n";
        return EXIT_FAILURE;
    }
    if (options.mode != "any" && options.mode != "start" && options.mode != "end") {
        std::cerr << "Mode must be any, start, or end.\n";
        return EXIT_FAILURE;
    }
    if (options.charset != "an" && options.charset != "lower" && options.charset != "sym") {
        std::cerr << "Charset must be an, lower, or sym.\n";
        return EXIT_FAILURE;
    }
    if (options.compute != "gpu" && options.compute != "cpu" && options.compute != "both") {
        std::cerr << "Compute mode must be gpu, cpu, or both.\n";
        return EXIT_FAILURE;
    }
    for (auto& target : targets) {
        if (!validTarget(target)) {
            std::cerr << "Invalid target \"" << target << "\". Use tripcode characters . / 0-9 A-Z a-z (up to 10 chars).\n";
            return EXIT_FAILURE;
        }
        if (options.ignoreCase) {
            std::transform(target.begin(), target.end(), target.begin(), [](unsigned char ch) {
                return static_cast<char>(lowerAscii(ch));
            });
        }
        if (options.mode == "end" && !endsWithAllowedChar(target, options.ignoreCase)) {
            std::cerr << "Target \"" << target << "\" cannot end a tripcode. The last character must be one of .26AEIMQUYcgkosw.\n";
            return EXIT_FAILURE;
        }
    }

    const bool useGpu = options.compute != "cpu";
    const bool useCpu = options.compute != "gpu";
    int device = -1;
    cudaDeviceProp properties{};
    if (useGpu) {
        int deviceCount = 0;
        checkCuda(cudaGetDeviceCount(&deviceCount), "CUDA device query");
        if (deviceCount > 0) {
            device = 0;
            checkCuda(cudaGetDeviceProperties(&properties, device), "CUDA device properties");
        }
        if (device < 0) {
            std::cerr << "NVIDIA GPU not found. Select CPU-only mode or install the CUDA driver.\n";
            return EXIT_FAILURE;
        }
        checkCuda(cudaSetDevice(device), "Select GPU");
    }

    const std::string charset = options.charset == "lower"
        ? "abcdefghijklmnopqrstuvwxyz0123456789"
        : options.charset == "sym"
            ? "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!$%()*+,-./:;=?@[]^_{|}~"
            : "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    const std::uint32_t charsetLength = static_cast<std::uint32_t>(charset.size());
    std::vector<unsigned char> deviceTargets(kMaxTargets * kTripLength, 0);
    std::vector<unsigned char> targetLengths(kMaxTargets, 0);
    for (std::size_t i = 0; i < targets.size(); ++i) {
        targetLengths[i] = static_cast<unsigned char>(targets[i].size());
        std::copy(targets[i].begin(), targets[i].end(), deviceTargets.begin() + i * kTripLength);
    }

    std::array<unsigned char, 128> saltMap{};
    const std::string saltText =
        ".............................................../0123456789ABCDEF"
        "GABCDEFGHIJKLMNOPQRSTUVWXYZabcdefabcdefghijklmnopqrstuvwxyz.....";
    for (std::size_t i = 0; i < saltMap.size(); ++i) {
        saltMap[i] = i < saltText.size() ? static_cast<unsigned char>(saltText[i]) : static_cast<unsigned char>('.');
    }
    unsigned char* dCharset = nullptr;
    unsigned char* dTargets = nullptr;
    unsigned char* dTargetLengths = nullptr;
    int* dFound = nullptr;
    Match* dResults = nullptr;
    if (useGpu) {
        checkCuda(cudaMemcpyToSymbol(dSPtrans, hSPtrans, sizeof(hSPtrans)), "Upload SPtrans table");
        checkCuda(cudaMemcpyToSymbol(dSKB, hSKB, sizeof(hSKB)), "Upload SKB table");
        checkCuda(cudaMemcpyToSymbol(dConSalt, hConSalt, sizeof(hConSalt)), "Upload salt table");
        checkCuda(cudaMemcpyToSymbol(dShifts, hShifts, sizeof(hShifts)), "Upload DES shifts");
        checkCuda(cudaMemcpyToSymbol(dCov, hCov, sizeof(hCov)), "Upload output table");
        checkCuda(cudaMemcpyToSymbol(dSaltMap, saltMap.data(), saltMap.size()), "Upload salt map");

        checkCuda(cudaMalloc(&dCharset, charset.size()), "Allocate charset");
        checkCuda(cudaMalloc(&dTargets, deviceTargets.size()), "Allocate targets");
        checkCuda(cudaMalloc(&dTargetLengths, targetLengths.size()), "Allocate target lengths");
        checkCuda(cudaMalloc(&dFound, sizeof(int)), "Allocate match flag");
        checkCuda(cudaMalloc(&dResults, sizeof(Match) * kMaxBatchResults), "Allocate results");
        checkCuda(cudaMemcpy(dCharset, charset.data(), charset.size(), cudaMemcpyHostToDevice), "Upload charset");
        checkCuda(cudaMemcpy(dTargets, deviceTargets.data(), deviceTargets.size(), cudaMemcpyHostToDevice), "Upload targets");
        checkCuda(cudaMemcpy(dTargetLengths, targetLengths.data(), targetLengths.size(), cudaMemcpyHostToDevice), "Upload target lengths");
    }

    const std::uint64_t searchSpace = [] (std::uint32_t n) {
        std::uint64_t result = 1;
        for (int i = 0; i < kPasswordLength; ++i) result *= n;
        return result;
    }(charsetLength);
    std::mt19937_64 random(std::random_device{}());
    std::atomic<std::uint64_t> nextCandidate{random() % searchSpace};
    std::atomic<std::uint64_t> cpuTried{0};
    std::atomic<std::uint64_t> cpuHits{0};
    std::atomic<std::uint64_t> cpuReported{0};
    std::atomic<bool> firstHit{false};
    std::mutex cpuResultsMutex;
    std::deque<Match> cpuResults;
    std::vector<std::thread> workers;
    const int mode = options.mode == "start" ? 1 : options.mode == "end" ? 2 : 0;
    const auto initialGpuBlocks = useGpu
        ? static_cast<std::uint32_t>(std::min<std::uint64_t>(
            (options.batchSize + kThreadsPerBlock - 1) / kThreadsPerBlock,
            properties.maxGridSize[0]))
        : 0u;
    const auto gpuBatchSize = initialGpuBlocks * kThreadsPerBlock;
    gGpuBlocks.store(initialGpuBlocks, std::memory_order_relaxed);
    gCpuWorkers.store(useCpu ? options.cpuWorkers : 0, std::memory_order_relaxed);
    gMaxCpuWorkers = std::max(1u, std::thread::hardware_concurrency());
    gGpuEnabled = useGpu;
    gCpuEnabled = useCpu;

    SetConsoleCtrlHandler(handleConsoleControl, TRUE);
    if (useGpu) {
        std::cout << "CUDA device: " << properties.name << " (compute capability "
                  << properties.major << '.' << properties.minor << ", "
                  << properties.multiProcessorCount << " SMs)\n";
    }
    const char* computeName = options.compute == "gpu"
        ? "GPU only"
        : options.compute == "cpu" ? "CPU only" : "GPU + CPU";
    std::cout << "Compute: " << computeName
              << "; CUDA blocks/launch: " << (useGpu ? std::to_string(initialGpuBlocks) : "off")
              << "; CUDA threads/launch: " << (useGpu ? std::to_string(gpuBatchSize) : "off")
              << "; CPU workers: " << (useCpu ? std::to_string(options.cpuWorkers) : "off")
              << "; search mode: " << options.mode << "; charset: " << options.charset
              << "\nSearching. Press Ctrl+C to stop.\n";

    const auto started = std::chrono::steady_clock::now();
    auto nextReport = started + std::chrono::milliseconds(250);
    std::uint64_t gpuTried = 0;
    std::uint64_t gpuHits = 0;
    std::string pendingCommands;
    if (useCpu) {
        for (unsigned int worker = 0; worker < gMaxCpuWorkers; ++worker) {
            workers.emplace_back([&, worker] {
                while (!InterlockedCompareExchange(&gStopRequested, 0, 0) &&
                       (options.continueAfterHit || !firstHit.load(std::memory_order_relaxed))) {
                    if (worker >= gCpuWorkers.load(std::memory_order_relaxed)) {
                        std::this_thread::sleep_for(std::chrono::milliseconds(10));
                        continue;
                    }
                    const auto chunkBase = nextCandidate.fetch_add(kCpuChunkSize, std::memory_order_relaxed)
                        % searchSpace;
                    std::uint64_t completed = 0;
                    std::uint64_t chunkHits = 0;
                    bool reportedHit = false;
                    for (std::uint64_t offset = 0; offset < kCpuChunkSize; ++offset) {
                        if (InterlockedCompareExchange(&gStopRequested, 0, 0) ||
                            (!options.continueAfterHit && firstHit.load(std::memory_order_relaxed)) ||
                            worker >= gCpuWorkers.load(std::memory_order_relaxed)) break;
                        auto candidate = chunkBase + offset;
                        if (candidate >= searchSpace) candidate -= searchSpace;
                        unsigned char key[kPasswordLength];
                        for (int position = kPasswordLength - 1; position >= 0; --position) {
                            key[position] = static_cast<unsigned char>(
                                charset[candidate % charsetLength]);
                            candidate /= charsetLength;
                        }
                        unsigned char tripcode[kTripLength];
                        makeTripcode(key, tripcode, saltMap.data(), hSPtrans);
                        ++completed;

                        int targetIndex = -1;
                        if (matchesTargets(tripcode, targets, mode, options.ignoreCase, targetIndex)) {
                            ++chunkHits;
                            if (!reportedHit) {
                                Match result{};
                                std::copy_n(reinterpret_cast<const char*>(key), kPasswordLength, result.password);
                                std::copy_n(reinterpret_cast<const char*>(tripcode), kTripLength, result.tripcode);
                                result.targetIndex = targetIndex;
                                {
                                    std::lock_guard<std::mutex> lock(cpuResultsMutex);
                                    cpuResults.push_back(result);
                                }
                                cpuReported.fetch_add(1, std::memory_order_relaxed);
                                reportedHit = true;
                            }
                            firstHit.store(true, std::memory_order_relaxed);
                        }
                    }
                    cpuTried.fetch_add(completed, std::memory_order_relaxed);
                    cpuHits.fetch_add(chunkHits, std::memory_order_relaxed);
                    if (completed == 0) break;
                }
            });
        }
    }

    bool matched = false;
    std::uint64_t totalTried = 0;
    std::uint64_t totalHits = 0;
    while (!InterlockedCompareExchange(&gStopRequested, 0, 0) &&
           (options.continueAfterHit || !firstHit.load(std::memory_order_relaxed))) {
        processRuntimeCommands(pendingCommands);
        if (InterlockedCompareExchange(&gStopRequested, 0, 0)) break;
        if (useGpu) {
            const auto batch = gGpuBlocks.load(std::memory_order_relaxed) * kThreadsPerBlock;
            const auto base = nextCandidate.fetch_add(batch, std::memory_order_relaxed) % searchSpace;
            checkCuda(cudaMemset(dFound, 0, sizeof(int)), "Reset match flag");
            if (options.charset == "lower") {
                launchSearchBatch<36>(base, searchSpace, batch, dCharset, dTargets, dTargetLengths,
                    static_cast<std::uint32_t>(targets.size()), mode, options.ignoreCase,
                    !options.continueAfterHit, dFound, dResults);
            } else if (options.charset == "sym") {
                launchSearchBatch<86>(base, searchSpace, batch, dCharset, dTargets, dTargetLengths,
                    static_cast<std::uint32_t>(targets.size()), mode, options.ignoreCase,
                    !options.continueAfterHit, dFound, dResults);
            } else {
                launchSearchBatch<62>(base, searchSpace, batch, dCharset, dTargets, dTargetLengths,
                    static_cast<std::uint32_t>(targets.size()), mode, options.ignoreCase,
                    !options.continueAfterHit, dFound, dResults);
            }
            checkCuda(cudaGetLastError(), "Launch CUDA search kernel");

            int hostFound = 0;
            checkCuda(cudaMemcpy(&hostFound, dFound, sizeof(int), cudaMemcpyDeviceToHost), "Read match flag");
            gpuTried += batch;
            if (hostFound) {
                const int copiedResults = std::min(hostFound, kMaxBatchResults);
                std::array<Match, kMaxBatchResults> hostResults{};
                checkCuda(cudaMemcpy(hostResults.data(), dResults, sizeof(Match) * copiedResults,
                    cudaMemcpyDeviceToHost), "Read found results");
                matched = true;
                gpuHits += static_cast<std::uint64_t>(hostFound);
                if (!options.continueAfterHit) firstHit.store(true, std::memory_order_relaxed);
                for (int i = 0; i < copiedResults; ++i) {
                    const auto& result = hostResults[i];
                    std::cout << "HIT\t" << result.password << '\t' << result.tripcode
                              << '\t' << targets[result.targetIndex] << '\n';
                }
                if (hostFound > copiedResults) {
                    std::cout << "OVERFLOW\t" << (hostFound - copiedResults) << '\n';
                }
                std::cout << std::flush;
            }
        } else {
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
        }

        {
            std::lock_guard<std::mutex> lock(cpuResultsMutex);
            while (!cpuResults.empty()) {
                const auto result = cpuResults.front();
                cpuResults.pop_front();
                matched = true;
                std::cout << "HIT\t" << result.password << '\t' << result.tripcode
                          << '\t' << targets[result.targetIndex] << '\n' << std::flush;
            }
        }

        totalTried = gpuTried + cpuTried.load(std::memory_order_relaxed);
        totalHits = gpuHits + cpuHits.load(std::memory_order_relaxed);
        const auto now = std::chrono::steady_clock::now();
        if (now >= nextReport) {
            const double seconds = std::chrono::duration<double>(now - started).count();
            const double rate = seconds > 0 ? totalTried / seconds : 0;
            std::ostringstream progress;
            progress << "PROGRESS\t" << totalTried << '\t' << static_cast<std::uint64_t>(rate) << '\t' << totalHits
                     << '\t' << gpuTried << '\t' << cpuTried.load(std::memory_order_relaxed)
                     << '\n';
            std::cout << progress.str() << std::flush;
            nextReport = now + std::chrono::milliseconds(250);
        }
    }

    InterlockedExchange(&gStopRequested, 1);
    for (auto& worker : workers) worker.join();
    {
        std::lock_guard<std::mutex> lock(cpuResultsMutex);
        while (!cpuResults.empty()) {
            const auto result = cpuResults.front();
            cpuResults.pop_front();
            matched = true;
            std::cout << "HIT\t" << result.password << '\t' << result.tripcode
                      << '\t' << targets[result.targetIndex] << '\n';
        }
    }
    const auto omittedCpuHits = cpuHits.load(std::memory_order_relaxed) -
        cpuReported.load(std::memory_order_relaxed);
    if (omittedCpuHits > 0) std::cout << "OVERFLOW\t" << omittedCpuHits << '\n';
    std::cout << std::flush;
    totalTried = gpuTried + cpuTried.load(std::memory_order_relaxed);
    totalHits = gpuHits + cpuHits.load(std::memory_order_relaxed);
    const auto finished = std::chrono::steady_clock::now();
    const double seconds = std::chrono::duration<double>(finished - started).count();
    const double rate = seconds > 0 ? totalTried / seconds : 0;
    std::cout << "FINISHED\t" << (matched ? "hit" : "stopped") << '\t' << totalTried
              << '\t' << static_cast<std::uint64_t>(rate) << '\t' << seconds << '\t' << totalHits
              << '\t' << gpuTried << '\t' << cpuTried.load(std::memory_order_relaxed)
              << '\n' << std::flush;

    if (useGpu) {
        cudaFree(dResults);
        cudaFree(dFound);
        cudaFree(dTargetLengths);
        cudaFree(dTargets);
        cudaFree(dCharset);
    }
    SetConsoleCtrlHandler(handleConsoleControl, FALSE);
    if (options.interactive) {
        std::cout << (matched ? "Match found. Search finished." : "Search stopped.")
                  << "\nPress Enter to close this window." << std::flush;
        std::string close;
        std::getline(std::cin, close);
    }
    return EXIT_SUCCESS;
}
