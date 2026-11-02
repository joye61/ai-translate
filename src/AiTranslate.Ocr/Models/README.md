# Bundled offline OCR models

These are the unmodified ONNX models published by PaddlePaddle, distributed under
Apache-2.0. No WeChat binary, parameter file, dictionary, or model is included.
The exact repositories and immutable revisions are in manifest.json.

| File | Purpose | Size |
|---|---|---|
| det.onnx | PP-OCRv5 mobile text detection | about 4.8 MB |
| rec.onnx | PP-OCRv5 mobile Chinese/English text recognition | about 16.5 MB |
| keys.txt | Matching recognition dictionary | 18,383 entries |

The official model cards declare the Apache-2.0 license:

- https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det_onnx
- https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_rec_onnx

The paired inference configurations are retained as det.yml and rec.yml.
keys.txt is generated from `PostProcess.character_dict` in rec.yml in its original
order, encoded as UTF-8 without BOM, one entry per line and a final LF:

```python
import pathlib, yaml
config = yaml.safe_load(pathlib.Path("rec.yml").read_text(encoding="utf-8"))
chars = config["PostProcess"]["character_dict"]
pathlib.Path("keys.txt").write_bytes(("\n".join(chars) + "\n").encode("utf-8"))
```

The CTC decoder prepends blank and appends ASCII space, yielding 18,385 classes.
Do not substitute the old PP-OCRv4 dictionary or the v5 Latin dictionary.

Model contract and preprocessing:

- Detection: float NCHW BGR, height and width rounded to multiples of 32.
  Normalize each channel with `(byte/255 - mean)/std`,
  mean `[0.485,0.456,0.406]`, std `[0.229,0.224,0.225]`.
  The supplied config lists resize_long=960 and DB thresholds
  0.3 / 0.6, max_candidates=1000, unclip_ratio=1.5. The application selects its
  own bounded detection resolution and tile policy for screenshot small text.
- Recognition: float NCHW BGR, height 48 and proportional dynamic width.
  Normalize with `(byte/255 - 0.5)/0.5`; CTC greedy decoding removes blank and
  adjacent repeated labels. Recognition crops come from the original image.
- Model weights and the inference pipeline are shared across Windows and macOS.
  Numeric execution can vary slightly between CPU architectures.

ONNX Runtime 1.23.2 is pinned because its NuGet package includes native libraries
for win-x64, osx-arm64, and osx-x64. Later versions inspected in this work omitted
osx-x64. Windows native compatibility is verified locally; both macOS runtime
architectures still require execution on corresponding hardware/CI.

Sources for the reference processing pipeline:

- https://github.com/PaddlePaddle/PaddleX/tree/develop/paddlex/inference/models/text_detection
- https://github.com/PaddlePaddle/PaddleX/tree/develop/paddlex/inference/models/text_recognition
- https://onnxruntime.ai/docs/get-started/with-csharp.html
