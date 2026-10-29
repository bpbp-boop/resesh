# Summarizes RESESH_FRAME_STATS output: one row per run folder (frames-<pid>.csv + gpu-<pid>.csv).
#   python framestats.py <run dir> [<run dir> ...] [--all]
# Only frames while output was arriving count (first to last frame that parsed bytes, per
# surface), unless --all. Times are microseconds; "cpu" is the UI thread's time for the whole
# frame (read the grid, prepare, record, EndDraw, copy, Present); "gpu" is timestamp-query time
# from just before Direct2D's work to just after the canvas copy.
import csv
import glob
import os
import sys


def pct(values, p):
    if not values:
        return float('nan')
    values = sorted(values)
    k = (len(values) - 1) * p / 100
    lo = int(k)
    hi = min(lo + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def load(run, keep_all):
    frames, gpu = [], {}
    for path in glob.glob(os.path.join(run, 'frames-*.csv')):
        with open(path, newline='') as f:
            rows = [r for r in csv.DictReader(f) if r.get('total_us')]
        for r in rows:
            for k, v in r.items():
                r[k] = float(v)
            r['pid'] = path
        by_surface = {}
        for r in rows:
            by_surface.setdefault(r['surface'], []).append(r)
        for surface_rows in by_surface.values():
            active = [i for i, r in enumerate(surface_rows) if r['parsed_bytes'] > 0]
            if keep_all or not active:
                frames += surface_rows if keep_all else []
            else:
                frames += surface_rows[active[0]:active[-1] + 1]
        gpu_path = path.replace('frames-', 'gpu-')
        if os.path.exists(gpu_path):
            with open(gpu_path, newline='') as f:
                for r in csv.DictReader(f):
                    gpu[(path, float(r['surface']), float(r['frame']))] = float(r['gpu_us'])
    for r in frames:
        r['gpu_us'] = gpu.get((r['pid'], r['surface'], r['frame']))
    return frames


def summarize(run, keep_all):
    frames = load(run, keep_all)
    if not frames:
        return None
    col = lambda k: [r[k] for r in frames]
    gpu = [r['gpu_us'] for r in frames if r['gpu_us'] is not None]
    span_ms = (max(col('t_ms')) - min(col('t_ms'))) or 1
    intervals = [r['interval_ms'] for r in frames if r['interval_ms'] > 0]
    parse_us = sum(col('parse_us'))
    first = frames[0]
    return {
        'run': os.path.basename(os.path.normpath(run)),
        'grid': f"{int(first['cols'])}x{int(first['rows'])}",
        'buffer': f"{int(first['buf_w'])}x{int(first['buf_h'])}",
        'frames': len(frames),
        'fps': len(frames) / span_ms * 1000,
        'rows/frame': sum(col('rows_drawn')) / len(frames),
        'cpu p50': pct(col('total_us'), 50),
        'cpu p95': pct(col('total_us'), 95),
        'cpu p99': pct(col('total_us'), 99),
        'cpu max': max(col('total_us')),
        'read p50': pct(col('read_us'), 50),
        'prep p50': pct(col('prep_us'), 50),
        'scroll p50': pct(col('scroll_us'), 50) if 'scroll_us' in first else float('nan'),
        'input p50': pct(col('input_us'), 50) if 'input_us' in first else float('nan'),
        'record p50': pct(col('record_us'), 50),
        'record p95': pct(col('record_us'), 95),
        'enddraw p50': pct(col('enddraw_us'), 50),
        'copy p50': pct(col('copy_us'), 50),
        'present p50': pct(col('present_us'), 50),
        'present p95': pct(col('present_us'), 95),
        'gpu p50': pct(gpu, 50),
        'gpu p95': pct(gpu, 95),
        'gaps>2x': sum(1 for i in intervals if i > 2 * pct(intervals, 50)),
        'gaps>50ms': sum(1 for i in intervals if i > 50),
        'annot max': max(col('annotations_us')) if 'annotations_us' in first else float('nan'),
        'annot ms/s': sum(col('annotations_us')) / span_ms if 'annotations_us' in first else float('nan'),
        'keyframe max': max(col('keyframe_us')) if 'keyframe_us' in first else float('nan'),
        'read max': max(col('read_us')),
        'gc ms/s': sum(col('gc_pause_us')) / span_ms if 'gc_pause_us' in first else float('nan'),
        'gc2': sum(col('gc2')) if 'gc2' in first else float('nan'),
        'alloc KB/frame': sum(col('alloc_kb')) / len(frames) if 'alloc_kb' in first else float('nan'),
        'parse MB/s': sum(col('parsed_bytes')) / parse_us if parse_us else float('nan'),
        'out MB/s': sum(col('parsed_bytes')) / span_ms / 1000,
    }


def main():
    keep_all = '--all' in sys.argv
    runs = [a for a in sys.argv[1:] if a != '--all']
    results = [s for s in (summarize(r, keep_all) for r in runs) if s]
    if not results:
        print('no frames found')
        return
    keys = list(results[0])
    print('| ' + ' | '.join(keys) + ' |')
    print('|' + '|'.join('---' if k in ('run', 'grid', 'buffer') else '---:' for k in keys) + '|')
    for s in results:
        cells = []
        for k in keys:
            v = s[k]
            cells.append(v if isinstance(v, str) else f'{v:.0f}' if abs(v) >= 100 or isinstance(v, int) else f'{v:.1f}')
        print('| ' + ' | '.join(cells) + ' |')


if __name__ == '__main__':
    main()
