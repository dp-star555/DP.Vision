"""仅用于离线分析；SDK和控件均不需要Python。"""
import csv
import json
import statistics as stats
import sys
from pathlib import Path
sys.stdout.reconfigure(encoding="utf-8")
root = Path(sys.argv[1])
data = [json.loads(p.read_text(encoding="utf-8")) for p in root.glob("*.json") if p.name != "source-hashes.json"]
assert len(data) == 40 and all(d["geometryCountValidated"] and d["dwmStatus"] == 0 for d in data)
cases = ["image_4mp", "image_16mp", "region_100k", "xld_100k", "mixed"]
backends = ["halcon", "unified", "vision", "vision-lod"]
rows = []
for case in cases:
    for backend in backends:
        runs = [d for d in data if d["workload"] == case and d["backend"] == backend]
        assert len(runs) == 2
        for phase in runs[0]["metrics"]:
            name = phase["name"]
            values = [next(m for m in d["metrics"] if m["name"] == name) for d in runs]
            rows.append(dict(case=case, backend=backend, phase=name, median_ms=stats.mean(v["medianMs"] for v in values), p95_ms=max(v["p95Ms"] for v in values), cpu_ms=stats.mean(v["cpuMsPerOperation"] for v in values), gen2=stats.mean(v["gen2"] for v in values), peak_ws_mib=stats.mean(d["beforeGc"]["peakWorkingSetMiB"] for d in runs)))
with (root / "summary.csv").open("w", encoding="utf-8-sig", newline="") as f:
    writer = csv.DictWriter(f, fieldnames=rows[0].keys())
    writer.writeheader()
    writer.writerows(rows)
lines = ["# 同机40进程复测", "", "P50为两轮中位数的均值；P95取较差一轮。提交耗时不是物理屏幕FPS。单位ms。", ""]
for phase in ["cached_redraw_submit", "zoom_pan_submit", "scene_refresh_submit", "image_stream_submit"]:
    lines += ["## " + phase, "", "| 场景 | HALCON | 旧GDI | DP.Vision精确XLD | DP.Vision显示LOD |", "|---|---:|---:|---:|---:|"]
    for case in cases:
        cells = [f"{next(r for r in rows if r['case']==case and r['backend']==b and r['phase']==phase)['median_ms']:.2f}" for b in backends]
        lines.append("| " + " | ".join([case] + cells) + " |")
    lines.append("")
lines += ["## 输入换帧：峰值驻留内存与Gen2 GC", "", "| 场景 | 后端 | 全流程峰值WS均值 MiB | 换图阶段20次Gen2均值 |", "|---|---|---:|---:|"]
for r in rows:
    if r["phase"] == "image_stream_submit":
        lines.append(f"| {r['case']} | {r['backend']} | {r['peak_ws_mib']:.2f} | {r['gen2']:.1f} |")
(root / "tables.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
print("\n".join(lines))
