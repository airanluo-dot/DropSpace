"""Private one-shot CT2 protocol. This module has no third-party imports at load time."""
import argparse
import json
import os
import re
import stat
import sys

MAX_BYTES = 256 * 1024  # JSON bytes; the request's final LF is framing.
MAX_LINES = 2048
MAX_LINE_BYTES = 4096
MAX_INPUT_TOKENS = 512
MAX_OUTPUT_TOKENS = 512
MAX_TOTAL_TOKENS = 16384
MAX_TOKENIZER_BYTES = 64 * 1024 * 1024
BATCH_SIZE = 16
ROUTES = {("ja", "en"), ("ko", "en"), ("zh", "en"), ("en", "zh"), ("ja", "zh"), ("ko", "zh")}
PROTOCOLS = {("ArgosSentencePiece", "Argos"), ("HelsinkiSentencePiece", "HelsinkiOpus")}
REQUEST_KEYS = {"version", "source", "target", "modelDirectory", "sourceTokenizer", "targetTokenizer",
                "targetPrefix", "tokenizerProtocol", "decoderProtocol", "lines"}
LINE_KEYS = {"id", "text"}
EOS = "</s>"


class ProtocolError(ValueError):
    """Only fixed, non-user-controlled diagnostics cross the stderr boundary."""


def require(condition, message):
    if not condition:
        raise ProtocolError(message)


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "duplicate JSON member")
        value[key] = item
    return value


def reject_constant(_):
    raise ProtocolError("non-finite JSON value")


def checked_text(value, message="invalid line text"):
    require(type(value) is str and bool(value.strip()), message)
    require(not any(ord(char) < 32 and char != "\t" for char in value), message)
    try:
        size = len(value.encode("utf-8", errors="strict"))
    except UnicodeError as error:
        raise ProtocolError(message) from error
    require(size <= MAX_LINE_BYTES, message)
    return value


def checked_path(value, *, directory):
    require(type(value) is str and 0 < len(value) <= 1024 and "\x00" not in value, "invalid package path")
    require(os.path.isabs(value) and os.path.normpath(value) == value, "package path must be canonical and absolute")
    # UNC/device namespaces and alternate data streams are outside the local package contract.
    if os.name == "nt":
        require(not value.startswith(("\\\\", "//")) and ":" not in value[2:], "invalid local package path")
    try:
        current = value
        while True:
            info = os.lstat(current)
            require(not stat.S_ISLNK(info.st_mode) and
                    not getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
                    "reparse package path")
            parent = os.path.dirname(current)
            if parent == current:
                break
            current = parent
        info = os.stat(value)
        if directory:
            require(stat.S_ISDIR(info.st_mode), "model directory missing")
        else:
            require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= MAX_TOKENIZER_BYTES, "invalid tokenizer file")
    except (OSError, ValueError) as error:
        if isinstance(error, ProtocolError):
            raise
        raise ProtocolError("package path unavailable") from error
    return value


def validate_request(request):
    require(type(request) is dict and set(request) == REQUEST_KEYS, "invalid request schema")
    require(type(request["version"]) is int and request["version"] == 1, "invalid protocol version")
    source, target = request["source"], request["target"]
    require(type(source) is str and type(target) is str and (source, target) in ROUTES, "invalid language route")
    tokenizer, decoder = request["tokenizerProtocol"], request["decoderProtocol"]
    require(type(tokenizer) is str and type(decoder) is str and (tokenizer, decoder) in PROTOCOLS,
            "unsupported tokenizer/decoder pair")
    prefix = request["targetPrefix"]
    require(prefix is None or type(prefix) is str and re.fullmatch(r">>[A-Za-z][A-Za-z0-9_-]{0,59}<<", prefix),
            "invalid target prefix")
    require(decoder != "Argos" or prefix is None, "Argos target prefix must be null")
    lines = request["lines"]
    require(type(lines) is list and 0 < len(lines) <= MAX_LINES, "invalid line count")
    ids = set()
    for line in lines:
        require(type(line) is dict and set(line) == LINE_KEYS, "invalid line schema")
        identifier = line["id"]
        require(type(identifier) is int and 0 <= identifier <= 2147483647, "invalid line ID")
        require(identifier not in ids, "duplicate IDs")
        ids.add(identifier)
        checked_text(line["text"])
    checked_path(request["modelDirectory"], directory=True)
    checked_path(request["sourceTokenizer"], directory=False)
    checked_path(request["targetTokenizer"], directory=False)
    return request


def read_request(stream=None):
    stream = sys.stdin.buffer if stream is None else stream
    # A single bounded line plus EOF: trailing spaces/newlines are a second frame, not ignored.
    raw = stream.readline(MAX_BYTES + 2)
    require(bool(raw) and raw.endswith(b"\n") and len(raw) - 1 <= MAX_BYTES and
            not raw.endswith(b"\r\n"), "invalid bounded request")
    require(not stream.read(1), "multiple requests are not supported")
    try:
        request = json.loads(raw[:-1].decode("utf-8", errors="strict"),
                             object_pairs_hook=unique_object, parse_constant=reject_constant)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise ProtocolError("invalid JSON") from error
    return validate_request(request)


