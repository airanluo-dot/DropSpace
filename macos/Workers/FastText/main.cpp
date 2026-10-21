// Private bounded inherited-pipe worker. No network, auto-download or default model.
#include "fasttext.h"
#include "nlohmann/json.hpp"
#include <CommonCrypto/CommonDigest.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#include <algorithm>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
using json = nlohmann::json;
constexpr size_t model_bytes = 131266198;
// A permitted 2 Mi UTF-16 lyric document can expand substantially when JSON
// escapes control characters. Keep the entire whole-track sample, bounded.
constexpr size_t max_frame = 16 * 1024 * 1024;
constexpr const char * model_hash = "7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e";
constexpr const char * engine_commit = "1142dc4c4ecbc19cc16eee5cdd28472e689267e6";

struct verified_model {
    int fd = -1;
    void * bytes = MAP_FAILED;
    explicit verified_model(const char * path) {
        fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        struct stat info{};
        if (fd < 0 || fstat(fd,&info) != 0 || !S_ISREG(info.st_mode) || info.st_size != model_bytes) {
            if (fd >= 0) close(fd);
            throw std::runtime_error("model-size-or-type");
        }
        bytes = mmap(nullptr,model_bytes,PROT_READ,MAP_PRIVATE,fd,0);
        if (bytes == MAP_FAILED) { close(fd); throw std::runtime_error("model-mapping"); }
        unsigned char digest[CC_SHA256_DIGEST_LENGTH];
        CC_SHA256(bytes,static_cast<CC_LONG>(model_bytes),digest);
        std::string hex; constexpr char alphabet[] = "0123456789abcdef";
        for (auto value : digest) { hex += alphabet[value >> 4]; hex += alphabet[value & 15]; }
        if (hex != model_hash) { munmap(bytes,model_bytes); close(fd); throw std::runtime_error("model-checksum"); }
    }
    ~verified_model() { if (bytes != MAP_FAILED) munmap(bytes,model_bytes); if (fd >= 0) close(fd); }
    verified_model(const verified_model &) = delete;
};
class model_buffer : public std::streambuf {
public:
    explicit model_buffer(verified_model & model) {
        char * data = static_cast<char *>(model.bytes); setg(data,data,data + model_bytes);
    }
protected:
    pos_type seekoff(off_type offset,std::ios_base::seekdir direction,std::ios_base::openmode mode) override {
        if (!(mode & std::ios_base::in)) return pos_type(off_type(-1));
        auto base = direction == std::ios_base::beg ? 0 : direction == std::ios_base::cur ? gptr() - eback() : egptr() - eback();
        auto target = base + offset;
        if (target < 0 || target > egptr() - eback()) return pos_type(off_type(-1));
        setg(eback(),eback() + target,egptr()); return pos_type(target);
    }
    pos_type seekpos(pos_type position,std::ios_base::openmode mode) override { return seekoff(off_type(position),std::ios_base::beg,mode); }
};
class verified_fasttext final : public fasttext::FastText {
public:
    void loadVerified(std::istream & input) {
        // The upstream stream overload starts *after* its magic/version header.
        // Use its protected header reader, just as the filename overload does,
        // while retaining the already-hashed mapping and open-file identity.
        if (!checkModel(input)) throw std::runtime_error("model-header");
        loadModel(input);
    }
};
static bool read_frame(std::string & frame) {
    frame.clear(); char c;
    while (std::cin.get(c)) {
        if (c == '\n') return true;
        if (frame.size() >= max_frame) throw std::runtime_error("frame-budget");
        frame += c;
    }
    if (!frame.empty()) throw std::runtime_error("incomplete-frame");
    return false;
}
static void send(const json & message) { std::cout << message.dump() << '\n' << std::flush; }
int main(int argc,char ** argv) {
    try {
        if (argc == 2 && std::string(argv[1]) == "--version") {
            send({{"protocol",1},{"engineCommit",engine_commit},{"modelSha256",model_hash},{"platform","macos"},{"architecture","arm64"}}); return 0;
        }
        if (argc != 3 || std::string(argv[1]) != "--model") return 64;
        verified_model verified(argv[2]); model_buffer buffer(verified); std::istream model_input(&buffer);
        verified_fasttext model; model.loadVerified(model_input);
        send({{"type","ready"},{"protocol",1},{"modelSha256",model_hash},{"engineCommit",engine_commit}});
        std::string frame;
        while (read_frame(frame)) {
            auto request = json::parse(frame);
            if (request.value("type","") == "shutdown") return 0;
            if (request.value("type","") != "predict" || !request.contains("id") || !request["id"].is_string() ||
                !request.contains("text") || !request["text"].is_string()) throw std::runtime_error("invalid-request");
            const auto id = request["id"].get<std::string>(); auto sample = request["text"].get<std::string>();
            if (id.empty() || id.size() > 128 || sample.empty() || sample.find('\0') != std::string::npos ||
                sample.find_first_of("\r\n") != std::string::npos) throw std::runtime_error("invalid-sample");
            // The host supplies original-space-sample-v1; do not silently truncate or reinterpret it.
            std::istringstream input(sample + "\n"); std::vector<std::pair<fasttext::real,std::string>> predictions;
            model.predictLine(input,predictions,2,0.0);
            json output = json::array();
            for (const auto & prediction : predictions) output.push_back({{"label",prediction.second},{"score",prediction.first}});
            send({{"type","prediction"},{"id",id},{"modelSha256",model_hash},{"predictions",output}});
        }
        return 0;
    } catch (const std::exception &) {
        // Never echo a model path, lyric text, or malformed input back into diagnostics.
        std::cerr << "language-worker: validation or engine failure\n"; return 67;
    }
}
