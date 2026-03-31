"""
Background Removal Python Sidecar Server
Runs as a local Flask API alongside the .NET app.
Provides access to PyTorch-based background removal models.

Usage: python bg_server.py [--port 5100]
"""
import io
import os
import sys
import time
import logging
import argparse
import base64
from flask import Flask, request, jsonify, send_file

import torch
import numpy as np
from PIL import Image

logging.basicConfig(level=logging.INFO, format='%(asctime)s [%(levelname)s] %(message)s')
logger = logging.getLogger(__name__)

app = Flask(__name__)

# ── Model cache ──
_models = {}
_device = "cuda" if torch.cuda.is_available() else "cpu"
logger.info(f"Using device: {_device}")


def get_bria_rmbg():
    """Load BRIA RMBG 1.4 from HuggingFace (non-gated, free access)."""
    if "bria" not in _models:
        logger.info("Loading BRIA RMBG 1.4 model from HuggingFace...")
        from transformers import AutoModelForImageSegmentation
        model = AutoModelForImageSegmentation.from_pretrained(
            "briaai/RMBG-1.4", trust_remote_code=True
        )
        model.to(_device)
        model.eval()
        _models["bria"] = model
        logger.info("BRIA RMBG 1.4 loaded.")
    return _models["bria"]


def get_birefnet():
    """Load BiRefNet-General from HuggingFace (non-gated)."""
    if "birefnet" not in _models:
        logger.info("Loading BiRefNet model from HuggingFace...")
        from transformers import AutoModelForImageSegmentation
        model = AutoModelForImageSegmentation.from_pretrained(
            "ZhengPeng7/BiRefNet", trust_remote_code=True
        )
        model.to(_device)
        model.eval()
        _models["birefnet"] = model
        logger.info("BiRefNet loaded.")
    return _models["birefnet"]


# ── Processing functions ──

def extract_mask(preds, input_size):
    """Extract the best mask from model output (handles various output formats).
    Both BRIA and BiRefNet return multi-scale outputs; the last element is the
    most refined mask — matching their official usage: model(x)[-1].sigmoid()
    """
    if isinstance(preds, (list, tuple)):
        # Use the last element — the most refined segmentation mask
        last = preds[-1]
        if isinstance(last, (list, tuple)):
            mask_tensor = last[-1]
        else:
            mask_tensor = last
    else:
        mask_tensor = preds

    pred = torch.sigmoid(mask_tensor[0, 0])
    return pred


def process_with_bria(image: Image.Image) -> Image.Image:
    """Remove background using BRIA RMBG 1.4."""
    from torchvision import transforms
    model = get_bria_rmbg()

    orig_size = image.size  # (W, H)
    input_size = (1024, 1024)

    transform = transforms.Compose([
        transforms.Resize(input_size),
        transforms.ToTensor(),
        transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
    ])

    input_tensor = transform(image.convert("RGB")).unsqueeze(0).to(_device)

    with torch.no_grad():
        preds = model(input_tensor)
        pred = extract_mask(preds, input_size)

    # Resize mask back to original size
    mask = pred.cpu().numpy()
    mask = (mask * 255).astype(np.uint8)
    mask_img = Image.fromarray(mask).resize(orig_size, Image.LANCZOS)

    # Apply mask
    result = image.convert("RGBA")
    result.putalpha(mask_img)
    return result


def process_with_birefnet(image: Image.Image) -> Image.Image:
    """Remove background using BiRefNet."""
    from torchvision import transforms
    model = get_birefnet()

    orig_size = image.size
    input_size = (1024, 1024)

    transform = transforms.Compose([
        transforms.Resize(input_size),
        transforms.ToTensor(),
        transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
    ])

    input_tensor = transform(image.convert("RGB")).unsqueeze(0).to(_device)

    with torch.no_grad():
        preds = model(input_tensor)
        pred = extract_mask(preds, input_size)

    mask = pred.cpu().numpy()
    mask = (mask * 255).astype(np.uint8)
    mask_img = Image.fromarray(mask).resize(orig_size, Image.LANCZOS)

    result = image.convert("RGBA")
    result.putalpha(mask_img)
    return result


# ── API endpoints ──

@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok", "device": _device, "models_loaded": list(_models.keys())})


@app.route("/remove-bg", methods=["POST"])
def remove_bg():
    """
    Remove background from an image.
    POST body: multipart/form-data with 'image' file and 'method' field.
    Methods: inspyrenet, bria, birefnet
    Returns: PNG image with transparent background.
    """
    start = time.time()

    if "image" not in request.files:
        return jsonify({"error": "No image file provided"}), 400

    method = request.form.get("method", "bria")
    image_file = request.files["image"]

    try:
        image = Image.open(image_file.stream).convert("RGB")
        logger.info(f"Processing {image.size[0]}x{image.size[1]} image with method: {method}")

        if method == "birefnet":
            result = process_with_birefnet(image)
        elif method == "bria":
            result = process_with_bria(image)
        else:
            return jsonify({"error": f"Unknown method: {method}"}), 400

        # Convert to PNG bytes
        buf = io.BytesIO()
        result.save(buf, format="PNG")
        buf.seek(0)

        elapsed = time.time() - start
        logger.info(f"Done: {method} in {elapsed:.2f}s")

        return send_file(buf, mimetype="image/png",
                        download_name=f"bg_removed_{method}.png")

    except Exception as e:
        logger.error(f"Error processing image: {e}", exc_info=True)
        return jsonify({"error": str(e)}), 500


@app.route("/models", methods=["GET"])
def list_models():
    """List available models and their status."""
    return jsonify({
        "available": [
            {"id": "bria", "name": "BRIA RMBG 2.0", "loaded": "bria" in _models,
             "description": "Best quality, state-of-the-art. HuggingFace model."},
            {"id": "birefnet", "name": "BiRefNet", "loaded": "birefnet" in _models,
             "description": "Bilateral Reference Network. Excellent edge quality."},
        ],
        "device": _device
    })


@app.route("/preload", methods=["POST"])
def preload():
    """Pre-load a model to avoid first-request latency."""
    method = request.json.get("method", "bria") if request.is_json else "bria"
    try:
        if method == "bria":
            get_bria_rmbg()
        elif method == "birefnet":
            get_birefnet()
        return jsonify({"status": "loaded", "method": method})
    except Exception as e:
        return jsonify({"error": str(e)}), 500


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="BG Removal Python Sidecar")
    parser.add_argument("--port", type=int, default=5100, help="Port to run on")
    parser.add_argument("--host", default="127.0.0.1", help="Host to bind to")
    args = parser.parse_args()

    logger.info(f"Starting BG Removal server on {args.host}:{args.port}")
    app.run(host=args.host, port=args.port, debug=False, threaded=True)
