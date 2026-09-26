"""Export the CNN backbone used by OpenCvCnnPatchAnomalyDetector.

Source: PaddleOCR PP-OCRv4 text-detection model as bundled in the
rapidocr_onnxruntime wheel on PyPI (Apache-2.0). The backbone (the part that
feeds the detector's feature pyramid) is cut out at its 1/4 and 1/8
resolution stages, giving a ~110 KB ONNX model with two outputs.

Usage:
    pip install onnx onnxruntime
    python export_ppocr_backbone.py [output.onnx] [rapidocr_onnxruntime.whl]

Without a wheel argument the script runs `pip download` for
rapidocr_onnxruntime==1.4.4. The exported file is verified with ONNX Runtime
and its SHA-256 printed; anomaly models record that hash, so keep the same
file for training and inspection.
"""

import glob
import hashlib
import subprocess
import sys
import tempfile
import zipfile

import numpy as np
import onnx
import onnxruntime as ort

DETECTOR = "rapidocr_onnxruntime/models/ch_PP-OCRv4_det_infer.onnx"
# The feature-pyramid lateral convolutions read the backbone stage outputs.
LATERALS = ["conv2d_469.tmp_0", "conv2d_470.tmp_0"]


def main():
    output = sys.argv[1] if len(sys.argv) > 1 else "ppocrv4_det_backbone.onnx"
    with tempfile.TemporaryDirectory() as work:
        wheel = sys.argv[2] if len(sys.argv) > 2 else None
        if wheel is None:
            subprocess.check_call(
                [sys.executable, "-m", "pip", "download", "--no-deps", "rapidocr_onnxruntime==1.4.4", "-d", work]
            )
            wheel = glob.glob(f"{work}/*.whl")[0]
        detector = f"{work}/det.onnx"
        with zipfile.ZipFile(wheel) as z, open(detector, "wb") as f:
            f.write(z.read(DETECTOR))

        model = onnx.load(detector)
        producers = {n.output[0]: n for n in model.graph.node}
        stages = [producers[name].input[0] for name in LATERALS]
        onnx.utils.extract_model(detector, output, [model.graph.input[0].name], stages)

    backbone = onnx.load(output)
    onnx.checker.check_model(backbone)
    session = ort.InferenceSession(output, providers=["CPUExecutionProvider"])
    fine, coarse = session.run(None, {session.get_inputs()[0].name: np.random.rand(1, 3, 64, 256).astype(np.float32)})
    assert fine.shape[2:] == (16, 64) and coarse.shape[2:] == (8, 32), (fine.shape, coarse.shape)
    digest = hashlib.sha256(open(output, "rb").read()).hexdigest()
    print(f"{output}: outputs {fine.shape} {coarse.shape}, sha256 {digest}")


if __name__ == "__main__":
    main()
