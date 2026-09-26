# ppocrv4_det_backbone.onnx

Derived from the PaddleOCR PP-OCRv4 text-detection model (`ch_PP-OCRv4_det_infer.onnx`)
as distributed in the `rapidocr_onnxruntime` 1.4.4 wheel on PyPI.

- PaddleOCR: Copyright (c) PaddlePaddle Authors, Apache License 2.0 — https://github.com/PaddlePaddle/PaddleOCR
- RapidOCR: Apache License 2.0 — https://github.com/RapidAI/RapidOCR

Modification: the network was truncated to its backbone (the inputs of the detector's
feature-pyramid lateral convolutions `conv2d_469`/`conv2d_470`, i.e. the 1/4 and 1/8
resolution stages) by `tools/export_ppocr_backbone.py`. No weights were changed.
SHA-256: dfcac0b905b3ecb767bb9b8bc48dece751d16211556f60606bfc19c07269e2bf

Used only as a test asset for `OpenCvCnnPatchAnomalyDetector`.
