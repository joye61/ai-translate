# RapidOcrNet source provenance

This directory vendors the C# sources from RapidOcrNet 4.2.0, commit
`708cae2fcb88720e1d891a81b5ee3e8b2bcc139e`, the repository commit recorded in the
published NuGet package. Upstream:
https://github.com/BobLd/RapidOcrNet/tree/708cae2fcb88720e1d891a81b5ee3e8b2bcc139e

Apache-2.0 license and upstream notices are retained in LICENSE.txt and NOTICE.txt.
The source includes contour detection, polygon offset/cropping, BGR tensor
conversion, CTC decoding, and cancellable ONNX execution. X Capture uses the
detector and recognizer directly; the upstream convenience pipeline, classifier,
and v6 presets are retained for source traceability but are not instantiated.

Local modifications:

- Unroll the three BGR/RGB normalization channels and reuse channel spans and
  constants. Preserve per-channel floating-point operations, row padding, gray
  inputs, alpha handling and network sampling; bit-exact tensor regressions cover
  both detector and recognizer inputs.

- Initialize fields populated by InitModel with `null!`, and PContour's populated
  points field with `null!`, to satisfy the repository's warnings-as-errors build.
- TextDetector.GetTextBoxes and TextRecognizer.GetTextLine propagate failures as
  InvalidOperationException with the original inner exception. An inference
  failure must not appear as successful recognition with no text.
- Detector postprocessing checks cancellation during thresholding, around contour
  extraction, and between polygons, and caps polygon processing at the official
  configuration's 1,000 candidates. The contour scan itself remains bounded by
  the application's detection image dimensions.
- Build against ONNX Runtime 1.23.2 instead of upstream 1.29.0 to retain native
  libraries for both macOS x64 and arm64 as well as Windows x64. The per-request
  RunOptions.Terminate API is supported by this pinned runtime.
- Build against the application's SkiaSharp 3.119.4 rather than 3.119.1.

No upstream model is taken from the NuGet package. The two Chinese models and
their dictionary are separately attributed in ../../Models/README.md.