def checked_tokens(tokens, maximum):
    require(type(tokens) is list and 0 < len(tokens) <= maximum and
            all(type(piece) is str and 0 < len(piece) <= MAX_LINE_BYTES for piece in tokens),
            "invalid token count or value")
    return tokens


def translate(request):
    # Never import native code until all schema, type, route and local path checks have passed.
    validate_request(request)
    import ctranslate2
    import sentencepiece

    source = sentencepiece.SentencePieceProcessor(model_file=request["sourceTokenizer"])
    target = sentencepiece.SentencePieceProcessor(model_file=request["targetTokenizer"])
    encoded = []
    input_count = 0
    for line in request["lines"]:
        pieces = checked_tokens(source.encode(line["text"], out_type=str), MAX_INPUT_TOKENS)
        if request["decoderProtocol"] == "HelsinkiOpus":
            pieces = ([request["targetPrefix"]] if request["targetPrefix"] is not None else []) + pieces + [EOS]
        checked_tokens(pieces, MAX_INPUT_TOKENS)
        input_count += len(pieces)
        require(input_count <= MAX_TOTAL_TOKENS, "input token budget exceeded")
        encoded.append(pieces)

    translator = ctranslate2.Translator(request["modelDirectory"], device="cpu", compute_type="int8",
                                       inter_threads=1, intra_threads=4)
    output = []
    output_count = 0
    for offset in range(0, len(encoded), BATCH_SIZE):
        batch = encoded[offset:offset + BATCH_SIZE]
        results = translator.translate_batch(batch, beam_size=1, max_batch_size=BATCH_SIZE,
                                             max_input_length=0, max_decoding_length=MAX_OUTPUT_TOKENS,
                                             return_end_token=True, return_scores=False)
        require(type(results) is list and len(results) == len(batch), "incomplete translation batch")
        for line, result in zip(request["lines"][offset:offset + BATCH_SIZE], results, strict=True):
            hypotheses = result.hypotheses
            require(type(hypotheses) is list and len(hypotheses) == 1, "invalid translation hypothesis")
            pieces = checked_tokens(hypotheses[0], MAX_OUTPUT_TOKENS)
            # Reject a length-limited partial hypothesis instead of publishing truncated lyrics.
            require(pieces[-1] == EOS and EOS not in pieces[:-1], "translation missing terminal EOS")
            output_count += len(pieces)
            require(output_count <= MAX_TOTAL_TOKENS, "output token budget exceeded")
            text = checked_text(target.decode(pieces[:-1]), "invalid translated text").strip()
            output.append({"id": line["id"], "text": text})
        encode_response(output)  # Fail before scheduling another batch if the byte budget is exhausted.
    return output


def encode_response(lines):
    require(type(lines) is list and 0 < len(lines) <= MAX_LINES, "invalid response line count")
    identifiers = set()
    for line in lines:
        require(type(line) is dict and set(line) == LINE_KEYS, "invalid response line")
        identifier = line["id"]
        require(type(identifier) is int and 0 <= identifier <= 2147483647 and identifier not in identifiers,
                "invalid response ID")
        identifiers.add(identifier)
        checked_text(line["text"], "invalid translated text")
    response = json.dumps({"version": 1, "lines": lines}, ensure_ascii=False,
                          allow_nan=False, separators=(",", ":")).encode("utf-8", errors="strict")
    require(len(response) <= MAX_BYTES, "response too large")
    return response


def isolate_protocol_output():
    """Keep a private pipe fd, redirect both CRT fd 1 and the Win32 stdout handle."""
    sys.stdout.flush()
    protocol_fd = os.dup(1)
    os.set_inheritable(protocol_fd, False)
    try:
        os.dup2(2, 1)
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.GetStdHandle.argtypes = [wintypes.DWORD]
            kernel.GetStdHandle.restype = wintypes.HANDLE
            kernel.SetStdHandle.argtypes = [wintypes.DWORD, wintypes.HANDLE]
            kernel.SetStdHandle.restype = wintypes.BOOL
            stderr = kernel.GetStdHandle(wintypes.DWORD(-12))
            require(stderr not in (None, 0, ctypes.c_void_p(-1).value) and
                    kernel.SetStdHandle(wintypes.DWORD(-11), stderr), "stdout isolation failed")
        return protocol_fd
    except BaseException:
        os.close(protocol_fd)
        raise


def main():
    protocol_fd = None
    try:
        protocol_fd = isolate_protocol_output()
        parser = argparse.ArgumentParser(add_help=False, allow_abbrev=False)
        parser.add_argument("--stdio-once", action="store_true")
        args = parser.parse_args()
        require(args.stdio_once and sys.argv[1:] == ["--stdio-once"], "stdio mode required")
        response = encode_response(translate(read_request()))
        # os.write can be short even for a pipe. Never restore stdout: native atexit logs stay on stderr.
        offset = 0
        while offset < len(response):
            written = os.write(protocol_fd, response[offset:])
            require(written > 0, "protocol write failed")
            offset += written
        return 0
    except ProtocolError as error:
        print(str(error), file=sys.stderr, flush=True)
        return 2
    except Exception:
        print("CT2 helper failed", file=sys.stderr, flush=True)
        return 2
    finally:
        if protocol_fd is not None:
            os.close(protocol_fd)


if __name__ == "__main__":
    sys.exit(main())
