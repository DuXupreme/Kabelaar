"""Anti-aliased schermweergave op basis van de bestaande Pillow-paginarenderer.

De paginarenderer blijft de enige bron voor scherm-, PNG- en PDF-uitvoer. Voor
het scherm wordt de pagina op een hogere resolutie gerenderd, tot de zichtbare
viewport uitgesneden en met LANCZOS teruggeschaald. De bronafbeelding wordt
gecachet, zodat pannen, selecteren en interactie slechts een goedkope image-blit
nodig hebben.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Callable, Optional

from PIL import Image


@dataclass
class PageImageCache:
    """Laatst gerenderde scherpe paginabitmap."""

    content_signature: object = None
    dpi: float = 0.0
    image: Optional[Image.Image] = None
    source_generation: int = 0
    viewport_signature: object = None
    viewport_image: Optional[Image.Image] = None


def screen_render_dpi(
    paper_width_mm: float,
    paper_height_mm: float,
    zoom_px_per_mm: float,
    supersample: int = 2,
    max_source_pixels: int = 14_000_000,
) -> float:
    """Kies een scherpe, maar begrensde bronresolutie voor de volledige pagina."""

    supersample = max(1, int(supersample))
    desired_px_per_mm = max(0.1, float(zoom_px_per_mm)) * supersample
    area_mm2 = max(1.0, float(paper_width_mm) * float(paper_height_mm))
    max_px_per_mm = math.sqrt(max(1, int(max_source_pixels)) / area_mm2)
    px_per_mm = min(desired_px_per_mm, max_px_per_mm)
    # Onder 72 dpi levert geen zinvolle geheugenwinst op en maakt tekst onnodig zacht.
    return max(72.0, px_per_mm * 25.4)


def render_viewport_image(
    render_page: Callable[[int], Image.Image],
    cache: PageImageCache,
    *,
    content_signature: object,
    paper_width_mm: float,
    paper_height_mm: float,
    canvas_width_px: int,
    canvas_height_px: int,
    zoom_px_per_mm: float,
    pan_x_px: float,
    pan_y_px: float,
    background: str = "#eef2f7",
    sharp: bool = True,
    supersample: int = 2,
) -> Image.Image:
    """Render/crop een volledige pagina naar de actuele canvasviewport.

    Bij ``sharp=False`` wordt een bestaande bronbitmap opnieuw gebruikt. Dat is
    de snelle zoom-preview; na de settle-timer vraagt de caller opnieuw een
    scherpe bron op.
    """

    canvas_width_px = max(1, int(canvas_width_px))
    canvas_height_px = max(1, int(canvas_height_px))
    zoom_px_per_mm = max(0.01, float(zoom_px_per_mm))
    target_dpi = screen_render_dpi(
        paper_width_mm,
        paper_height_mm,
        zoom_px_per_mm,
        supersample=supersample if sharp else 1,
    )

    content_changed = cache.content_signature != content_signature or cache.image is None
    resolution_too_low = sharp and cache.dpi + 0.5 < target_dpi
    if content_changed or resolution_too_low:
        page = render_page(max(24, int(math.ceil(target_dpi))))
        if page.mode != "RGBA":
            page = page.convert("RGBA")
        cache.content_signature = content_signature
        cache.dpi = target_dpi
        cache.image = page
        cache.source_generation += 1
        cache.viewport_signature = None
        cache.viewport_image = None

    source = cache.image
    if source is None:  # Alleen mogelijk als een ongeldige render_page-callback niets oplevert.
        raise RuntimeError("De AA-paginarenderer leverde geen afbeelding op.")

    viewport_signature = (
        cache.source_generation,
        content_signature,
        canvas_width_px,
        canvas_height_px,
        round(zoom_px_per_mm, 6),
        round(float(pan_x_px), 3),
        round(float(pan_y_px), 3),
        background,
        bool(sharp),
    )
    if cache.viewport_signature == viewport_signature and cache.viewport_image is not None:
        return cache.viewport_image

    viewport = Image.new("RGBA", (canvas_width_px, canvas_height_px), background)
    display_w = max(1, int(round(paper_width_mm * zoom_px_per_mm)))
    display_h = max(1, int(round(paper_height_mm * zoom_px_per_mm)))
    page_left = int(round(pan_x_px))
    page_top = int(round(pan_y_px))

    dst_left = max(0, page_left)
    dst_top = max(0, page_top)
    dst_right = min(canvas_width_px, page_left + display_w)
    dst_bottom = min(canvas_height_px, page_top + display_h)
    if dst_right <= dst_left or dst_bottom <= dst_top:
        cache.viewport_signature = viewport_signature
        cache.viewport_image = viewport
        return viewport

    rel_left = (dst_left - page_left) / display_w
    rel_top = (dst_top - page_top) / display_h
    rel_right = (dst_right - page_left) / display_w
    rel_bottom = (dst_bottom - page_top) / display_h
    src_left = max(0, min(source.width, int(math.floor(rel_left * source.width))))
    src_top = max(0, min(source.height, int(math.floor(rel_top * source.height))))
    src_right = max(src_left + 1, min(source.width, int(math.ceil(rel_right * source.width))))
    src_bottom = max(src_top + 1, min(source.height, int(math.ceil(rel_bottom * source.height))))

    visible = source.crop((src_left, src_top, src_right, src_bottom))
    target_size = (dst_right - dst_left, dst_bottom - dst_top)
    if visible.size != target_size:
        resampling = getattr(Image, "Resampling", Image)
        visible = visible.resize(target_size, resampling.LANCZOS if sharp else resampling.BILINEAR)
    viewport.paste(visible, (dst_left, dst_top), visible)
    cache.viewport_signature = viewport_signature
    cache.viewport_image = viewport
    return viewport
