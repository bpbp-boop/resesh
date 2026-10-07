// Flattens libghostty-vt render state into a packed cell grid so the managed renderer
// crosses the native boundary once per frame instead of several times per cell.
#include <stdint.h>
#include <string.h>
#include <ghostty/vt.h>

#define RVT_BOLD 1u
#define RVT_ITALIC 2u
#define RVT_UNDERLINE 4u
#define RVT_STRIKE 8u
#define RVT_DEFAULT_BG 16u
#define RVT_FAINT 32u
#define RVT_INVISIBLE 64u

typedef struct {
  uint32_t cp;      // base codepoint, 0 when empty
  uint32_t fg;      // 0x00RRGGBB, resolved (inverse applied)
  uint32_t bg;      // 0x00RRGGBB, resolved (inverse applied)
  uint16_t flags;   // RVT_*
  uint8_t wide;     // GhosttyCellWide
  uint8_t glen;     // grapheme codepoint count (1 for plain cells)
} RvtCell;

typedef struct {
  uint32_t default_fg;
  uint32_t default_bg;
  uint16_t cursor_x;
  uint16_t cursor_y;
  uint8_t cursor_visible;
  uint8_t dirty;      // GhosttyRenderStateDirty before cleaning
  uint16_t dirty_rows;
} RvtFrameInfo;

static uint32_t pack(GhosttyColorRgb c) { return ((uint32_t)c.r << 16) | ((uint32_t)c.g << 8) | c.b; }

static uint32_t resolve(GhosttyStyleColor c, const GhosttyRenderStateColors* colors, uint32_t fallback) {
  switch (c.tag) {
    case GHOSTTY_STYLE_COLOR_RGB: return pack(c.value.rgb);
    case GHOSTTY_STYLE_COLOR_PALETTE: return pack(colors->palette[c.value.palette]);
    default: return fallback;
  }
}

typedef struct {
  GhosttyRenderStateRowIterator it;
  GhosttyRenderStateRowCells cells;
  GhosttyRenderStateColors colors;
} RvtReader;

__declspec(dllexport) RvtReader* rvt_reader_new(void) {
  static RvtReader readers[64];
  static int used = 0;
  if (used >= 64) return NULL;
  RvtReader* r = &readers[used++];
  memset(r, 0, sizeof *r);
  if (ghostty_render_state_row_iterator_new(NULL, &r->it) != GHOSTTY_SUCCESS) return NULL;
  if (ghostty_render_state_row_cells_new(NULL, &r->cells) != GHOSTTY_SUCCESS) return NULL;
  return r;
}

// Copies every dirty row of the render state into out[y * cols + x], sets dirty_rows[y] = 1
// for each copied row, then cleans the render state. Returns the number of dirty rows.
__declspec(dllexport) int rvt_read_frame(RvtReader* r, GhosttyRenderState rs, RvtCell* out,
                                         uint16_t cols, uint16_t rows, uint8_t* dirty_rows,
                                         RvtFrameInfo* info) {
  GhosttyRenderStateDirty dirty = GHOSTTY_RENDER_STATE_DIRTY_FALSE;
  ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_DIRTY, &dirty);
  info->dirty = (uint8_t)dirty;
  info->dirty_rows = 0;

  GhosttyRenderStateCursor cursor = GHOSTTY_INIT_SIZED(GhosttyRenderStateCursor);
  ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_CURSOR, &cursor);
  info->cursor_visible = cursor.visible && cursor.viewport_has_value;
  info->cursor_x = cursor.viewport_x;
  info->cursor_y = cursor.viewport_y;
  if (dirty == GHOSTTY_RENDER_STATE_DIRTY_FALSE) return 0;

  r->colors.size = sizeof r->colors;
  ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_COLORS, &r->colors);
  const uint32_t dfg = pack(r->colors.foreground), dbg = pack(r->colors.background);
  info->default_fg = dfg;
  info->default_bg = dbg;

  ghostty_render_state_get(rs, GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR, &r->it);
  uint16_t y = 0;
  int count = 0;
  while (ghostty_render_state_row_iterator_next_dirty(r->it, &y)) {
    if (y >= rows) continue;
    ghostty_render_state_row_get(r->it, GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &r->cells);
    RvtCell* row = out + (size_t)y * cols;
    uint16_t x = 0;
    while (x < cols && ghostty_render_state_row_cells_next(r->cells)) {
      RvtCell* c = &row[x++];
      GhosttyCell raw = 0;
      ghostty_render_state_row_cells_get(r->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_RAW, &raw);
      uint32_t cp = 0;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CODEPOINT, &cp);
      GhosttyCellWide wide = GHOSTTY_CELL_WIDE_NARROW;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_WIDE, &wide);
      bool styled = false;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_HAS_STYLING, &styled);
      uint32_t glen = cp ? 1 : 0;
      GhosttyCellContentTag tag = GHOSTTY_CELL_CONTENT_CODEPOINT;
      ghostty_cell_get(raw, GHOSTTY_CELL_DATA_CONTENT_TAG, &tag);
      if (tag == GHOSTTY_CELL_CONTENT_CODEPOINT_GRAPHEME)
        ghostty_render_state_row_cells_get(r->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_GRAPHEMES_LEN, &glen);

      uint32_t fg = dfg, bg = dbg;
      uint16_t flags = 0;
      bool bg_default = true;
      if (styled) {
        GhosttyStyle st = GHOSTTY_INIT_SIZED(GhosttyStyle);
        ghostty_render_state_row_cells_get(r->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_STYLE, &st);
        fg = resolve(st.fg_color, &r->colors, dfg);
        bg_default = st.bg_color.tag == GHOSTTY_STYLE_COLOR_NONE;
        bg = resolve(st.bg_color, &r->colors, dbg);
        if (st.bold) flags |= RVT_BOLD;
        if (st.italic) flags |= RVT_ITALIC;
        if (st.underline) flags |= RVT_UNDERLINE;
        if (st.strikethrough) flags |= RVT_STRIKE;
        if (st.faint) flags |= RVT_FAINT;
        if (st.invisible) flags |= RVT_INVISIBLE;
        if (st.inverse) { uint32_t t = fg; fg = bg; bg = t; bg_default = false; }
      } else if (tag == GHOSTTY_CELL_CONTENT_BG_COLOR_PALETTE || tag == GHOSTTY_CELL_CONTENT_BG_COLOR_RGB) {
        GhosttyColorRgb rgb;
        if (ghostty_render_state_row_cells_get(r->cells, GHOSTTY_RENDER_STATE_ROW_CELLS_DATA_BG_COLOR, &rgb) == GHOSTTY_SUCCESS) {
          bg = pack(rgb);
          bg_default = false;
        }
      }
      if (bg_default) flags |= RVT_DEFAULT_BG;
      c->cp = cp; c->fg = fg; c->bg = bg; c->flags = flags; c->wide = (uint8_t)wide;
      c->glen = (uint8_t)(glen > 255 ? 255 : glen);
    }
    for (; x < cols; x++) { RvtCell* c = &row[x]; memset(c, 0, sizeof *c); c->fg = dfg; c->bg = dbg; c->flags = RVT_DEFAULT_BG; }
    dirty_rows[y] = 1;
    count++;
  }
  info->dirty_rows = (uint16_t)count;
  ghostty_render_state_clean(rs);
  return count;
}
