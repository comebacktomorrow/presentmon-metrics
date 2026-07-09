#!/usr/bin/env python3
"""Frame simulator: serves the kiosk-presentmon exporter's exact metrics with
realistic, MOVING data so the Grafana dashboard/PromQL can be validated without
a presenting Windows display. This is sample data, not real capture."""
import time, math
from http.server import BaseHTTPRequestHandler, HTTPServer

START = time.time()
APP = "signage-sim"

# Must match the exporter's bucket set in Program.cs (FrameTimeBuckets).
BUCKETS = [6, 8, 10, 12, 14, 16.7, 20, 25, 33.3, 50, 66.7, 100, 250, 500]

# Fraction of frames whose frame time falls in each (upper) bucket. ~60fps with
# a small stutter tail so p99 lands around ~25-33ms (visible, realistic).
DIST = {
    16.7: 0.90,   # the 60Hz mass
    20:   0.05,
    25:   0.02,
    33.3: 0.02,
    50:   0.008,
    100:  0.002,
}
FPS = 60.0
DROP_RATE = 0.004  # ~0.4% dropped

def cumulative(le, total):
    """Cumulative count of frames with frame time <= le."""
    frac = 0.0
    for b, f in DIST.items():
        if b <= le + 1e-9:
            frac += f
    return int(total * min(frac, 1.0))

def render():
    elapsed = time.time() - START
    # gentle sinusoid on fps so the FPS panel isn't a flat line
    fps = FPS - 3 * math.sin(elapsed / 20.0)
    presented = int(fps * elapsed)
    dropped = int(presented * DROP_RATE)
    displayed = presented - dropped

    lines = []
    lines.append("# TYPE presentmon_up gauge")
    lines.append(f'presentmon_up{{app="{APP}"}} 1')

    for name, tot in (("presentmon_frame_time_ms", presented),
                      ("presentmon_displayed_time_ms", displayed)):
        lines.append(f"# TYPE {name} histogram")
        for le in BUCKETS:
            c = cumulative(le, tot)
            le_str = repr(le) if le != int(le) else str(int(le))
            lines.append(f'{name}_bucket{{app="{APP}",le="{le_str}"}} {c}')
        lines.append(f'{name}_bucket{{app="{APP}",le="+Inf"}} {tot}')
        # approx sum: assume mean ~17.5ms
        lines.append(f'{name}_sum{{app="{APP}"}} {tot * 17.5:.1f}')
        lines.append(f'{name}_count{{app="{APP}"}} {tot}')

    for name, val in (("presentmon_frames_presented_total", presented),
                      ("presentmon_frames_displayed_total", displayed),
                      ("presentmon_frames_dropped_total", dropped)):
        lines.append(f"# TYPE {name} counter")
        lines.append(f'{name}{{app="{APP}"}} {val}')

    for name, val in (("presentmon_gpu_utilization_percent", 42 + 8 * math.sin(elapsed / 15.0)),
                      ("presentmon_gpu_temperature_celsius", 58 + 4 * math.sin(elapsed / 30.0))):
        lines.append(f"# TYPE {name} gauge")
        lines.append(f'{name}{{app="{APP}"}} {val:.1f}')

    return "\n".join(lines) + "\n"

class H(BaseHTTPRequestHandler):
    def do_GET(self):
        body = render().encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; version=0.0.4")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *a):  # quiet
        pass

if __name__ == "__main__":
    HTTPServer(("0.0.0.0", 9111), H).serve_forever()
