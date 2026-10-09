"""Create a new recognition ONNX with the explicit matching dictionary embedded.

Requires: pip install onnx onnxruntime
Usage:
  python embed_ocr_dictionary.py input.onnx keys.txt output.onnx --format ctc-labels

ctc-labels: dictionary already includes index-0 '#' blank placeholder and final
ASCII space, and its length must equal output classes. characters: dictionary
contains only real characters; DP.Vision adds blank and space. No guessing, no
implicit sidecar loading, no weight changes, and no overwriting existing files.
Class count alone cannot prove provenance: supply the dictionary used to train
or export this model, not an arbitrary same-length character list.
"""

import argparse
import hashlib
from pathlib import Path

import onnx
import onnxruntime as ort


def session(data):
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 2
    options.log_severity_level = 3
    return ort.InferenceSession(data, sess_options=options, providers=["CPUExecutionProvider"])


def characters_from_labels(labels, classes, format_name):
    if not labels or any(not token or "\r" in token or "\n" in token for token in labels):
        raise ValueError("Dictionary contains empty or invalid lines.")
    if format_name == "ctc-labels":
        if len(labels) != classes or labels[0] != "#" or labels[-1] != " ":
            raise ValueError("CTC labels must match output classes and start with '#' blank / end with ASCII space.")
        characters = labels[1:-1]
    elif format_name == "characters":
        if len(labels) + 2 != classes or " " in labels:
            raise ValueError("Character-only dictionary requires output classes = characters + blank + space.")
        characters = labels
    else:
        raise ValueError("Unknown dictionary format.")
    if not characters:
        raise ValueError("Dictionary has no real characters.")
    return "\n".join(characters)


def embed(source, dictionary, output, format_name):
    source, dictionary, output = map(Path, (source, dictionary, output))
    if source.resolve() == output.resolve():
        raise ValueError("Use a new output path; the source model must remain unchanged.")
    if not 0 < source.stat().st_size <= 256 * 1024 * 1024:
        raise ValueError("Model exceeds 256MB budget.")
    if not 0 < dictionary.stat().st_size <= 1024 * 1024:
        raise ValueError("Dictionary exceeds 1MB budget.")
    original = source.read_bytes()
    original_session = session(original)
    inputs, outputs = original_session.get_inputs(), original_session.get_outputs()
    if len(inputs) != 1 or len(outputs) != 1:
        raise ValueError("Expected one recognition input/output.")
    shape = inputs[0].shape
    if (inputs[0].type != "tensor(float)" or len(shape) != 4 or shape[1] != 3
            or (isinstance(shape[0], int) and shape[0] != 1)
            or (isinstance(shape[2], int) and shape[2] != 48)
            or isinstance(shape[3], int)):
        raise ValueError("Expected float NCHW recognition input with dynamic width and height 48.")
    if (outputs[0].type != "tensor(float)" or len(outputs[0].shape) != 3
            or not isinstance(outputs[0].shape[2], int)):
        raise ValueError("Expected float recognition output with fixed class count.")
    classes = outputs[0].shape[2]
    labels = dictionary.read_text(encoding="utf-8-sig").split("\n")
    labels = [line.removesuffix("\r") for line in labels]
    if labels[-1] == "":
        labels.pop()  # A final line terminator is not an empty character.
    characters = characters_from_labels(labels, classes, format_name)
    existing = original_session.get_modelmeta().custom_metadata_map.get("character")
    if existing is not None and existing != characters:
        raise ValueError("Existing embedded dictionary differs; refusing to replace it.")
    del original_session
    model = onnx.load_model_from_string(original)
    graph_before = model.graph.SerializeToString()
    properties = {entry.key: entry.value for entry in model.metadata_props}
    properties["character"] = characters
    onnx.helper.set_model_props(model, properties)
    onnx.checker.check_model(model)
    if model.graph.SerializeToString() != graph_before:
        raise AssertionError("Model graph/weights changed.")
    prepared = model.SerializeToString()
    if len(prepared) > 256 * 1024 * 1024:
        raise ValueError("Prepared model exceeds 256MB budget.")
    verified = session(prepared)
    if verified.get_modelmeta().custom_metadata_map.get("character") != characters:
        raise AssertionError("Embedded dictionary verification failed.")
    if len(characters.split("\n")) + 2 != verified.get_outputs()[0].shape[2]:
        raise AssertionError("Dictionary/output class mismatch.")
    del verified
    if output.exists():
        if output.read_bytes() != prepared:
            raise FileExistsError("Output already exists with different content; choose a new path.")
    else:
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("xb") as stream:
            stream.write(prepared)
    if source.read_bytes() != original:
        raise AssertionError("Source model changed during conversion.")
    return {"output": str(output.resolve()), "classes": classes, "characters": len(characters.split("\n")),
            "source_sha256": hashlib.sha256(original).hexdigest(), "sha256": hashlib.sha256(prepared).hexdigest(),
            "dictionary_sha256": hashlib.sha256(dictionary.read_bytes()).hexdigest(), "graph_unchanged": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model")
    parser.add_argument("dictionary")
    parser.add_argument("output")
    parser.add_argument("--format", required=True, choices=["ctc-labels", "characters"])
    args = parser.parse_args()
    import json
    print(json.dumps(embed(args.model, args.dictionary, args.output, args.format), indent=2, ensure_ascii=True))


if __name__ == "__main__":
    main()
