"""Independently inspect the fixture emitted by PdfExporterTests.

Install PyMuPDF in a development environment, then run:
    python scripts/Verify-PdfOutput.py artifacts/dx04-pdf
This is a rendering/extraction check, not a PDF/A, PDF/UA or native-print validator.
"""
import argparse
import json
import math
from pathlib import Path
import re
import pymupdf as pdf


def blue_bounds(pixmap):
    samples = pixmap.samples
    points = []
    # The transparent image is in the table, below the hyperlink.
    for y in range(260, min(400, pixmap.height)):
        for x in range(30, min(140, pixmap.width)):
            offset = (y * pixmap.width + x) * pixmap.n
            r, g, b = samples[offset:offset + 3]
            if b > 100 and b > r + 40 and b > g + 20:
                points.append((x, y))
    assert points, "The embedded blue image must be visible."
    return [min(x for x, y in points), min(y for x, y in points),
            max(x for x, y in points), max(y for x, y in points)]


def verify(directory):
    document = pdf.open(directory / "fixture.pdf")
    assert len(document) == 2
    assert document.metadata["title"] == "DX-04 output fixture"
    assert document.metadata["author"] == "Textalonia"
    corpus = ["caf\u00e9", "\u0395\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac",
              "\u041f\u0440\u0438\u0432\u0435\u0442", "office", "e\u0301",
              "\u0627\u0644\u0639\u0631\u0628\u064a\u0629", "\u05e2\u05d1\u05e8\u05d9\u05ea"]
    texts = [page.get_text() for page in document]
    for expected in corpus:
        assert expected in texts[0], repr(expected)
    for index, page in enumerate(document):
        assert tuple(page.rect) == (0, 0, 360, 480)
        assert "Shared snapshot header" in texts[index]
        assert re.search(rf"Page\s+{index + 1}\s*/\s*2", texts[index]), texts[index]
        assert any(document.extract_font(font[0])[3] for font in page.get_fonts()), "Embedded font data is required."
        page.get_pixmap(matrix=pdf.Matrix(96 / 72, 96 / 72)).save(directory / f"pdf-page{index + 1}.png")
    links = document[0].get_links()
    assert len(links) == 1 and links[0]["uri"] == "https://example.com/report"
    assert document[0].rect.contains(links[0]["from"])
    images = document[0].get_images()
    assert len(images) == 1 and images[0][1] > 0, "The PNG must retain its transparency mask."
    assert "Clipped cell text wraps" in texts[0] and "cell." in texts[0]

    # Compare an independently extracted text origin to the exact snapshot geometry.
    geometry = (directory / "geometry.txt").read_text(encoding="utf-8")
    match = re.search(r"page=1 bounds=([0-9.]+), [^\n]+ baseline=([0-9.]+) text=Unicode", geometry)
    assert match
    spans = [span for block in document[0].get_text("dict")["blocks"] if "lines" in block
             for line in block["lines"] for span in line["spans"]]
    origin = next(span["origin"] for span in spans if span["text"].startswith("Unicode"))
    expected_origin = [float(match[1]) * .75, float(match[2]) * .75]
    assert all(math.isclose(actual, expected, abs_tol=.01) for actual, expected in zip(origin, expected_origin))

    # Preview and PDF independently decode/rasterize the same inline image at the same bounds.
    preview = pdf.Pixmap(str(directory / "preview-page1.png"))
    output = pdf.Pixmap(str(directory / "pdf-page1.png"))
    assert (preview.width, preview.height) == (output.width, output.height) == (480, 640)
    preview_blue, pdf_blue = blue_bounds(preview), blue_bounds(output)
    assert all(abs(a - b) <= 1 for a, b in zip(preview_blue, pdf_blue)), (preview_blue, pdf_blue)
    report = {
        "reader": "PyMuPDF " + pdf.VersionBind, "pages": len(document),
        "page_boxes_points": [list(page.rect) for page in document], "metadata": document.metadata,
        "extracted_text": texts, "first_origin_points": list(origin),
        "snapshot_origin_points": expected_origin, "uri": links[0]["uri"],
        "link_bounds_points": list(links[0]["from"]), "embedded_images": len(images),
        "preview_image_pixels": preview_blue, "pdf_image_pixels": pdf_blue,
        "limits": "No tagged reading-order, conformance-profile, or native-printer validation."
    }
    (directory / "inspection.json").write_text(json.dumps(report, ensure_ascii=True, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, nargs="?", default=Path("artifacts/dx04-pdf"))
    verify(parser.parse_args().directory)
