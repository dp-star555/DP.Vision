"""Agent-runnable converter regression: python -m unittest discover -s tools -p test_embed_ocr_dictionary.py"""
import tempfile
import unittest
from pathlib import Path

import onnx
from onnx import TensorProto, helper
from embed_ocr_dictionary import embed, characters_from_labels


class EmbedDictionaryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.model, self.keys, self.output = [self.root / name for name in ("raw.onnx", "keys.txt", "prepared.onnx")]
        graph = helper.make_graph([helper.make_node("Constant", [], ["result"], value=helper.make_tensor("probabilities", TensorProto.FLOAT, [1, 1, 5], [1, 0, 0, 0, 0]))],
            "fixture", [helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, 3, 48, "W"])],
            [helper.make_tensor_value_info("result", TensorProto.FLOAT, [1, 1, 5])])
        onnx.save(helper.make_model(graph, ir_version=9, opset_imports=[helper.make_opsetid("", 18)]), self.model)
        self.keys.write_text("#\n甲\n乙\n丙\n \n", encoding="utf-8")

    def tearDown(self):
        self.directory.cleanup()

    def test_ctc_labels_preserve_graph_and_source_and_are_idempotent(self):
        before = self.model.read_bytes()
        result = embed(self.model, self.keys, self.output, "ctc-labels")
        self.assertEqual((5, 3), (result["classes"], result["characters"]))
        self.assertEqual(before, self.model.read_bytes())
        original, prepared = onnx.load(self.model), onnx.load(self.output)
        self.assertEqual(original.graph.SerializeToString(), prepared.graph.SerializeToString())
        self.assertEqual("甲\n乙\n丙", {p.key: p.value for p in prepared.metadata_props}["character"])
        self.assertEqual(result, embed(self.model, self.keys, self.output, "ctc-labels"))

    def test_plain_characters_and_utf8_bom_crlf(self):
        self.keys.write_bytes("\ufeff甲\r\n乙\r\n丙\r\n".encode("utf-8"))
        self.assertEqual(5, embed(self.model, self.keys, self.output, "characters")["classes"])

    def test_wrong_dictionary_count_does_not_publish(self):
        self.keys.write_text("甲\n乙\n", encoding="utf-8")
        with self.assertRaises(ValueError):
            embed(self.model, self.keys, self.output, "characters")
        self.assertFalse(self.output.exists())

    def test_never_overwrites_source_or_different_output(self):
        with self.assertRaises(ValueError):
            embed(self.model, self.keys, self.model, "ctc-labels")
        self.output.write_bytes(b"keep")
        with self.assertRaises(FileExistsError):
            embed(self.model, self.keys, self.output, "ctc-labels")
        self.assertEqual(b"keep", self.output.read_bytes())

    def test_existing_different_embedded_dictionary_is_rejected(self):
        model = onnx.load(self.model)
        helper.set_model_props(model, {"character": "wrong"})
        onnx.save(model, self.model)
        with self.assertRaises(ValueError):
            embed(self.model, self.keys, self.output, "ctc-labels")
        self.assertFalse(self.output.exists())

    def test_blank_marker_and_space_are_not_guessed(self):
        with self.assertRaises(ValueError):
            characters_from_labels(["?", "甲", "乙", "丙", " "], 5, "ctc-labels")
        with self.assertRaises(ValueError):
            characters_from_labels(["#", "甲", "乙", "丙", "?"], 5, "ctc-labels")
        self.assertEqual("甲\n乙\n丙", characters_from_labels(["#", "甲", "乙", "丙", " "], 5, "ctc-labels"))


if __name__ == "__main__":
    unittest.main()
